using Asuka.Core;

namespace Asuka.Protocols;

public sealed record MediaCacheCleanupResult(int DeletedFiles, long DeletedBytes, int SkippedFiles);

public sealed class PlayableAudioSource(string path, FileStream stream) : IDisposable
{
    public string Path { get; } = path;
    public FileStream Stream { get; } = stream;
    public void Dispose() => Stream.Dispose();
}

public sealed partial class MediaService
{
    internal string AttachmentPreviewCacheDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "Asuka", "attachment-preview");
    internal Action<string>? CacheDirectoryReadyForEnumeration { get; init; }

    /// <summary>Guards a preview copy and shell launch against cleanup. Do not nest this with audio conversion.</summary>
    public static Task<IDisposable> AcquireCacheLeaseAsync(CancellationToken cancellationToken = default) =>
        SilkAudioConverter.AcquireCacheLeaseAsync(cancellationToken);

    /// <summary>Opens playback before releasing cache coordination; the owned stream prevents deletion while in use.</summary>
    public async Task<PlayableAudioSource> OpenPlayableAudioAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var reference = await GetReferenceAsync(asset, preferLocalPath: true, cancellationToken).ConfigureAwait(false);
        var resolved = reference.ResolvedAsset ?? asset;
        var path = Assets.LocationOf(resolved.Id);
        var header = await ReadHeaderAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (SilkAudioConverter.IsSilk(header))
            return await SilkAudioConverter.OpenWaveAsync(path, Path.Combine(Assets.DirectoryPath, ".audio-cache"), cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PlayableAudioSource(path, File.OpenRead(path));
    }

    /// <summary>Removes only rebuildable audio and shell-preview copies. Attachment originals and upload fragments are durable.</summary>
    public async Task<MediaCacheCleanupResult> CleanCacheAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await SilkAudioConverter.AcquireCleanupLeaseAsync(cancellationToken).ConfigureAwait(false);
        // Native enumeration and deletion can take time on a busy cache. Keep it off the UI dispatcher.
        return await Task.Run(() => MediaCacheFiles.Clean(
            Path.Combine(Assets.DirectoryPath, ".audio-cache"), AttachmentPreviewCacheDirectory, cancellationToken,
            CacheDirectoryReadyForEnumeration),
            cancellationToken).ConfigureAwait(false);
    }
}
