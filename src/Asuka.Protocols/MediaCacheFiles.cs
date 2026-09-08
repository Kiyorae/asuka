using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Asuka.Protocols;

/// <summary>Deletes recognized derived files by a validated handle, never by a path that can be swapped after inspection.</summary>
internal static class MediaCacheFiles
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparseAttribute = 0x400;

    internal static MediaCacheCleanupResult Clean(string audioDirectory, string previewDirectory, CancellationToken token,
        Action<string>? directoryReadyForEnumeration = null)
    {
        token.ThrowIfCancellationRequested();
        // The desktop app is Windows-only. On other hosts, do not replace the
        // handle-based safety boundary with a racy path-check/delete fallback.
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe media cache cleanup requires Windows.");
        var result = new Counts();
        var assetDirectory = Path.GetDirectoryName(audioDirectory)!;
        using var assets = OpenDirectoryChain(assetDirectory, out var assetsMissing);
        if (assets is null) return new MediaCacheCleanupResult(0, 0, assetsMissing ? 0 : 1);
        // Shell exports share a temp root across the demo and normal profiles.
        // A profile may clean only copies of originals it currently owns.
        var ownedIds = Enumerate(assetDirectory, result)
            .Where(path => IsAssetId(Path.GetFileName(path)) && IsRegularFile(path, assets.CanonicalPath!))
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        CleanDirectory(audioDirectory, IsAudioCacheName, result, directoryReadyForEnumeration, token);
        using var preview = OpenDirectoryChain(previewDirectory, out var previewMissing);
        if (preview is null)
        {
            if (!previewMissing) result.Skipped++;
            return result.Result;
        }
        foreach (var entry in Enumerate(previewDirectory, result))
        {
            token.ThrowIfCancellationRequested();
            if (!ownedIds.Contains(Path.GetFileName(entry))) { result.Skipped++; continue; }
            // Exactly one level of known hash directories. Unknown directories,
            // junctions and files are never traversed recursively.
            CleanDirectory(entry, name => name.StartsWith("Asuka-", StringComparison.Ordinal) && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase),
                result, directoryReadyForEnumeration, token);
        }
        return result.Result;
    }

    private static void CleanDirectory(string path, Func<string, bool> acceptsName, Counts result,
        Action<string>? directoryReadyForEnumeration, CancellationToken token)
    {
        using var directory = OpenDirectoryChain(path, out var missing);
        if (directory is null)
        {
            if (!missing) result.Skipped++;
            return;
        }
        directoryReadyForEnumeration?.Invoke(path);
        foreach (var file in Enumerate(path, result))
        {
            token.ThrowIfCancellationRequested();
            if (!acceptsName(Path.GetFileName(file))) { result.Skipped++; continue; }
            if (TryDeleteFile(file, directory.CanonicalPath!, out var bytes))
            {
                result.Deleted++;
                result.Bytes += bytes;
            }
            else result.Skipped++;
        }
    }

    private static string[] Enumerate(string path, Counts result)
    {
        try { return Directory.GetFileSystemEntries(path); }
        catch (IOException) { result.Skipped++; return []; }
        catch (UnauthorizedAccessException) { result.Skipped++; return []; }
    }

    private static bool IsAudioCacheName(string name) => name.StartsWith("silk-v1-", StringComparison.Ordinal)
        && name.EndsWith(".wav", StringComparison.Ordinal) && IsAssetId(name[8..^4]);

    private static bool IsAssetId(string name) => name.Length == 64
        && name.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsRegularFile(string path, string expectedParent)
    {
        using var file = CreateFile(path, ReadAttributes, (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero,
            OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (file.IsInvalid) return false;
        var information = new byte[52];
        return GetFileInformationByHandle(file, information)
            && (BinaryPrimitives.ReadUInt32LittleEndian(information) & (DirectoryAttribute | ReparseAttribute)) == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(40)) == 1
            && TryGetCanonicalPath(file, out var canonical) && ParentMatches(canonical, expectedParent);
    }

    private static bool TryDeleteFile(string path, string expectedParent, out long bytes)
    {
        bytes = 0;
        using var file = CreateFile(path, DeleteAccess | ReadAttributes, 0, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (file.IsInvalid) return false; // Includes files open in a player, shell or upload.
        var information = new byte[52];
        if (!GetFileInformationByHandle(file, information)) return false;
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(information);
        if ((attributes & (ReparseAttribute | DirectoryAttribute)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(40)) != 1) return false;
        // A directory can be turned into a reparse point without being renamed.
        // Therefore ancestor no-delete handles alone are insufficient: check the
        // file handle's resolved parent against the original directory handle.
        if (!TryGetCanonicalPath(file, out var canonical) || !ParentMatches(canonical, expectedParent)) return false;
        var length = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(32)) << 32)
            | BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(36));
        if (length > long.MaxValue) return false;
        // FILE_DISPOSITION_INFO contains a one-byte BOOLEAN. Removal applies to
        // this exact handle, so renaming a pathname cannot redirect deletion.
        if (!SetFileInformationByHandle(file, 4, [1], 1)) return false;
        bytes = (long)length;
        return true;
    }

    private static DirectoryChain? OpenDirectoryChain(string path, out bool missing)
    {
        missing = false;
        DirectoryChain? chain = new();
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            // Never open a network/device namespace during cleanup.
            if (root is null || root.Length != 3 || root[1] != ':' || !char.IsAsciiLetter(root[0])) return null;
            var components = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            foreach (var component in new[] { string.Empty }.Concat(components))
            {
                if (component.Contains(':')) return null;
                if (component.Length != 0) current = Path.Combine(current, component);
                var handle = CreateFile(current, ReadAttributes, (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero,
                    OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    missing = error is 2 or 3;
                    return null;
                }
                chain.Handles.Add(handle);
                var information = new byte[52];
                if (!GetFileInformationByHandle(handle, information)) return null;
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(information);
                if ((attributes & ReparseAttribute) != 0 || (attributes & DirectoryAttribute) == 0) return null;
                if (!TryGetCanonicalPath(handle, out var canonical)
                    || (chain.CanonicalPath is { } parent && !ParentMatches(canonical, parent))) return null;
                chain.CanonicalPath = canonical.TrimEnd('\\');
                // Every ancestor remains open without FILE_SHARE_DELETE to block
                // renames; canonical parent checks also cover in-place reparse changes.
            }
            var result = chain;
            chain = null;
            return result;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        finally { chain?.Dispose(); }
    }

    private static bool ParentMatches(string path, string expectedParent)
    {
        var separator = path.LastIndexOf('\\');
        // Both names come from normalized handles, not caller spelling. NTFS
        // can mark an individual directory case-sensitive; folding case here
        // would merge distinct cache and outside directories.
        return separator > 0 && path[..separator].Equals(expectedParent, StringComparison.Ordinal);
    }

    private static bool TryGetCanonicalPath(SafeFileHandle handle, out string path)
    {
        // VOLUME_NAME_GUID avoids drive-letter aliases and rejects remote/unknown
        // namespaces. Windows extended paths are limited to 32,767 characters.
        var buffer = new char[512];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 1);
        path = string.Empty;
        if (length >= buffer.Length && length < 32768)
        {
            buffer = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 1);
        }
        if (length == 0 || length >= buffer.Length) return false;
        var result = new string(buffer, 0, (int)length);
        const string prefix = @"\\?\Volume{";
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var close = result.IndexOf('}', prefix.Length);
        if (close < 0 || close + 1 >= result.Length || result[close + 1] != '\\'
            || !Guid.TryParse(result.AsSpan(prefix.Length, close - prefix.Length), out _)) return false;
        path = result.TrimEnd('\\');
        return true;
    }

    private sealed class DirectoryChain : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        internal string? CanonicalPath { get; set; }
        public void Dispose()
        {
            foreach (var handle in Handles) handle.Dispose();
        }
    }

    private sealed class Counts
    {
        internal int Deleted;
        internal long Bytes;
        internal int Skipped;
        internal MediaCacheCleanupResult Result => new(Deleted, Bytes, Skipped);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        [In] byte[] information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);
}
