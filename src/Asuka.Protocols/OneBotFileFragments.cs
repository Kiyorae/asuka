using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol : IDisposable
{
    private readonly object _fileTransfersLock = new();
    private OneBotFileTransfers? _fileTransfers;
    private bool _filesDisposed;

    private OneBotFileTransfers FileTransfers
    {
        get
        {
            lock (_fileTransfersLock)
            {
                ObjectDisposedException.ThrowIf(_filesDisposed, this);
                return _fileTransfers ??= new OneBotFileTransfers(_media!, _store);
            }
        }
    }

    private Task<ProtocolReply> UploadFileFragmentedAsync(ProtocolCall call, CancellationToken cancellationToken) =>
        _media is null ? Task.FromResult(Unsupported("upload_file_fragmented"))
            : FileTransfers.UploadAsync(call.Parameters, cancellationToken);

    private Task<ProtocolReply> GetFileFragmentedAsync(ProtocolCall call, CancellationToken cancellationToken) =>
        _media is null ? Task.FromResult(Unsupported("get_file_fragmented"))
            : FileTransfers.DownloadAsync(call.Parameters, cancellationToken);

    public void Dispose()
    {
        DisposeScheduledActions();
        lock (_fileTransfersLock)
        {
            _filesDisposed = true;
            _fileTransfers?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal void ClearPendingFileTransfers()
    {
        lock (_fileTransfersLock)
        {
            _fileTransfers?.Dispose();
            _fileTransfers = null;
        }
    }
}

/// <summary>
/// Protocol-instance-owned staging. Temporary IDs never resolve through AssetStore;
/// a completed upload is published only after complete coverage and SHA-256 validation.
/// </summary>
internal sealed class OneBotFileTransfers : IDisposable
{
    internal const int MaximumChunkByteCount = 1024 * 1024;
    internal const int MaximumPendingUploadCount = 16;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private const long MaximumPendingBytes = 128L * 1024 * 1024;
    private const int MaximumRanges = 4096;
    private static readonly ConditionalWeakTable<AssetStore, SharedBudget> SharedBudgets = new();
    // Bound transient hash/read/write buffers even across different protocol instances.
    private static readonly SemaphoreSlim WorkSlots = new(4, 4);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PendingUpload> _uploads = new(StringComparer.Ordinal);
    private readonly MediaService _media;
    private readonly AsukaStore _store;
    private readonly TimeProvider _time;
    private readonly SharedBudget _budget;
    private readonly ITimer? _expiryTimer;
    private long _reservedBytes;
    private bool _disposed;

    internal OneBotFileTransfers(MediaService media, AsukaStore store, TimeProvider? timeProvider = null)
    {
        _media = media;
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _budget = SharedBudgets.GetValue(media.Assets, static _ => new SharedBudget());
        _expiryTimer = _time.CreateTimer(static state =>
        {
            if (((WeakReference<OneBotFileTransfers>)state!).TryGetTarget(out var owner))
            {
                owner.SweepExpired();
            }
        }, new WeakReference<OneBotFileTransfers>(this), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    ~OneBotFileTransfers() => DisposeCore();

    internal Task<ProtocolReply> UploadAsync(JsonObject parameters, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () => RequiredString(parameters, "stage") switch
        {
            "prepare" => Prepare(parameters),
            "transfer" => await TransferAsync(parameters, cancellationToken).ConfigureAwait(false),
            "finish" => await FinishAsync(parameters, cancellationToken).ConfigureAwait(false),
            _ => new ProtocolReply(10004, Message: "Unsupported upload stage"),
        }, cancellationToken);

    internal Task<ProtocolReply> DownloadAsync(JsonObject parameters, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var stage = RequiredString(parameters, "stage");
            var id = RequiredString(parameters, "file_id");
            if (stage is not ("prepare" or "transfer"))
            {
                return new ProtocolReply(10004, Message: "Unsupported download stage");
            }

            var offset = stage == "transfer" ? RequiredNonnegativeInteger(parameters, "offset") : 0;
            var size = stage == "transfer" ? RequiredNonnegativeInteger(parameters, "size") : 0;
            if (size > MaximumChunkByteCount)
            {
                return InvalidFile("A fragment cannot exceed 1 MiB");
            }

            var asset = await _media.ResolveIdAsync(id, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false);
            if (asset is null)
            {
                return FileFailure("File not found");
            }

            await using var input = new FileStream(_media.Assets.LocationOf(asset.Id), FileMode.Open,
                FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (stage == "prepare")
            {
                return ProtocolReply.Success(new JsonObject
                {
                    ["name"] = asset.Name,
                    ["total_size"] = input.Length,
                    ["sha256"] = asset.Id,
                });
            }

            if (offset > input.Length || size > input.Length - offset)
            {
                return InvalidFile("The requested fragment is outside the file");
            }

            var data = new byte[checked((int)size)];
            input.Position = offset;
            await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return ProtocolReply.Success(new JsonObject { ["data"] = Convert.ToBase64String(data) });
        }, cancellationToken);

    private async Task<ProtocolReply> ExecuteAsync(Func<Task<ProtocolReply>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpired();
            await WorkSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (ArgumentException error)
            {
                return InvalidFile(error.Message);
            }
            catch (HttpRequestException error)
            {
                return new ProtocolReply(33000, Message: error.Message);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new ProtocolReply(32000, Message: error.Message);
            }
            finally
            {
                WorkSlots.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProtocolReply Prepare(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        var size = RequiredNonnegativeInteger(parameters, "total_size");
        if (size > _media.Assets.MaximumByteCount)
        {
            return InvalidFile("File exceeds the asset size limit");
        }

        if (name.Length > 4096)
        {
            return InvalidFile("File name is too long");
        }

        if (_uploads.Count >= MaximumPendingUploadCount || size > MaximumPendingBytes - _reservedBytes
            || !_budget.TryReserve(size))
        {
            return FileFailure("Pending file upload limit reached; finish existing uploads or wait for expiry");
        }

        try
        {
            var id = $"upload-{Guid.NewGuid():N}";
            var path = Path.Combine(_media.Assets.DirectoryPath, $".onebot-fragment-{Guid.NewGuid():N}.tmp");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            try
            {
                stream.SetLength(size);
                _uploads.Add(id, new PendingUpload(name, size, stream, _time.GetUtcNow()));
                _reservedBytes += size;
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            return ProtocolReply.Success(new JsonObject { ["file_id"] = id });
        }
        catch
        {
            _budget.Release(size);
            throw;
        }
    }

    private async Task<ProtocolReply> TransferAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "file_id");
        var offset = RequiredNonnegativeInteger(parameters, "offset");
        var encoded = RequiredString(parameters, "data", allowEmpty: true);
        if (encoded.Length > (MaximumChunkByteCount + 2L) / 3 * 4)
        {
            return InvalidFile("A fragment cannot exceed 1 MiB");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return InvalidFile("data must be Base64 encoded");
        }

        if (data.Length > MaximumChunkByteCount)
        {
            return InvalidFile("A fragment cannot exceed 1 MiB");
        }

        if (!_uploads.TryGetValue(id, out var upload))
        {
            return FileFailure("Pending upload not found or expired");
        }

        if (offset > upload.Size || data.LongLength > upload.Size - offset)
        {
            return InvalidFile("The fragment is outside the prepared file");
        }

        var ranges = MergeRanges(upload.Ranges, offset, offset + data.Length);
        if (ranges.Count > MaximumRanges)
        {
            return FileFailure("Too many disjoint fragments; fill the existing gaps first");
        }

        upload.Stream.Position = offset;
        await upload.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        upload.Ranges = ranges;
        upload.LastActivity = _time.GetUtcNow();
        return new ProtocolReply();
    }

    private async Task<ProtocolReply> FinishAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "file_id");
        var checksum = RequiredString(parameters, "sha256");
        if (!IsLowercaseSha256(checksum))
        {
            return InvalidFile("sha256 must be a lowercase SHA256 checksum");
        }

        if (!_uploads.TryGetValue(id, out var upload))
        {
            return FileFailure("Pending upload not found or expired");
        }

        if (upload.Size != 0 && (upload.Ranges.Count != 1 || upload.Ranges[0] != (0L, upload.Size)))
        {
            return FileFailure("The file is incomplete");
        }

        upload.Stream.Position = 0;
        var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(upload.Stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(checksum, actualHash, StringComparison.Ordinal))
        {
            return FileFailure("File SHA256 checksum does not match");
        }

        upload.Stream.Position = 0;
        var asset = await _media.Assets.StoreAsync(upload.Stream, upload.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        Remove(id, upload);
        return ProtocolReply.Success(new JsonObject { ["file_id"] = asset.Id });
    }

    private static List<(long Start, long End)> MergeRanges(List<(long Start, long End)> existing, long start, long end)
    {
        if (start == end)
        {
            return existing;
        }

        var ranges = new List<(long Start, long End)>(existing.Count + 1);
        var inserted = false;
        foreach (var range in existing)
        {
            if (range.End < start)
            {
                ranges.Add(range);
            }
            else if (range.Start > end)
            {
                if (!inserted)
                {
                    ranges.Add((start, end));
                    inserted = true;
                }

                ranges.Add(range);
            }
            else
            {
                start = Math.Min(start, range.Start);
                end = Math.Max(end, range.End);
            }
        }

        if (!inserted)
        {
            ranges.Add((start, end));
        }

        return ranges;
    }

    internal void SweepExpired()
    {
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (!_disposed)
            {
                RemoveExpired();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RemoveExpired()
    {
        var cutoff = _time.GetUtcNow() - IdleTimeout;
        foreach (var (id, upload) in _uploads.ToArray())
        {
            if (upload.LastActivity <= cutoff)
            {
                Remove(id, upload);
            }
        }
    }

    private void Remove(string id, PendingUpload upload)
    {
        _uploads.Remove(id);
        _reservedBytes -= upload.Size;
        _budget.Release(upload.Size);
        try
        {
            upload.Stream.Dispose();
        }
        catch (IOException)
        {
            // This staging file is being discarded. FileStream closes its
            // DeleteOnClose handle even if flushing abandoned data fails;
            // cleanup must continue for the remaining uploads and timer callback.
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _expiryTimer?.Dispose();
            foreach (var (id, upload) in _uploads.ToArray())
            {
                Remove(id, upload);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string RequiredString(JsonObject parameters, string key, bool allowEmpty = false) =>
        parameters[key] is JsonValue value && value.TryGetValue<string>(out var text) && (allowEmpty || !string.IsNullOrEmpty(text))
            ? text : throw new ArgumentException($"Missing or invalid string parameter: {key}");

    private static long RequiredNonnegativeInteger(JsonObject parameters, string key) =>
        parameters[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw new ArgumentException($"{key} must be a nonnegative int64");

    internal static bool IsLowercaseSha256(string checksum) => checksum.Length == 64
        && checksum.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ProtocolReply InvalidFile(string message) => new(10003, Message: message);
    private static ProtocolReply FileFailure(string message) => new(35000, Message: message);

    private sealed class PendingUpload(string name, long size, FileStream stream, DateTimeOffset lastActivity)
    {
        internal string Name { get; } = name;
        internal long Size { get; } = size;
        internal FileStream Stream { get; } = stream;
        internal DateTimeOffset LastActivity { get; set; } = lastActivity;
        internal List<(long Start, long End)> Ranges { get; set; } = [];
    }

    private sealed class SharedBudget
    {
        private readonly object _sync = new();
        private int _count;
        private long _bytes;

        internal bool TryReserve(long size)
        {
            lock (_sync)
            {
                if (_count >= 32 || size > 256L * 1024 * 1024 - _bytes)
                {
                    return false;
                }

                _count++;
                _bytes += size;
                return true;
            }
        }

        internal void Release(long size)
        {
            lock (_sync)
            {
                _count--;
                _bytes -= size;
            }
        }
    }
}
