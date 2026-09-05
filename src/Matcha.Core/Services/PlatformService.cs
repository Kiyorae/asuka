using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Matcha.Core;

public enum PlatformError
{
    UserNotFound,
    GroupNotFound,
    MessageNotFound,
    RequestNotFound,
    NotAMember,
    Muted,
    WholeGroupMuted,
    NotPermitted,
    InvalidParameter,
    AlreadyExists,
}

public sealed class PlatformException : Exception
{
    public PlatformException(PlatformError error, string message)
        : base(message)
    {
        Error = error;
    }

    public PlatformError Error { get; }
    public string? UserId { get; init; }
    public string? GroupId { get; init; }
    public string? ResourceId { get; init; }
    public DateTimeOffset? MutedUntil { get; init; }
}

public sealed class PlatformService : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly Dictionary<string, Channel<DomainEvent>> _subscribers = [];
    private readonly HashSet<string> _registeredBots = new(StringComparer.Ordinal);
    private bool _echoesSelfEvents;
    private volatile bool _disposed;

    public PlatformService(MatchaStore store, AssetStore? assetStore = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Assets = assetStore;
    }

    public MatchaStore Store { get; }
    public AssetStore? Assets { get; }

    public bool EchoesSelfEvents
    {
        get
        {
            lock (_stateSync)
            {
                return _echoesSelfEvents;
            }
        }
    }

    public bool EchoBotGeneratedEvents
    {
        get => EchoesSelfEvents;
        set => SetEchoesSelfEvents(value);
    }

    public IReadOnlySet<string> Bots
    {
        get
        {
            lock (_stateSync)
            {
                return _registeredBots.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    public async IAsyncEnumerable<DomainEvent> Events(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = IdGenerator.RequestId();
        var channel = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(token, channel);
        }

        try
        {
            await foreach (var domainEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return domainEvent;
            }
        }
        finally
        {
            lock (_stateSync)
            {
                _subscribers.Remove(token);
            }

            channel.Writer.TryComplete();
        }
    }

    public IAsyncEnumerable<DomainEvent> EventsAsync(CancellationToken cancellationToken = default) =>
        Events(cancellationToken);

    public void SetEchoesSelfEvents(bool enabled)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _echoesSelfEvents = enabled;
        }
    }

    public void RegisterBot(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registeredBots.Add(id);
        }
    }

    public void UnregisterBot(string id)
    {
        lock (_stateSync)
        {
            _registeredBots.Remove(id);
        }
    }

    public void SetRegisteredBot(string? id)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registeredBots.Clear();
            if (!string.IsNullOrWhiteSpace(id))
            {
                _registeredBots.Add(id);
            }
        }
    }

    public Task SaveUserAsync(User user, CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.SaveAsync(user, token), cancellationToken);

    public Task DeleteUserAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.DeleteUserAsync(id, token), cancellationToken);

    public Task SaveGroupAsync(Group group, CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.SaveAsync(group, token), cancellationToken);

    public Task<Group> CreateGroupAsync(
        Group group,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetUserAsync(ownerId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(ownerId);
                if (await Store.GetGroupAsync(group.Id, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists($"Group {group.Id} already exists");
                }

                await Store.CreateGroupWithOwnerAsync(group, ownerId, token).ConfigureAwait(false);
                return group;
            },
            cancellationToken);

    public Task DeleteGroupAsync(
        string id,
        string operatorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var actor = await Store.GetMemberAsync(id, operatorId, token).ConfigureAwait(false);
                if (actor?.Role != GroupRole.Owner)
                {
                    throw NotPermitted("Only the group owner can delete the group");
                }

                await Store.DeleteGroupAsync(id, token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<Message> SendMessageAsync(
        ChatScene scene,
        string peerId,
        string senderId,
        string selfId,
        IEnumerable<MessageSegment> content,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                await ValidateCanSendAsync(scene, peerId, senderId, selfId, token).ConfigureAwait(false);
                var message = new Message(
                    scene,
                    peerId,
                    senderId,
                    selfId,
                    content,
                    senderId == selfId ? MessageDirection.Incoming : MessageDirection.Outgoing);
                var stored = await Store.AppendMessageAsync(message, token).ConfigureAwait(false);

                if (scene == ChatScene.Group
                    && await Store.GetMemberAsync(peerId, senderId, CancellationToken.None).ConfigureAwait(false) is { } member)
                {
                    await Store.SaveAsync(member with { LastSentAt = stored.Time }, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                Publish(new DomainEvent(selfId, new MessageEvent(stored), time: stored.Time), senderId);
                return stored;
            },
            cancellationToken);

    public Task<Message> RecallMessageAsync(
        string id,
        string operatorId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var existing = await Store.GetMessageAsync(id, token).ConfigureAwait(false)
                    ?? throw MessageNotFound(id);
                await ValidateCanRecallAsync(existing, operatorId, token).ConfigureAwait(false);
                var recalled = await Store.RecallMessageAsync(id, operatorId, token).ConfigureAwait(false)
                    ?? throw MessageNotFound(id);
                Publish(
                    new DomainEvent(
                        recalled.SelfId,
                        new MessageRecalledEvent(new MessageRecalled(
                            recalled.Id,
                            recalled.Scene,
                            recalled.PeerId,
                            recalled.SenderId,
                            operatorId))),
                    operatorId);
                return recalled;
            },
            cancellationToken);

    /// <summary>
    /// Removes every locally persisted message belonging to a conversation.
    /// Keeping this operation here ensures callers cannot bypass the platform
    /// mutation gate when changing visible conversation state.
    /// </summary>
    public Task ClearMessageHistoryAsync(
        Chat chat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return MutateAsync(token => Store.DeleteMessagesAsync(chat, token), cancellationToken);
    }

    public Task<GroupMember> AddMemberAsync(
        string groupId,
        string userId,
        string? operatorId = null,
        GroupMemberChangeReason reason = GroupMemberChangeReason.Voluntary,
        CancellationToken cancellationToken = default) =>
        MutateAsync(token => AddMemberCoreAsync(groupId, userId, operatorId, reason, token), cancellationToken);

    public Task RemoveMemberAsync(
        string groupId,
        string userId,
        string operatorId,
        GroupMemberChangeReason reason = GroupMemberChangeReason.Voluntary,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                if (operatorId != userId)
                {
                    await RequireAuthorityAsync(userId, groupId, operatorId, "remove a member", token)
                        .ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupMemberRemovedEvent(new GroupMemberChange(groupId, userId, operatorId, reason)),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
                await Store.RemoveMemberAsync(groupId, userId, CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task SetAdminAsync(
        string groupId,
        string userId,
        string operatorId,
        bool granted,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var member = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                var actor = await Store.GetMemberAsync(groupId, operatorId, token).ConfigureAwait(false);
                if (actor?.Role != GroupRole.Owner)
                {
                    throw NotPermitted("Only the group owner can manage administrators");
                }

                if (member.Role == GroupRole.Owner)
                {
                    throw NotPermitted("The group owner's role cannot be changed");
                }

                await Store.SaveAsync(member with { Role = granted ? GroupRole.Admin : GroupRole.Member }, token)
                    .ConfigureAwait(false);
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupAdminChangedEvent(new GroupAdminChange(groupId, userId, operatorId, granted)),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task MuteMemberAsync(
        string groupId,
        string userId,
        string operatorId,
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var member = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                await RequireAuthorityAsync(userId, groupId, operatorId, "mute a member", token).ConfigureAwait(false);
                var effectiveDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
                DateTimeOffset? until = effectiveDuration > TimeSpan.Zero
                    ? DateTimeOffset.UtcNow + effectiveDuration
                    : null;
                await Store.SaveAsync(member with { MutedUntil = until }, token).ConfigureAwait(false);
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupMutedEvent(new GroupMute(
                        groupId,
                        userId,
                        operatorId,
                        effectiveDuration > TimeSpan.Zero,
                        effectiveDuration)),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task MuteMemberAsync(
        string groupId,
        string userId,
        string operatorId,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(durationSeconds))
        {
            throw InvalidParameter("Mute duration must be finite");
        }

        return MuteMemberAsync(
            groupId,
            userId,
            operatorId,
            TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
            cancellationToken);
    }

    public Task SetWholeMuteAsync(
        string groupId,
        string operatorId,
        bool muted,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var group = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false)
                    ?? throw GroupNotFound(groupId);
                var actor = await Store.GetMemberAsync(groupId, operatorId, token).ConfigureAwait(false);
                if (actor is null || actor.Role <= GroupRole.Member)
                {
                    throw NotPermitted("Administrator privileges are required to change group-wide mute");
                }

                await Store.SaveAsync(group with { WholeMuted = muted }, token).ConfigureAwait(false);
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupMutedEvent(new GroupMute(groupId, null, operatorId, muted)),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task SetGroupNameAsync(
        string groupId,
        string operatorId,
        string name,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var group = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false)
                    ?? throw GroupNotFound(groupId);
                var trimmed = name.Trim();
                if (trimmed.Length == 0)
                {
                    throw InvalidParameter("Group name cannot be empty");
                }

                var actor = await Store.GetMemberAsync(groupId, operatorId, token).ConfigureAwait(false);
                if (actor is null || actor.Role <= GroupRole.Member)
                {
                    throw NotPermitted("Administrator privileges are required to rename the group");
                }

                await Store.SaveAsync(group with { Name = trimmed }, token).ConfigureAwait(false);
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupNameChangedEvent(groupId, operatorId, trimmed),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task SetMemberCardAsync(
        string groupId,
        string userId,
        string operatorId,
        string card,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var member = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                if (operatorId != userId)
                {
                    await RequireAuthorityAsync(userId, groupId, operatorId, "edit a member card", token)
                        .ConfigureAwait(false);
                }

                await Store.SaveAsync(member with { Card = card }, token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task SetMemberTitleAsync(
        string groupId,
        string userId,
        string operatorId,
        string title,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var member = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                var actor = await Store.GetMemberAsync(groupId, operatorId, token).ConfigureAwait(false);
                if (actor?.Role != GroupRole.Owner)
                {
                    throw NotPermitted("Only the group owner can set custom titles");
                }

                await Store.SaveAsync(member with { Title = title }, token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task PokeAsync(
        ChatScene scene,
        string peerId,
        string senderId,
        string targetId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                if (scene == ChatScene.Group)
                {
                    _ = await Store.GetMemberAsync(peerId, senderId, token).ConfigureAwait(false)
                        ?? throw NotAMember(peerId, senderId);
                    _ = await Store.GetMemberAsync(peerId, targetId, token).ConfigureAwait(false)
                        ?? throw NotAMember(peerId, targetId);
                }
                else
                {
                    _ = await Store.GetUserAsync(senderId, token).ConfigureAwait(false)
                        ?? throw UserNotFound(senderId);
                    _ = await Store.GetUserAsync(peerId, token).ConfigureAwait(false)
                        ?? throw UserNotFound(peerId);
                    _ = await Store.GetUserAsync(targetId, token).ConfigureAwait(false)
                        ?? throw UserNotFound(targetId);
                    if (senderId == peerId || (targetId != senderId && targetId != peerId))
                    {
                        throw NotPermitted("A private poke must target a conversation participant");
                    }

                    if (scene == ChatScene.Friend
                        && await Store.GetFriendshipAsync(senderId, peerId, token).ConfigureAwait(false) is null
                        && await Store.GetFriendshipAsync(peerId, senderId, token).ConfigureAwait(false) is null)
                    {
                        throw NotPermitted("A friend poke requires an active friendship");
                    }
                }

                var payload = new PokeEvent(new PokeInteraction(scene, peerId, senderId, targetId));
                if (scene == ChatScene.Group)
                {
                    await PublishToGroupBotsAsync(peerId, payload, senderId, token).ConfigureAwait(false);
                }
                else
                {
                    Publish(new DomainEvent(targetId, payload), senderId);
                }
            },
            cancellationToken);

    public Task ReactAsync(
        string messageId,
        string userId,
        string reaction,
        bool added,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var message = await Store.GetMessageAsync(messageId, token).ConfigureAwait(false)
                    ?? throw MessageNotFound(messageId);
                if (message.Scene == ChatScene.Group)
                {
                    _ = await Store.GetMemberAsync(message.PeerId, userId, token).ConfigureAwait(false)
                        ?? throw NotAMember(message.PeerId, userId);
                }
                else if (userId != message.SelfId && userId != message.PeerId)
                {
                    throw NotPermitted("A private message reaction must come from a conversation participant");
                }

                Publish(
                    new DomainEvent(
                        message.SelfId,
                        new MessageReactionEvent(new MessageReaction(
                            messageId,
                            message.Scene,
                            message.PeerId,
                            userId,
                            reaction,
                            added))),
                    userId);
            },
            cancellationToken);

    public Task<PendingRequest> RequestFriendAsync(
        string requesterId,
        string targetId,
        string comment = "",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetUserAsync(requesterId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(requesterId);
                _ = await Store.GetUserAsync(targetId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(targetId);
                if (await Store.GetFriendshipAsync(targetId, requesterId, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists("The users are already friends");
                }

                var request = new PendingRequest(RequestKind.Friend, requesterId, targetId, comment: comment);
                await Store.SaveAsync(request, token).ConfigureAwait(false);
                Publish(new DomainEvent(targetId, new RequestReceivedEvent(request)), requesterId);
                return request;
            },
            cancellationToken);

    public Task<PendingRequest> RequestJoinGroupAsync(
        string groupId,
        string requesterId,
        string targetBotId,
        string comment = "",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false)
                    ?? throw GroupNotFound(groupId);
                _ = await Store.GetUserAsync(requesterId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(requesterId);
                _ = await Store.GetUserAsync(targetBotId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(targetBotId);
                _ = await Store.GetMemberAsync(groupId, targetBotId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, targetBotId);
                if (await Store.GetMemberAsync(groupId, requesterId, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists("The user is already in the group");
                }

                var request = new PendingRequest(
                    RequestKind.GroupJoin,
                    requesterId,
                    targetBotId,
                    groupId,
                    comment);
                await Store.SaveAsync(request, token).ConfigureAwait(false);
                Publish(new DomainEvent(targetBotId, new RequestReceivedEvent(request)), requesterId);
                return request;
            },
            cancellationToken);

    public Task<PendingRequest> InviteToGroupAsync(
        string groupId,
        string inviterId,
        string inviteeId,
        string comment = "",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetGroupAsync(groupId, token).ConfigureAwait(false)
                    ?? throw GroupNotFound(groupId);
                _ = await Store.GetUserAsync(inviterId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(inviterId);
                _ = await Store.GetUserAsync(inviteeId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(inviteeId);
                _ = await Store.GetMemberAsync(groupId, inviterId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, inviterId);
                if (await Store.GetMemberAsync(groupId, inviteeId, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists("The invitee is already in the group");
                }

                var request = new PendingRequest(
                    RequestKind.GroupInvite,
                    inviterId,
                    inviteeId,
                    groupId,
                    comment);
                await Store.SaveAsync(request, token).ConfigureAwait(false);
                Publish(new DomainEvent(inviteeId, new RequestReceivedEvent(request)), inviterId);
                return request;
            },
            cancellationToken);

    public Task ResolveRequestAsync(
        string flag,
        bool approve,
        string reason = "",
        string remark = "",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var request = await Store.GetRequestByFlagAsync(flag, token).ConfigureAwait(false)
                    ?? throw RequestNotFound(flag);
                if (request.Resolution is not null)
                {
                    throw NotPermitted("This request has already been resolved");
                }

                if (!approve)
                {
                    _ = await Store.ResolveRequestAsync(
                        request.Id,
                        RequestResolution.Rejected(reason),
                        token).ConfigureAwait(false);
                    return;
                }

                switch (request.Kind)
                {
                    case RequestKind.Friend:
                        await Store.AcceptFriendRequestAsync(request, remark, token).ConfigureAwait(false);
                        Publish(new DomainEvent(request.SelfId, new FriendAddedEvent(request.RequesterId)));
                        break;
                    case RequestKind.GroupJoin:
                        if (request.GroupId is null)
                        {
                            throw InvalidParameter("The group join request is missing a group ID");
                        }

                        _ = await Store.AcceptGroupRequestAsync(
                            request,
                            request.GroupId,
                            request.RequesterId,
                            request.SelfId,
                            token).ConfigureAwait(false);
                        await PublishToGroupBotsAsync(
                            request.GroupId,
                            new GroupMemberAddedEvent(new GroupMemberChange(
                                request.GroupId,
                                request.RequesterId,
                                request.SelfId,
                                GroupMemberChangeReason.Administrative)),
                            request.SelfId,
                            CancellationToken.None).ConfigureAwait(false);
                        break;
                    case RequestKind.GroupInvite:
                        if (request.GroupId is null)
                        {
                            throw InvalidParameter("The group invitation is missing a group ID");
                        }

                        _ = await Store.AcceptGroupRequestAsync(
                            request,
                            request.GroupId,
                            request.SelfId,
                            request.RequesterId,
                            token).ConfigureAwait(false);
                        await PublishToGroupBotsAsync(
                            request.GroupId,
                            new GroupMemberAddedEvent(new GroupMemberChange(
                                request.GroupId,
                                request.SelfId,
                                request.RequesterId,
                                GroupMemberChangeReason.Invited)),
                            request.RequesterId,
                            CancellationToken.None).ConfigureAwait(false);
                        break;
                    default:
                        throw InvalidParameter("Unknown request type");
                }
            },
            cancellationToken);

    public Task RemoveFriendAsync(
        string userId,
        string friendId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                if (await Store.GetFriendshipAsync(userId, friendId, token).ConfigureAwait(false) is null)
                {
                    throw NotPermitted("The users are not friends");
                }

                await Store.RemoveFriendshipAsync(userId, friendId, token).ConfigureAwait(false);
                Publish(new DomainEvent(userId, new FriendRemovedEvent(friendId)));
            },
            cancellationToken);

    public Task AddFriendshipAsync(
        string userId,
        string friendId,
        string remark = "",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
                _ = await Store.GetUserAsync(friendId, token).ConfigureAwait(false) ?? throw UserNotFound(friendId);
                await Store.SaveAsync(new Friendship(userId, friendId, remark), token).ConfigureAwait(false);
                await Store.SaveAsync(new Friendship(friendId, userId), CancellationToken.None).ConfigureAwait(false);
                Publish(new DomainEvent(userId, new FriendAddedEvent(friendId)));
                Publish(new DomainEvent(friendId, new FriendAddedEvent(userId)));
            },
            cancellationToken);

    public Task UploadGroupFileAsync(
        string groupId,
        string userId,
        Asset asset,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetMemberAsync(groupId, userId, token).ConfigureAwait(false)
                    ?? throw NotAMember(groupId, userId);
                await Store.SaveAsync(asset, token).ConfigureAwait(false);
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupFileUploadedEvent(new GroupFileUpload(groupId, userId, asset)),
                    userId,
                    CancellationToken.None).ConfigureAwait(false);
            },
            cancellationToken);

    public Task SaveAssetAsync(
        Asset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return MutateAsync(
            token => Store.SaveAsync(asset, token),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            List<ChannelWriter<DomainEvent>> writers;
            lock (_stateSync)
            {
                writers = _subscribers.Values.Select(channel => channel.Writer).ToList();
                _subscribers.Clear();
                _registeredBots.Clear();
            }

            foreach (var writer in writers)
            {
                writer.TryComplete();
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<GroupMember> AddMemberCoreAsync(
        string groupId,
        string userId,
        string? operatorId,
        GroupMemberChangeReason reason,
        CancellationToken cancellationToken)
    {
        var group = await Store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw GroupNotFound(groupId);
        _ = await Store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound(userId);
        if (operatorId is not null
            && await Store.GetMemberAsync(groupId, operatorId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw NotAMember(groupId, operatorId);
        }

        if (await Store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AlreadyExists($"User {userId} is already a member of group {groupId}");
        }

        if (await Store.GetMemberCountAsync(groupId, cancellationToken).ConfigureAwait(false) >= group.MaxMemberCount)
        {
            throw NotPermitted($"The group has reached its member limit of {group.MaxMemberCount}");
        }

        var member = new GroupMember(groupId, userId);
        await Store.SaveAsync(member, cancellationToken).ConfigureAwait(false);
        await PublishToGroupBotsAsync(
            groupId,
            new GroupMemberAddedEvent(new GroupMemberChange(
                groupId,
                userId,
                operatorId ?? userId,
                reason)),
            operatorId,
            CancellationToken.None).ConfigureAwait(false);
        return member;
    }

    private async Task ValidateCanSendAsync(
        ChatScene scene,
        string peerId,
        string senderId,
        string selfId,
        CancellationToken cancellationToken)
    {
        if (scene == ChatScene.Group)
        {
            var group = await Store.GetGroupAsync(peerId, cancellationToken).ConfigureAwait(false)
                ?? throw GroupNotFound(peerId);
            var member = await Store.GetMemberAsync(peerId, senderId, cancellationToken).ConfigureAwait(false)
                ?? throw NotAMember(peerId, senderId);
            if (member.IsMuted)
            {
                throw new PlatformException(
                    PlatformError.Muted,
                    $"Muted until {member.MutedUntil:HH:mm:ss}")
                {
                    GroupId = peerId,
                    UserId = senderId,
                    MutedUntil = member.MutedUntil,
                };
            }

            if (group.WholeMuted && member.Role == GroupRole.Member)
            {
                throw new PlatformException(
                    PlatformError.WholeGroupMuted,
                    $"Group {peerId} has group-wide mute enabled")
                {
                    GroupId = peerId,
                };
            }

            return;
        }

        _ = await Store.GetUserAsync(peerId, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound(peerId);
        if (selfId == peerId)
        {
            throw NotPermitted("A private conversation must have two distinct participants");
        }

        if (senderId != selfId && senderId != peerId)
        {
            throw NotPermitted("A private message sender must be one of the conversation participants");
        }
    }

    private async Task ValidateCanRecallAsync(
        Message message,
        string operatorId,
        CancellationToken cancellationToken)
    {
        if (message.SenderId == operatorId)
        {
            return;
        }

        if (message.Scene != ChatScene.Group)
        {
            throw NotPermitted("You can only recall private messages that you sent");
        }

        var actor = await Store.GetMemberAsync(message.PeerId, operatorId, cancellationToken).ConfigureAwait(false);
        if (actor is null || actor.Role <= GroupRole.Member)
        {
            throw NotPermitted("Administrator privileges are required to recall another user's message");
        }

        var sender = await Store.GetMemberAsync(message.PeerId, message.SenderId, cancellationToken)
            .ConfigureAwait(false);
        if (sender is not null && sender.Role >= actor.Role)
        {
            throw NotPermitted("Cannot recall a message from a member with an equal or higher role");
        }
    }

    private async Task RequireAuthorityAsync(
        string targetId,
        string groupId,
        string operatorId,
        string action,
        CancellationToken cancellationToken)
    {
        var actor = await Store.GetMemberAsync(groupId, operatorId, cancellationToken).ConfigureAwait(false)
            ?? throw NotAMember(groupId, operatorId);
        if (actor.Role <= GroupRole.Member)
        {
            throw NotPermitted($"Administrator privileges are required to {action}");
        }

        var target = await Store.GetMemberAsync(groupId, targetId, cancellationToken).ConfigureAwait(false);
        if (target is not null && target.Role >= actor.Role)
        {
            throw NotPermitted($"Cannot {action} when the target has an equal or higher role");
        }
    }

    private async Task PublishToGroupBotsAsync(
        string groupId,
        DomainEventPayload payload,
        string? origin,
        CancellationToken cancellationToken)
    {
        var members = await Store.GetMembersAsync(groupId, cancellationToken).ConfigureAwait(false);
        string[] registered;
        lock (_stateSync)
        {
            registered = _registeredBots.ToArray();
        }

        var memberIds = members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal);
        var recipients = registered.Where(memberIds.Contains).ToArray();
        if (recipients.Length == 0)
        {
            recipients = registered;
        }

        foreach (var selfId in recipients)
        {
            Publish(new DomainEvent(selfId, payload), origin);
        }
    }

    private void Publish(DomainEvent domainEvent, string? origin = null)
    {
        ChannelWriter<DomainEvent>[] writers;
        lock (_stateSync)
        {
            if (origin == domainEvent.SelfId && !_echoesSelfEvents)
            {
                return;
            }

            writers = _subscribers.Values.Select(channel => channel.Writer).ToArray();
        }

        foreach (var writer in writers)
        {
            _ = writer.TryWrite(domainEvent);
        }
    }

    private async Task<T> MutateAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await mutation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task MutateAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await mutation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static PlatformException UserNotFound(string id) => new(
        PlatformError.UserNotFound,
        $"User not found: {id}")
    {
        UserId = id,
        ResourceId = id,
    };

    private static PlatformException GroupNotFound(string id) => new(
        PlatformError.GroupNotFound,
        $"Group not found: {id}")
    {
        GroupId = id,
        ResourceId = id,
    };

    private static PlatformException MessageNotFound(string id) => new(
        PlatformError.MessageNotFound,
        $"Message not found: {id}")
    {
        ResourceId = id,
    };

    private static PlatformException RequestNotFound(string id) => new(
        PlatformError.RequestNotFound,
        $"Request not found: {id}")
    {
        ResourceId = id,
    };

    private static PlatformException NotAMember(string groupId, string userId) => new(
        PlatformError.NotAMember,
        $"User {userId} is not a member of group {groupId}")
    {
        GroupId = groupId,
        UserId = userId,
    };

    private static PlatformException NotPermitted(string reason) => new(PlatformError.NotPermitted, reason);
    private static PlatformException InvalidParameter(string detail) => new(
        PlatformError.InvalidParameter,
        $"Invalid parameter: {detail}");
    private static PlatformException AlreadyExists(string detail) => new(
        PlatformError.AlreadyExists,
        $"Already exists: {detail}");
}
