namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task SetGroupAvatarAsync(string groupId, string operatorId, string avatarUri, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentAdministratorAsync(groupId, operatorId, token).ConfigureAwait(false);
            if (!Uri.TryCreate(avatarUri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeFile && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw InvalidParameter("The group avatar must be an absolute image URI");
            var group = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
            if (group.Avatar != uri.AbsoluteUri)
                await Store.SaveAsync(group with { Avatar = uri.AbsoluteUri }, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<GroupAnnouncement>> GetGroupAnnouncementsAsync(
        string groupId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentMemberAsync(groupId, userId, token).ConfigureAwait(false);
            return await Store.GetGroupAnnouncementsAsync(groupId, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GroupAnnouncement> SendGroupAnnouncementAsync(
        string groupId, string operatorId, string content, Asset? image = null, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentAdministratorAsync(groupId, operatorId, token).ConfigureAwait(false);
            if (content is null || (string.IsNullOrWhiteSpace(content) && image is null))
                throw InvalidParameter("An announcement requires text or an image");
            if (image is not null)
            {
                ValidateSharedAsset(image);
                await Store.SaveAsync(image, token).ConfigureAwait(false);
            }
            var announcement = new GroupAnnouncement(Guid.NewGuid().ToString("N"), groupId, operatorId, DateTimeOffset.UtcNow, content, image);
            await Store.SaveGroupAnnouncementAsync(announcement, token).ConfigureAwait(false);
            return announcement;
        }, cancellationToken);

    public Task DeleteGroupAnnouncementAsync(string groupId, string announcementId, string operatorId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentAdministratorAsync(groupId, operatorId, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(announcementId)) throw InvalidParameter("announcement_id must not be empty");
            if (!await Store.DeleteGroupAnnouncementAsync(groupId, announcementId, token).ConfigureAwait(false))
                throw new PlatformException(PlatformError.MessageNotFound, "Group announcement not found") { GroupId = groupId, ResourceId = announcementId };
        }, cancellationToken);

    public Task<GroupEssencePage> GetGroupEssenceMessagesAsync(string groupId, string userId, int pageIndex, int pageSize,
        string? selfId = null, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentMemberAsync(groupId, userId, token).ConfigureAwait(false);
            if (pageIndex < 0 || pageSize <= 0) throw InvalidParameter("page_index must be nonnegative and page_size must be positive");
            var accountId = selfId ?? userId;
            await RequireGroupContentMemberAsync(groupId, accountId, token).ConfigureAwait(false);
            return await Store.GetGroupEssenceMessagesAsync(groupId, accountId, pageIndex, pageSize, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetGroupEssenceMessageAsync(string groupId, long messageSequence, string selfId, string operatorId,
        bool isSet = true, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireGroupContentAdministratorAsync(groupId, operatorId, token).ConfigureAwait(false);
            await RequireGroupContentMemberAsync(groupId, selfId, token).ConfigureAwait(false);
            if (messageSequence <= 0) throw InvalidParameter("message_seq must be positive");
            var message = await Store.GetMessageAsync(ChatScene.Group, groupId, messageSequence, selfId, token).ConfigureAwait(false);
            if (message is null || message.IsRecalled) throw MessageNotFound(messageSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (message.Anonymous is not null) throw InvalidParameter("Anonymous messages cannot be marked as essence");
            var senderName = await GroupContentDisplayNameAsync(groupId, message.SenderId, token).ConfigureAwait(false);
            var operatorName = await GroupContentDisplayNameAsync(groupId, operatorId, token).ConfigureAwait(false);
            var essence = new GroupEssenceMessage(message, senderName, operatorId, operatorName, DateTimeOffset.UtcNow);
            if (await Store.SetGroupEssenceMessageAsync(essence, isSet, token).ConfigureAwait(false))
                Publish(new DomainEvent(selfId, new GroupEssenceMessageChangedEvent(groupId, messageSequence, operatorId, isSet)), operatorId);
        }, cancellationToken);

    private async Task<GroupMember> RequireGroupContentMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        _ = await Store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
        return await Store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false) ?? throw NotAMember(groupId, userId);
    }

    private async Task RequireGroupContentAdministratorAsync(string groupId, string operatorId, CancellationToken cancellationToken)
    {
        var member = await RequireGroupContentMemberAsync(groupId, operatorId, cancellationToken).ConfigureAwait(false);
        if (member.Role <= GroupRole.Member)
            throw NotPermitted("Administrator privileges are required to manage group content");
    }

    private async Task<string> GroupContentDisplayNameAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        var member = await Store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(member?.Card)) return member.Card;
        return (await Store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false))?.DisplayName ?? userId;
    }
}
