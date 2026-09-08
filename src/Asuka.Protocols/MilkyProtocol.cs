using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class MilkyProtocol : IProtocolImplementation
{
    private const string CurrentVersion = "0.1.0.1";
    private static readonly FrozenSet<string> ApiNames = new[]
    {
        "get_login_info", "get_impl_info", "get_user_profile", "get_friend_list", "get_friend_info",
        "get_group_list", "get_group_info", "get_group_member_list", "get_group_member_info",
        "get_cookies", "get_csrf_token",
        "get_peer_pins", "set_peer_pin", "set_avatar", "set_nickname", "set_bio", "get_custom_face_url_list",
        "send_private_message", "send_group_message", "recall_private_message", "recall_group_message",
        "get_message", "get_history_messages", "get_resource_temp_url", "get_forwarded_messages",
        "mark_message_as_read", "send_friend_nudge", "send_profile_like", "delete_friend",
        "get_friend_requests", "accept_friend_request", "reject_friend_request", "set_group_name",
        "set_group_member_card", "set_group_member_special_title", "set_group_member_admin",
        "set_group_member_mute", "set_group_whole_mute", "kick_group_member", "quit_group",
        "send_group_nudge", "send_group_message_reaction", "get_group_notifications",
        "accept_group_request", "reject_group_request", "accept_group_invitation", "reject_group_invitation",
        "upload_private_file", "upload_group_file", "get_private_file_download_url", "get_group_file_download_url",
        "get_group_files", "move_group_file", "rename_group_file", "delete_group_file", "persist_group_file",
        "create_group_folder", "rename_group_folder", "delete_group_folder",
        "set_group_avatar", "get_group_announcements", "send_group_announcement", "delete_group_announcement",
        "get_group_essence_messages", "set_group_essence_message",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly PlatformService _platform;
    private readonly AsukaStore _store;
    private readonly MediaService _media;
    private readonly MilkySegmentCodec _segments;
    private readonly MilkyEntityEncoder _entities;
    private readonly MilkyEventEncoder _events;

    public MilkyProtocol(string selfId, PlatformService platform, MediaService media)
    {
        SelfId = selfId;
        _platform = platform;
        _store = platform.Store;
        _media = media;
        _segments = new MilkySegmentCodec(media, _store);
        _entities = new MilkyEntityEncoder(_store);
        _events = new MilkyEventEncoder(selfId, _store, _segments, _entities);
    }

    public string Identifier => "milky";

    public string DisplayName => "Milky 1.3";

    public string SelfId { get; }

    public IReadOnlySet<TransportMode> SupportedTransports { get; } =
        new HashSet<TransportMode> { TransportMode.MilkyService };

    public WebSocketClientHandshake ClientHandshake => WebSocketClientHandshake.Default;

    public TimeSpan? HeartbeatInterval => null;

    public async Task<ProtocolReply> HandleAsync(
        ProtocolCall request,
        CancellationToken cancellationToken = default)
    {
        if (!ApiNames.Contains(request.Name))
        {
            return new ProtocolReply(
                -404,
                Message: $"Requested API does not exist: {request.Name}",
                HttpStatus: 404);
        }

        if (OfflineFailure(request.Name) is { } offline) return offline;
        try
        {
            using var botAction = RequiresOnlineAccount(request.Name) ? _platform.BeginBotAction(SelfId) : null;
            return request.Name switch
            {
                "get_login_info" => await LoginInfoAsync(cancellationToken).ConfigureAwait(false),
                "get_impl_info" => ImplementationInfo(),
                "get_user_profile" => await UserProfileAsync(request, cancellationToken).ConfigureAwait(false),
                "get_friend_list" => await FriendListAsync(cancellationToken).ConfigureAwait(false),
                "get_friend_info" => await FriendInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_list" => await GroupListAsync(cancellationToken).ConfigureAwait(false),
                "get_group_info" => await GroupInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_member_list" => await GroupMemberListAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_member_info" => await GroupMemberInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_cookies" or "get_csrf_token" => await CredentialApiAsync(request, cancellationToken).ConfigureAwait(false),
                "send_private_message" => await SendMessageAsync(request, ChatScene.Friend, cancellationToken).ConfigureAwait(false),
                "send_group_message" => await SendMessageAsync(request, ChatScene.Group, cancellationToken).ConfigureAwait(false),
                "recall_private_message" => await RecallMessageAsync(request, ChatScene.Friend, cancellationToken).ConfigureAwait(false),
                "recall_group_message" => await RecallMessageAsync(request, ChatScene.Group, cancellationToken).ConfigureAwait(false),
                "get_message" => await GetMessageAsync(request, cancellationToken).ConfigureAwait(false),
                "get_history_messages" => await HistoryAsync(request, cancellationToken).ConfigureAwait(false),
                "get_resource_temp_url" => await ResourceUrlAsync(request, cancellationToken).ConfigureAwait(false),
                "get_forwarded_messages" => await ForwardedMessagesAsync(request, cancellationToken).ConfigureAwait(false),
                "get_peer_pins" => await PeerPinsAsync(cancellationToken).ConfigureAwait(false),
                "set_peer_pin" => await SetPeerPinAsync(request, cancellationToken).ConfigureAwait(false),
                "mark_message_as_read" => await MarkMessageAsReadAsync(request, cancellationToken).ConfigureAwait(false),
                "send_profile_like" => await SendProfileLikeAsync(request, cancellationToken).ConfigureAwait(false),
                "set_avatar" => await SetAvatarAsync(request, cancellationToken).ConfigureAwait(false),
                "set_nickname" => await SetNicknameAsync(request, cancellationToken).ConfigureAwait(false),
                "set_bio" => await SetBioAsync(request, cancellationToken).ConfigureAwait(false),
                "get_custom_face_url_list" => await CustomFaceUrlsAsync(cancellationToken).ConfigureAwait(false),
                "send_friend_nudge" => await FriendNudgeAsync(request, cancellationToken).ConfigureAwait(false),
                "delete_friend" => await DeleteFriendAsync(request, cancellationToken).ConfigureAwait(false),
                "get_friend_requests" => await FriendRequestsAsync(request, cancellationToken).ConfigureAwait(false),
                "accept_friend_request" => await ResolveFriendRequestAsync(request, true, cancellationToken).ConfigureAwait(false),
                "reject_friend_request" => await ResolveFriendRequestAsync(request, false, cancellationToken).ConfigureAwait(false),
                "set_group_name" => await SetGroupNameAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_member_card" => await SetMemberCardAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_member_special_title" => await SetMemberTitleAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_member_admin" => await SetMemberAdminAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_member_mute" => await SetMemberMuteAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_whole_mute" => await SetWholeMuteAsync(request, cancellationToken).ConfigureAwait(false),
                "kick_group_member" => await KickMemberAsync(request, cancellationToken).ConfigureAwait(false),
                "quit_group" => await QuitGroupAsync(request, cancellationToken).ConfigureAwait(false),
                "send_group_nudge" => await GroupNudgeAsync(request, cancellationToken).ConfigureAwait(false),
                "send_group_message_reaction" => await GroupReactionAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_notifications" => await GroupNotificationsAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_avatar" => await SetGroupAvatarAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_announcements" => await GetGroupAnnouncementsAsync(request, cancellationToken).ConfigureAwait(false),
                "send_group_announcement" => await SendGroupAnnouncementAsync(request, cancellationToken).ConfigureAwait(false),
                "delete_group_announcement" => await DeleteGroupAnnouncementAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_essence_messages" => await GetGroupEssenceMessagesAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_essence_message" => await SetGroupEssenceMessageAsync(request, cancellationToken).ConfigureAwait(false),
                "accept_group_request" => await ResolveGroupRequestAsync(request, RequestKind.GroupJoin, true, cancellationToken).ConfigureAwait(false),
                "reject_group_request" => await ResolveGroupRequestAsync(request, RequestKind.GroupJoin, false, cancellationToken).ConfigureAwait(false),
                "accept_group_invitation" => await ResolveGroupRequestAsync(request, RequestKind.GroupInvite, true, cancellationToken).ConfigureAwait(false),
                "reject_group_invitation" => await ResolveGroupRequestAsync(request, RequestKind.GroupInvite, false, cancellationToken).ConfigureAwait(false),
                "upload_private_file" or "upload_group_file" or "get_private_file_download_url" or "get_group_file_download_url"
                    or "get_group_files" or "move_group_file" or "rename_group_file" or "delete_group_file" or "persist_group_file"
                    or "create_group_folder" or "rename_group_folder" or "delete_group_folder"
                    => await FileApiAsync(request, cancellationToken).ConfigureAwait(false),
                _ => new ProtocolReply(-404, Message: $"Requested API does not exist: {request.Name}", HttpStatus: 404),
            };
        }
        catch (PlatformException error)
        {
            if (OfflineFailure(request.Name) is { } offlineReply) return offlineReply;
            return new ProtocolReply(error.Error == PlatformError.InvalidParameter ? -400 : -404, Message: error.Message);
        }
        catch (ArgumentException error)
        {
            return Invalid(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ProtocolReply(-404, Message: error.Message);
        }
    }

    public JsonObject CreateEnvelope(ProtocolReply reply, JsonNode? echo = null)
    {
        _ = echo;
        return reply.IsSuccess
            ? new JsonObject
            {
                ["status"] = "ok",
                ["retcode"] = 0,
                ["data"] = reply.EffectiveData.DeepClone(),
            }
            : new JsonObject
            {
                ["status"] = "failed",
                ["retcode"] = reply.RetCode,
                ["message"] = reply.Message,
            };
    }

    public Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        return domainEvent.SelfId == SelfId
            ? _events.EncodeAsync(domainEvent, cancellationToken)
            : Task.FromResult<IReadOnlyList<OutboundFrame>>([]);
    }

    private async Task<ProtocolReply> LoginInfoAsync(CancellationToken cancellationToken)
    {
        var user = await _store.GetUserAsync(SelfId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["uin"] = Uin(SelfId),
            ["nickname"] = user?.DisplayName ?? SelfId,
        });
    }

    private static ProtocolReply ImplementationInfo() => ProtocolReply.Success(new JsonObject
    {
        ["impl_name"] = "asuka",
        ["impl_version"] = CurrentVersion,
        ["qq_protocol_version"] = string.Empty,
        ["qq_protocol_type"] = "windows",
        ["milky_version"] = "1.3",
    });

    private async Task<ProtocolReply> UserProfileAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var userId = request.GetId("user_id");
        if (userId is null)
        {
            return Invalid("Missing user_id");
        }

        var user = await _store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.UserNotFound, $"User not found: {userId}", userId);
        var relation = await _store.GetFriendshipAsync(SelfId, userId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["nickname"] = user.DisplayName,
            ["age"] = user.Age ?? 0,
            ["sex"] = user.Sex.ToString().ToLowerInvariant(),
            ["remark"] = relation?.Remark ?? string.Empty,
            ["bio"] = user.Sign,
            ["qid"] = string.Empty,
            ["level"] = 0,
            ["country"] = string.Empty,
            ["city"] = string.Empty,
            ["school"] = string.Empty,
        });
    }

    private async Task<ProtocolReply> FriendListAsync(CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var entry in await _store.GetFriendsAsync(SelfId, cancellationToken).ConfigureAwait(false))
        {
            result.Add(MilkyEntityEncoder.Friend(entry.User, entry.Friendship.Remark));
        }

        return ProtocolReply.Success(new JsonObject { ["friends"] = result });
    }

    private async Task<ProtocolReply> FriendInfoAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var userId = request.GetId("user_id");
        if (userId is null)
        {
            return Invalid("Missing user_id");
        }

        var user = await _store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.UserNotFound, $"User not found: {userId}", userId);
        var relation = await _store.GetFriendshipAsync(SelfId, userId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["friend"] = MilkyEntityEncoder.Friend(user, relation?.Remark ?? string.Empty),
        });
    }

    private async Task<ProtocolReply> GroupListAsync(CancellationToken cancellationToken)
    {
        var groups = await _store.GetGroupsContainingAsync(SelfId, cancellationToken).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            groups = await _store.GetAllGroupsAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = new JsonArray();
        foreach (var group in groups)
        {
            result.Add(await _entities.GroupAsync(group, cancellationToken).ConfigureAwait(false));
        }

        return ProtocolReply.Success(new JsonObject { ["groups"] = result });
    }

    private async Task<ProtocolReply> GroupInfoAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var groupId = request.GetId("group_id");
        if (groupId is null)
        {
            return Invalid("Missing group_id");
        }

        var group = await _store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.GroupNotFound, $"Group not found: {groupId}", groupId);
        return ProtocolReply.Success(new JsonObject
        {
            ["group"] = await _entities.GroupAsync(group, cancellationToken).ConfigureAwait(false),
        });
    }

    private async Task<ProtocolReply> GroupMemberListAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var groupId = request.GetId("group_id");
        if (groupId is null)
        {
            return Invalid("Missing group_id");
        }

        if (await _store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw Missing(PlatformError.GroupNotFound, $"Group not found: {groupId}", groupId);
        }

        var result = new JsonArray();
        foreach (var member in await _store.GetMembersAsync(groupId, cancellationToken).ConfigureAwait(false))
        {
            result.Add(MilkyEntityEncoder.GroupMember(
                member,
                await _store.GetUserAsync(member.UserId, cancellationToken).ConfigureAwait(false)));
        }

        return ProtocolReply.Success(new JsonObject { ["members"] = result });
    }

    private async Task<ProtocolReply> GroupMemberInfoAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var groupId = request.GetId("group_id");
        var userId = request.GetId("user_id");
        if (groupId is null || userId is null)
        {
            return Invalid("Missing group_id or user_id");
        }

        var member = await _store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.NotAMember, $"User {userId} is not a member of group {groupId}", userId);
        var user = await _store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["member"] = MilkyEntityEncoder.GroupMember(member, user),
        });
    }

    private async Task<ProtocolReply> SendMessageAsync(
        ProtocolCall request,
        ChatScene scene,
        CancellationToken cancellationToken)
    {
        var peerId = request.GetId(scene == ChatScene.Group ? "group_id" : "user_id");
        if (peerId is null)
        {
            return Invalid(scene == ChatScene.Group ? "Missing group_id" : "Missing user_id");
        }

        var raw = request.GetArray("message");
        if (raw is null)
        {
            return Invalid("Missing message");
        }

        var content = await _segments.DecodeOutgoingAsync(
            raw,
            new Chat(scene, peerId, SelfId),
            cancellationToken).ConfigureAwait(false);
        if (content.Count == 0)
        {
            return Invalid("Message content is empty");
        }

        var message = await _platform.SendMessageAsync(
            scene,
            peerId,
            SelfId,
            SelfId,
            content,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["message_seq"] = message.Seq,
            ["time"] = message.Time.ToUnixTimeSeconds(),
        });
    }

    private async Task<ProtocolReply> RecallMessageAsync(
        ProtocolCall request,
        ChatScene scene,
        CancellationToken cancellationToken)
    {
        var peerId = request.GetId(scene == ChatScene.Group ? "group_id" : "user_id");
        var sequence = request.GetLong("message_seq");
        if (peerId is null || sequence is null)
        {
            return Invalid("Missing peer identifier or message_seq");
        }

        var message = await _store.GetMessageAsync(
            scene,
            peerId,
            sequence.Value,
            SelfId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.MessageNotFound, $"Message not found: {scene}:{peerId}:{sequence}", peerId);
        _ = await _platform.RecallMessageAsync(message.Id, SelfId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GetMessageAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (ParseScene(request.GetText("message_scene")) is not { } scene
            || request.GetId("peer_id") is not { } peerId
            || request.GetLong("message_seq") is not { } sequence)
        {
            return Invalid("Missing or invalid message identity");
        }

        var message = await _store.GetMessageAsync(scene, peerId, sequence, SelfId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Missing(PlatformError.MessageNotFound, $"Message not found: {scene}:{peerId}:{sequence}", peerId);
        if (message.Anonymous is not null)
            throw Missing(PlatformError.MessageNotFound, "Anonymous messages are unavailable in this protocol", peerId);
        if (message.IsRecalled)
        {
            throw Missing(PlatformError.MessageNotFound, $"Message has been recalled: {sequence}", peerId);
        }

        var encoded = await _segments.EncodeIncomingAsync(message.Content, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject
        {
            ["message"] = await _entities.IncomingMessageAsync(message, encoded, cancellationToken).ConfigureAwait(false),
        });
    }

    private async Task<ProtocolReply> HistoryAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (ParseScene(request.GetText("message_scene")) is not { } scene
            || request.GetId("peer_id") is not { } peerId)
        {
            return Invalid("Missing or invalid message_scene or peer_id");
        }

        var limit = Math.Clamp(request.GetInteger("limit") ?? 20, 1, 30);
        var history = await _store.GetHistoryAsync(
            scene,
            peerId,
            SelfId,
            request.GetLong("start_message_seq"),
            limit,
            cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var message in history.Where(static message => !message.IsRecalled && message.Anonymous is null))
        {
            var encoded = await _segments.EncodeIncomingAsync(message.Content, cancellationToken).ConfigureAwait(false);
            result.Add(await _entities.IncomingMessageAsync(message, encoded, cancellationToken).ConfigureAwait(false));
        }

        var data = new JsonObject { ["messages"] = result };
        if (history.Count > 0 && history[0].Seq is > 1 and var oldest)
        {
            data["next_message_seq"] = oldest - 1;
        }

        return ProtocolReply.Success(data);
    }

    private async Task<ProtocolReply> ResourceUrlAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var resourceId = request.GetId("resource_id");
        if (resourceId is null)
        {
            return Invalid("Missing resource_id");
        }

        var resolved = await _media.ResolveIdAsync(resourceId, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.NotPermitted, $"Resource not found: {resourceId}", resourceId);
        return ProtocolReply.Success(new JsonObject { ["url"] = _media.GetUrl(resolved.Id).AbsoluteUri });
    }

    private async Task<ProtocolReply> ForwardedMessagesAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var forwardId = request.GetText("forward_id");
        if (string.IsNullOrWhiteSpace(forwardId))
        {
            return Invalid("Missing forward_id");
        }

        var nodes = await _store.GetForwardNodesAsync(forwardId, SelfId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.MessageNotFound, $"Forward not found: {forwardId}", forwardId);
        var result = new JsonArray();
        foreach (var node in nodes)
        {
            var stored = await _store.GetMessageAsync(node.Id, cancellationToken).ConfigureAwait(false);
            var sender = stored?.Anonymous is null
                ? await _store.GetUserAsync(node.SenderId, cancellationToken).ConfigureAwait(false) : null;
            result.Add(new JsonObject
            {
                ["message_seq"] = stored?.SelfId == SelfId ? stored.Seq : 0,
                ["sender_name"] = stored?.Anonymous?.Name ?? node.SenderName,
                ["avatar_url"] = HttpAvatar(sender?.Avatar),
                ["time"] = node.Time.ToUnixTimeSeconds(),
                ["segments"] = await _segments.EncodeIncomingAsync(node.Content, cancellationToken).ConfigureAwait(false),
            });
        }

        return ProtocolReply.Success(new JsonObject { ["messages"] = result });
    }

    private async Task<ProtocolReply> FriendNudgeAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var userId = request.GetId("user_id");
        if (userId is null)
        {
            return Invalid("Missing user_id");
        }

        await _platform.PokeAsync(
            ChatScene.Friend,
            userId,
            SelfId,
            request.GetBoolean("is_self") == true ? SelfId : userId,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> DeleteFriendAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var userId = request.GetId("user_id");
        if (userId is null)
        {
            return Invalid("Missing user_id");
        }

        await _platform.RemoveFriendAsync(SelfId, userId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> FriendRequestsAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var limit = request.GetInteger("limit") ?? 20;
        if (limit <= 0) return Invalid("limit must be positive");
        var filtered = request.GetBoolean("is_filtered") ?? false;
        var history = await _store.GetFriendRequestHistoryAsync(SelfId, filtered, limit, cancellationToken)
            .ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var item in history)
        {
            result.Add(MilkyEntityEncoder.FriendRequest(item));
        }

        return ProtocolReply.Success(new JsonObject { ["requests"] = result });
    }

    private async Task<ProtocolReply> ResolveFriendRequestAsync(
        ProtocolCall request,
        bool approve,
        CancellationToken cancellationToken)
    {
        var initiatorUid = request.GetText("initiator_uid");
        if (string.IsNullOrWhiteSpace(initiatorUid))
        {
            return Invalid("Missing initiator_uid");
        }

        var filtered = request.GetBoolean("is_filtered") ?? false;
        var requests = await _store.GetPendingRequestsAsync(SelfId, RequestKind.Friend, cancellationToken)
            .ConfigureAwait(false);
        var pending = requests.FirstOrDefault(item => item.RequesterId == initiatorUid && item.IsFiltered == filtered);
        if (pending is null)
        {
            throw Missing(PlatformError.RequestNotFound, $"Friend request not found: {initiatorUid}", initiatorUid);
        }

        await _platform.ResolveRequestAsync(
            pending.Flag,
            approve,
            request.GetText("reason") ?? string.Empty,
            cancellationToken: cancellationToken,
            expectedSelfId: SelfId,
            expectedRequestId: pending.Id).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupNameAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.GetId("group_id") is not { } groupId || request.GetText("new_group_name") is not { } name)
        {
            return Invalid("Missing group_id or new_group_name");
        }

        await _platform.SetGroupNameAsync(groupId, SelfId, name, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetMemberCardAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetMemberCardAsync(
            groupId,
            userId,
            SelfId,
            request.GetText("card") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetMemberTitleAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetMemberTitleAsync(
            groupId,
            userId,
            SelfId,
            request.GetText("special_title") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetMemberAdminAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetAdminAsync(
            groupId,
            userId,
            SelfId,
            request.GetBoolean("is_set") ?? true,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetMemberMuteAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.MuteMemberAsync(
            groupId,
            userId,
            SelfId,
            request.GetLong("duration") ?? 0,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetWholeMuteAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.GetId("group_id") is not { } groupId)
        {
            return Invalid("Missing group_id");
        }

        await _platform.SetWholeMuteAsync(
            groupId,
            SelfId,
            request.GetBoolean("is_mute") ?? true,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> KickMemberAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.RemoveMemberAsync(
            groupId,
            userId,
            SelfId,
            GroupMemberChangeReason.Administrative,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> QuitGroupAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.GetId("group_id") is not { } groupId)
        {
            return Invalid("Missing group_id");
        }

        await _platform.RemoveMemberAsync(
            groupId,
            SelfId,
            SelfId,
            GroupMemberChangeReason.Voluntary,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GroupNudgeAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupMember(request, out var groupId, out var userId))
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.PokeAsync(ChatScene.Group, groupId, SelfId, userId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GroupReactionAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.GetId("group_id") is not { } groupId
            || request.GetLong("message_seq") is not { } sequence
            || request.GetText("reaction") is not { } reaction)
        {
            return Invalid("Missing group_id, message_seq, or reaction");
        }

        var reactionType = request.GetText("reaction_type") ?? "face";
        if (reactionType is not ("face" or "emoji") || string.IsNullOrWhiteSpace(reaction))
        {
            return Invalid("reaction must be nonempty and reaction_type must be face or emoji");
        }

        var message = await _store.GetMessageAsync(
            ChatScene.Group,
            groupId,
            sequence,
            SelfId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.MessageNotFound, $"Message not found: group:{groupId}:{sequence}", groupId);
        await _platform.ReactAsync(
            message.Id,
            SelfId,
            reaction,
            request.GetBoolean("is_add") ?? true,
            reactionType,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GroupNotificationsAsync(
        ProtocolCall request,
        CancellationToken cancellationToken)
    {
        var limit = request.GetInteger("limit") ?? 20;
        if (limit <= 0) return Invalid("limit must be positive");
        var filtered = request.GetBoolean("is_filtered") ?? false;
        var page = await _platform.GetGroupNotificationsAsync(SelfId, filtered,
            request.GetLong("start_notification_seq"), limit, cancellationToken)
            .ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var item in page.Notifications)
        {
            result.Add(MilkyEntityEncoder.GroupNotification(item));
        }

        var data = new JsonObject { ["notifications"] = result };
        if (page.NextSequence is { } next)
        {
            data["next_notification_seq"] = next;
        }

        return ProtocolReply.Success(data);
    }

    private async Task<ProtocolReply> ResolveGroupRequestAsync(
        ProtocolCall request,
        RequestKind kind,
        bool approve,
        CancellationToken cancellationToken)
    {
        var sequenceKey = kind == RequestKind.GroupJoin ? "notification_seq" : "invitation_seq";
        if (request.GetLong(sequenceKey) is not { } sequence || request.GetId("group_id") is not { } groupId)
        {
            return Invalid($"Missing {sequenceKey} or group_id");
        }

        if (kind == RequestKind.GroupJoin)
        {
            kind = request.GetText("notification_type") switch
            {
                "join_request" => RequestKind.GroupJoin,
                "invited_join_request" => RequestKind.GroupInvitedJoin,
                _ => (RequestKind)(-1),
            };
            if (!Enum.IsDefined(kind)) return Invalid("Unknown notification_type");
        }

        var filtered = kind != RequestKind.GroupInvite && (request.GetBoolean("is_filtered") ?? false);
        var pending = await _store.GetPendingRequestsAsync(SelfId, kind, cancellationToken).ConfigureAwait(false);
        var match = pending.FirstOrDefault(item =>
            item.GroupId == groupId && item.IsFiltered == filtered
                && MilkyEntityEncoder.NotificationSequence(item) == sequence);
        if (match is null)
        {
            throw Missing(
                PlatformError.RequestNotFound,
                $"Request not found: {sequence}",
                sequence.ToString(CultureInfo.InvariantCulture));
        }

        await _platform.ResolveRequestAsync(
            match.Flag,
            approve,
            request.GetText("reason") ?? string.Empty,
            cancellationToken: cancellationToken,
            expectedSelfId: SelfId,
            expectedRequestId: match.Id).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private static bool TryGroupMember(ProtocolCall request, out string groupId, out string userId)
    {
        groupId = request.GetId("group_id") ?? string.Empty;
        userId = request.GetId("user_id") ?? string.Empty;
        return groupId.Length > 0 && userId.Length > 0;
    }

    private static ChatScene? ParseScene(string? value) => value switch
    {
        "friend" => ChatScene.Friend,
        "group" => ChatScene.Group,
        "temp" => ChatScene.Temp,
        _ => null,
    };

    private static string HttpAvatar(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        ? uri.AbsoluteUri
        : string.Empty;

    private static JsonNode Uin(string value) => MilkyEntityEncoder.Uin(value);

    private static ProtocolReply Invalid(string message) => new(-400, Message: message);

    private static PlatformException Missing(PlatformError error, string message, string resourceId) => new(error, message)
    {
        ResourceId = resourceId,
    };
}
