namespace Asuka.Core;

public sealed partial class PlatformService
{
    /// <summary>Notification history belongs to the account and remains readable after group departure or deletion.</summary>
    public Task<GroupNotificationPage> GetGroupNotificationsAsync(string selfId, bool isFiltered = false,
        long? startSequence = null, int limit = 20, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (limit <= 0 || startSequence < 0)
                throw InvalidParameter("limit must be positive and start_notification_seq must be nonnegative");
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            return await Store.GetGroupNotificationPageAsync(selfId, isFiltered, startSequence, limit, token).ConfigureAwait(false);
        }, cancellationToken);
}
