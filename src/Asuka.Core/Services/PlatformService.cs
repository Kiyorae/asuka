using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Asuka.Core;

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
    FileNotFound,
    FolderNotFound,
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

public sealed partial class PlatformService : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly Dictionary<string, Channel<DomainEvent>> _subscribers = [];
    private readonly HashSet<string> _registeredBots = new(StringComparer.Ordinal);
    private bool _echoesSelfEvents;
    private volatile bool _disposed;

    public PlatformService(AsukaStore store, AssetStore? assetStore = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Assets = assetStore;
    }

    public AsukaStore Store { get; }
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
            if (_registeredBots.Add(id))
                _botPresence[id] = new BotPresence(id, true, string.Empty, DateTimeOffset.UtcNow);
        }
    }

    public void UnregisterBot(string id)
    {
        lock (_stateSync)
        {
            _registeredBots.Remove(id);
            _botPresence.Remove(id);
        }
    }

    public void SetRegisteredBot(string? id)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = id is null ? null : _botPresence.GetValueOrDefault(id);
            _registeredBots.Clear();
            _botPresence.Clear();
            if (!string.IsNullOrWhiteSpace(id))
            {
                _registeredBots.Add(id);
                _botPresence[id] = existing ?? new BotPresence(id, true, string.Empty, DateTimeOffset.UtcNow);
            }
        }
    }

    public Task SaveUserAsync(User user, CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.SaveAsync(user, token), cancellationToken);

    public Task DeleteUserAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await Store.DeleteUserAsync(id, token).ConfigureAwait(false);
            _accountCredentials.Remove(id);
        }, cancellationToken);

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
                var recipients = await Store.DisbandGroupAsync(id, operatorId, RegisteredBotIds(), token).ConfigureAwait(false);
                foreach (var selfId in recipients)
                    Publish(new DomainEvent(selfId, new GroupDisbandedEvent(id, operatorId)), operatorId);
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
                var segments = content.ToArray();
                foreach (var reply in segments.OfType<ReplySegment>())
                {
                    var target = await Store.GetMessageAsync(reply.MessageId, token).ConfigureAwait(false);
                    if (target is null || target.IsRecalled || target.Scene != scene
                        || target.PeerId != peerId || target.SelfId != selfId)
                    {
                        throw InvalidParameter("A reply must reference an active message in this conversation");
                    }
                }
                var message = new Message(
                    scene,
                    peerId,
                    senderId,
                    selfId,
                    segments,
                    senderId == selfId ? MessageDirection.Incoming : MessageDirection.Outgoing);
                var stored = await Store.AppendLiveMessageAsync(message, token).ConfigureAwait(false);

                if (scene == ChatScene.Group && stored.Anonymous is null
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
                if (existing.IsRecalled)
                {
                    return existing;
                }

                var result = await Store.RecallMessageWithResultAsync(id, operatorId, token).ConfigureAwait(false);
                var recalled = result.Message ?? throw MessageNotFound(id);
                if (!result.Changed)
                {
                    return recalled;
                }

                Publish(
                    new DomainEvent(
                        recalled.SelfId,
                        new MessageRecalledEvent(new MessageRecalled(
                            recalled.Id,
                            recalled.Scene,
                            recalled.PeerId,
                            recalled.SenderId,
                            operatorId,
                            recalled.Anonymous))),
                    operatorId);
                if (result.EssenceRemoved)
                {
                    Publish(new DomainEvent(recalled.SelfId, new GroupEssenceMessageChangedEvent(
                        recalled.PeerId, recalled.Seq, operatorId, false)), operatorId);
                }
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

                if (!await Store.RemoveMemberWithNotificationsAsync(groupId, userId, operatorId, reason,
                    RegisteredBotIds(), token).ConfigureAwait(false)) return;
                var effectiveReason = operatorId == userId ? GroupMemberChangeReason.Voluntary : GroupMemberChangeReason.Administrative;
                await PublishToGroupBotsAsync(
                    groupId,
                    new GroupMemberRemovedEvent(new GroupMemberChange(groupId, userId, operatorId, effectiveReason)),
                    operatorId,
                    CancellationToken.None).ConfigureAwait(false);
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
                if (!await Store.SetAdminWithNotificationsAsync(groupId, userId, operatorId, granted,
                    RegisteredBotIds(), token).ConfigureAwait(false)) return;
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
        ReactAsync(messageId, userId, reaction, added, "face", cancellationToken);

    public Task ReactAsync(
        string messageId,
        string userId,
        string reaction,
        bool added,
        string reactionType,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                if (string.IsNullOrWhiteSpace(reaction) || string.IsNullOrWhiteSpace(reactionType))
                {
                    throw InvalidParameter("Reaction ID and type cannot be empty");
                }

                var message = await Store.GetMessageAsync(messageId, token).ConfigureAwait(false)
                    ?? throw MessageNotFound(messageId);
                if (message.IsRecalled)
                {
                    throw NotPermitted("Cannot react to a recalled message");
                }

                if (message.Scene == ChatScene.Group)
                {
                    _ = await Store.GetMemberAsync(message.PeerId, userId, token).ConfigureAwait(false)
                        ?? throw NotAMember(message.PeerId, userId);
                }
                else if (userId != message.SelfId && userId != message.PeerId)
                {
                    throw NotPermitted("A private message reaction must come from a conversation participant");
                }

                _ = await Store.GetUserAsync(userId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(userId);
                if (!await Store.SetMessageReactionAsync(
                    messageId, userId, reaction, added, reactionType, token).ConfigureAwait(false))
                {
                    var current = await Store.GetMessageAsync(messageId, token).ConfigureAwait(false);
                    if (current is null || current.IsRecalled)
                    {
                        throw NotPermitted("Cannot react to a recalled or removed message");
                    }
                    return;
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
                            added,
                            reactionType))),
                    userId);
            },
            cancellationToken);

    public Task<PendingRequest> RequestFriendAsync(
        string requesterId,
        string targetId,
        string comment = "",
        bool isFiltered = false,
        string via = "asuka",
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                _ = await Store.GetUserAsync(requesterId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(requesterId);
                _ = await Store.GetUserAsync(targetId, token).ConfigureAwait(false)
                    ?? throw UserNotFound(targetId);
                if (requesterId == targetId)
                {
                    throw InvalidParameter("A user cannot send a friend request to themselves");
                }

                if (await Store.GetFriendshipAsync(targetId, requesterId, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists("The users are already friends");
                }

                var request = new PendingRequest(RequestKind.Friend, requesterId, targetId, comment: comment,
                    isFiltered: isFiltered, via: via);
                request = await Store.CreateFriendRequestAsync(request, token).ConfigureAwait(false);
                Publish(new DomainEvent(targetId, new RequestReceivedEvent(request)), requesterId);
                return request;
            },
            cancellationToken);

    public Task<PendingRequest> RequestJoinGroupAsync(
        string groupId,
        string requesterId,
        string targetBotId,
        string comment = "",
        bool isFiltered = false,
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
                await RequireRequestModeratorAsync(groupId, targetBotId, token).ConfigureAwait(false);
                if (await Store.GetMemberAsync(groupId, requesterId, token).ConfigureAwait(false) is not null)
                {
                    throw AlreadyExists("The user is already in the group");
                }

                var request = new PendingRequest(
                    RequestKind.GroupJoin,
                    requesterId,
                    targetBotId,
                    groupId,
                    comment,
                    isFiltered: isFiltered);
                await Store.SaveAsync(request, token).ConfigureAwait(false);
                request = (await Store.GetRequestAsync(request.Id, CancellationToken.None).ConfigureAwait(false))!;
                Publish(new DomainEvent(targetBotId, new RequestReceivedEvent(request)), requesterId);
                return request;
            },
            cancellationToken);

    public Task<PendingRequest> InviteToGroupAsync(
        string groupId,
        string inviterId,
        string inviteeId,
        string comment = "",
        string? sourceGroupId = null,
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

                if (sourceGroupId is not null)
                {
                    _ = await Store.GetGroupAsync(sourceGroupId, token).ConfigureAwait(false)
                        ?? throw GroupNotFound(sourceGroupId);
                    _ = await Store.GetMemberAsync(sourceGroupId, inviterId, token).ConfigureAwait(false)
                        ?? throw NotAMember(sourceGroupId, inviterId);
                    _ = await Store.GetMemberAsync(sourceGroupId, inviteeId, token).ConfigureAwait(false)
                        ?? throw NotAMember(sourceGroupId, inviteeId);
                }

                var request = new PendingRequest(
                    RequestKind.GroupInvite,
                    inviterId,
                    inviteeId,
                    groupId,
                    comment,
                    sourceGroupId: sourceGroupId);
                await Store.SaveAsync(request, token).ConfigureAwait(false);
                request = (await Store.GetRequestAsync(request.Id, CancellationToken.None).ConfigureAwait(false))!;
                Publish(new DomainEvent(inviteeId, new RequestReceivedEvent(request)), inviterId);
                return request;
            },
            cancellationToken);

    public Task ResolveRequestAsync(
        string flag,
        bool approve,
        string reason,
        string remark,
        CancellationToken cancellationToken) =>
        ResolveRequestAsync(flag, approve, reason, remark, expectedSelfId: null,
            cancellationToken: cancellationToken);

    public Task ResolveRequestAsync(
        string flag,
        bool approve,
        string reason = "",
        string remark = "",
        string? expectedSelfId = null,
        string? expectedRequestId = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            async token =>
            {
                var request = expectedRequestId is null
                    ? await Store.GetRequestByFlagAsync(flag, expectedSelfId, token).ConfigureAwait(false)
                    : await Store.GetRequestAsync(expectedRequestId, token).ConfigureAwait(false);
                if (request is null || request.Flag != flag
                    || (expectedSelfId is not null && request.SelfId != expectedSelfId))
                {
                    throw RequestNotFound(flag);
                }
                if (request.Resolution is not null)
                {
                    throw NotPermitted("This request has already been resolved");
                }

                await ValidateRequestResolutionAsync(request, token).ConfigureAwait(false);

                if (!approve)
                {
                    _ = await Store.ResolveRequestAsync(
                        request.Id,
                        RequestResolution.Rejected(reason),
                        resolvedBy: request.SelfId,
                        cancellationToken: token).ConfigureAwait(false)
                        ?? throw NotPermitted("This request has already been resolved");
                    return;
                }

                switch (request.Kind)
                {
                    case RequestKind.Friend:
                        await Store.AcceptFriendRequestAsync(request, remark, token).ConfigureAwait(false);
                        Publish(new DomainEvent(request.SelfId, new FriendAddedEvent(request.RequesterId)));
                        Publish(new DomainEvent(request.RequesterId, new FriendAddedEvent(request.SelfId)));
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
                    case RequestKind.GroupInvitedJoin:
                        if (request.GroupId is null || request.TargetUserId is null)
                        {
                            throw InvalidParameter("The invited join request is missing its group or target user");
                        }

                        _ = await Store.AcceptGroupRequestAsync(request, request.GroupId, request.TargetUserId,
                            request.SelfId, token).ConfigureAwait(false);
                        await PublishToGroupBotsAsync(request.GroupId,
                            new GroupMemberAddedEvent(new GroupMemberChange(request.GroupId, request.TargetUserId,
                                request.SelfId, GroupMemberChangeReason.Administrative, request.RequesterId)),
                            request.SelfId, CancellationToken.None).ConfigureAwait(false);
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
                                GroupMemberChangeReason.Invited,
                                request.RequesterId)),
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
        ShareGroupFileAsync(groupId, userId, asset, "/", cancellationToken);

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
            _accountCredentials.Clear();
            List<ChannelWriter<DomainEvent>> writers;
            lock (_stateSync)
            {
                writers = _subscribers.Values.Select(channel => channel.Writer).ToList();
                _subscribers.Clear();
                _registeredBots.Clear();
                _botPresence.Clear();
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
                reason,
                reason == GroupMemberChangeReason.Invited ? operatorId : null)),
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
        if (senderId == selfId && GetBotPresence(selfId) is { IsOnline: false })
            throw NotPermitted("The bot account is offline");
        if (scene == ChatScene.Group)
        {
            var group = await Store.GetGroupAsync(peerId, cancellationToken).ConfigureAwait(false)
                ?? throw GroupNotFound(peerId);
            var member = await Store.GetMemberAsync(peerId, senderId, cancellationToken).ConfigureAwait(false)
                ?? throw NotAMember(peerId, senderId);
            if (selfId != senderId && await Store.GetMemberAsync(peerId, selfId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw NotAMember(peerId, selfId);
            }
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
        _ = await Store.GetUserAsync(selfId, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound(selfId);
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
        if (message.Scene != ChatScene.Group)
        {
            if (operatorId != message.SelfId && operatorId != message.PeerId)
            {
                throw NotPermitted("A private message can only be recalled by a conversation participant");
            }

            if (message.SenderId != operatorId)
            {
                throw NotPermitted("You can only recall private messages that you sent");
            }

            _ = await Store.GetUserAsync(operatorId, cancellationToken).ConfigureAwait(false)
                ?? throw UserNotFound(operatorId);
            return;
        }

        var actor = await Store.GetMemberAsync(message.PeerId, operatorId, cancellationToken).ConfigureAwait(false)
            ?? throw NotAMember(message.PeerId, operatorId);
        if (message.SenderId == operatorId)
        {
            return;
        }

        if (actor.Role <= GroupRole.Member)
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

    private string[] RegisteredBotIds()
    {
        lock (_stateSync) return _registeredBots.ToArray();
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
        // A removed bot receives its own departure after membership is committed.
        // No other account outside the group is a recipient.
        if (payload is GroupMemberRemovedEvent removed)
        {
            memberIds.Add(removed.Change.UserId);
        }

        var recipients = registered.Where(memberIds.Contains).ToArray();
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
            if (domainEvent.Payload is not BotPresenceChangedEvent
                && ((origin == domainEvent.SelfId && !_echoesSelfEvents)
                    || _botPresence.GetValueOrDefault(domainEvent.SelfId) is { IsOnline: false }))
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
            RequireBotActionOnline();
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
            RequireBotActionOnline();
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
