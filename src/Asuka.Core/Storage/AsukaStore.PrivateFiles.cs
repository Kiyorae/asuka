namespace Asuka.Core;

public sealed partial class AsukaStore
{
    /// <summary>Private share history, including expired shares, newest first with a stable ID tie-breaker.</summary>
    public Task<IReadOnlyList<SharedFile>> GetPrivateFileListingAsync(
        string userId, string peerId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            var files = await ReadSharedFilesAsync(
                "f.group_id IS NULL AND ((f.recipient_id=$peer AND f.uploader_id=$id) " +
                "OR (f.recipient_id=$id AND f.uploader_id=$peer))",
                peerId, userId, null, token).ConfigureAwait(false);
            // The common reader orders by upload time and ID ascending.
            return (IReadOnlyList<SharedFile>)files.Reverse().ToArray();
        }, cancellationToken);
}
