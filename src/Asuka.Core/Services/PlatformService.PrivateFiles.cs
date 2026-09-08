namespace Asuka.Core;

public sealed partial class PlatformService
{
    /// <summary>Lists local private share history even after the participants remove their friendship.</summary>
    public Task<IReadOnlyList<SharedFile>> GetPrivateFileListingAsync(
        string userId, string peerId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (userId == peerId) throw InvalidParameter("Private file history requires two distinct participants");
            _ = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
            _ = await Store.GetUserAsync(peerId, token).ConfigureAwait(false) ?? throw UserNotFound(peerId);
            return await Store.GetPrivateFileListingAsync(userId, peerId, token).ConfigureAwait(false);
        }, cancellationToken);
}
