namespace Asuka.Core;

public sealed partial class AssetStore
{
    /// <summary>Opens only a content-addressed cached asset, without source rehydration.</summary>
    public Task<FileStream> OpenReadAsync(string assetId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FileStream(
            LocationOf(assetId), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
}
