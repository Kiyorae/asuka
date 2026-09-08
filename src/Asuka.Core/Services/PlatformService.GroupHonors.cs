namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task<GroupHonorInfo> GetGroupHonorInfoAsync(string groupId, string accountId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.GetGroupHonorInfoAsync(groupId, accountId, token), cancellationToken);

    /// <summary>Sets simulator honor state. OneBot exposes a query and notices, not an award action.</summary>
    public Task<bool> SetGroupHonorAsync(string groupId, string userId, GroupHonorType type, string operatorId,
        string description = "", int dayCount = 1, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ValidateGroupHonor(type, description, dayCount);
            var result = await Store.SetGroupHonorAsync(groupId, userId, type, operatorId, description, dayCount,
                RegisteredBotIds(), token).ConfigureAwait(false);
            if (result.Granted && type is GroupHonorType.Talkative or GroupHonorType.Performer or GroupHonorType.Emotion)
            {
                // Platform awards have no bot action origin, including awards to the bot itself.
                var change = new GroupHonorChangedEvent(new GroupHonorChange(groupId, userId, type));
                foreach (var selfId in result.Recipients) Publish(new DomainEvent(selfId, change));
            }
            return result.Changed;
        }, cancellationToken);

    public Task<bool> RemoveGroupHonorAsync(string groupId, string userId, GroupHonorType type, string operatorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(token =>
        {
            ValidateGroupHonor(type, string.Empty, 1);
            return Store.RemoveGroupHonorAsync(groupId, userId, type, operatorId, token);
        }, cancellationToken);

    /// <summary>Publishes a simulated lucky-king result; this does not send a payment or red packet.</summary>
    public Task PublishGroupLuckyKingAsync(string groupId, string senderId, string targetId, string operatorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var recipients = await Store.GetGroupLuckyKingRecipientsAsync(groupId, senderId, targetId, operatorId,
                RegisteredBotIds(), token).ConfigureAwait(false);
            var result = new GroupLuckyKingEvent(new GroupLuckyKing(groupId, senderId, targetId));
            foreach (var selfId in recipients) Publish(new DomainEvent(selfId, result));
        }, cancellationToken);

    private static void ValidateGroupHonor(GroupHonorType type, string description, int dayCount)
    {
        if (!Enum.IsDefined(type)) throw InvalidParameter("Unknown group honor type");
        if (description is null || description.Length > 1024) throw InvalidParameter("Honor description must not exceed 1024 characters");
        if (dayCount <= 0) throw InvalidParameter("Honor day count must be a positive integer");
    }
}
