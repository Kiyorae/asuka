using System.Text.Json;
using Asuka.Protocols;

namespace Asuka.App;

/// <summary>The small launch-mode preference lives separately from normal and showcase account data.</summary>
internal sealed class DemoModeSettingsStore
{
    private const int MaximumBytes = 4096;
    private readonly string _path;

    public DemoModeSettingsStore(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("The demo settings path must be absolute.", nameof(absolutePath));
        _path = Path.GetFullPath(absolutePath);
    }

    public async Task<DemoLaunchOptions?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                MaximumBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream)
        {
            if (stream.Length > MaximumBytes) throw InvalidSettings();
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes) throw InvalidSettings();
            try
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw InvalidSettings();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name is not ("Enabled" or "Protocol" or "IntervalSeconds") || !names.Add(property.Name))
                        throw InvalidSettings();
                }
                if (names.Count != 3
                    || !root.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !root.TryGetProperty("Protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("IntervalSeconds", out var interval) || interval.ValueKind != JsonValueKind.Number
                    || !interval.TryGetDouble(out var seconds)) throw InvalidSettings();
                var kind = protocol.GetString() switch
                {
                    nameof(ProtocolKind.Milky) => ProtocolKind.Milky,
                    nameof(ProtocolKind.OneBotV11) => ProtocolKind.OneBotV11,
                    nameof(ProtocolKind.OneBotV12) => ProtocolKind.OneBotV12,
                    _ => throw InvalidSettings(),
                };
                var result = new DemoLaunchOptions(enabled.GetBoolean(), kind, seconds);
                try { result.Validate(); }
                catch (ArgumentException) { throw InvalidSettings(); }
                return result;
            }
            catch (JsonException exception) { throw new InvalidDataException("The demo settings file is invalid.", exception); }
        }
    }

    public async Task SaveAsync(DemoLaunchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            options.Enabled,
            Protocol = options.Protocol.ToString(),
            options.IntervalSeconds,
        });
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".demo-mode-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                MaximumBytes, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            // Cancellation is checked before the single atomic commit. Once the
            // rename succeeds, report success rather than a misleading cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static InvalidDataException InvalidSettings() => new("The demo settings file is invalid or exceeds 4096 bytes.");
}
