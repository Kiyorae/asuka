namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task SetPeerPinAsync(Chat chat, bool isPinned, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await ValidatePeerAccessAsync(chat, token).ConfigureAwait(false);
            if (await Store.SetPeerPinAsync(chat, isPinned, token).ConfigureAwait(false))
                Publish(new DomainEvent(chat.SelfId, new PeerPinChangedEvent(chat.Scene, chat.PeerId, isPinned)));
        }, cancellationToken);

    public Task MarkMessageAsReadAsync(Chat chat, long messageSequence, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (messageSequence <= 0) throw InvalidParameter("message_seq must be positive");
            await ValidatePeerAccessAsync(chat, token).ConfigureAwait(false);
            _ = await Store.GetMessageAsync(chat.Scene, chat.PeerId, messageSequence, chat.SelfId, token).ConfigureAwait(false)
                ?? throw MessageNotFound(messageSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Store.MarkMessageAsReadAsync(chat, messageSequence, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SendProfileLikeAsync(string userId, string senderId, int count = 1, CancellationToken cancellationToken = default) =>
        SendProfileLikeCoreAsync(userId, senderId, count, null, cancellationToken);

    public Task SetNicknameAsync(string userId, string nickname, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(nickname)) throw InvalidParameter("new_nickname must not be empty");
            var user = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
            await Store.SaveAsync(user with { Nickname = nickname }, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetBioAsync(string userId, string bio, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (bio is null) throw InvalidParameter("new_bio must be a string");
            var user = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
            await Store.SaveAsync(user with { Sign = bio }, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>The caller ingests protocol media through the safe asset resolver before providing the cached URI.</summary>
    public Task SetAvatarAsync(string userId, string avatarUri, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            if (!Uri.TryCreate(avatarUri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeFile && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw InvalidParameter("Avatar must be an absolute image URI");
            var user = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
            await Store.SaveAsync(user with { Avatar = uri.AbsoluteUri }, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task ValidatePeerAccessAsync(Chat chat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (!Enum.IsDefined(chat.Scene)) throw InvalidParameter("Unsupported message scene");
        _ = await Store.GetUserAsync(chat.SelfId, cancellationToken).ConfigureAwait(false) ?? throw UserNotFound(chat.SelfId);
        if (chat.Scene == ChatScene.Group)
        {
            _ = await Store.GetGroupAsync(chat.PeerId, cancellationToken).ConfigureAwait(false) ?? throw GroupNotFound(chat.PeerId);
            _ = await Store.GetMemberAsync(chat.PeerId, chat.SelfId, cancellationToken).ConfigureAwait(false)
                ?? throw NotAMember(chat.PeerId, chat.SelfId);
            return;
        }
        if (chat.PeerId == chat.SelfId) throw NotPermitted("A private conversation must have two distinct participants");
        _ = await Store.GetUserAsync(chat.PeerId, cancellationToken).ConfigureAwait(false) ?? throw UserNotFound(chat.PeerId);
        if (chat.Scene == ChatScene.Friend)
        {
            if (await Store.GetFriendshipAsync(chat.SelfId, chat.PeerId, cancellationToken).ConfigureAwait(false) is null)
                throw NotPermitted("The users are not friends");
        }
        else if ((await Store.GetMessagesAsync(chat, 1, cancellationToken).ConfigureAwait(false)).Count == 0
            && !(await Store.GetPeerStateAsync(chat, cancellationToken).ConfigureAwait(false)).IsPinned)
        {
            throw NotPermitted("The temporary conversation does not exist for this account");
        }
    }
}
