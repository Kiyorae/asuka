using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed class OneBotProtocol : IProtocolImplementation
{
    private const string ImplementationName = "asuka";
    private const string CurrentVersion = "0.1.0.1";
    private static readonly FrozenSet<string> Actions = new[]
    {
        "send_msg", "send_private_msg", "send_group_msg", "send_message",
        "delete_msg", "delete_message", "get_msg", "get_message",
        "get_login_info", "get_self_info", "get_stranger_info", "get_user_info", "get_friend_list",
        "get_group_info", "get_group_list", "get_group_member_info", "get_group_member_list",
        "set_group_name", "set_group_card", "set_group_special_title", "set_group_admin",
        "set_group_ban", "set_group_whole_ban", "set_group_kick", "set_group_leave", "leave_group",
        "set_friend_add_request", "set_group_add_request",
        "get_status", "get_version_info", "get_version", "get_supported_actions",
        "can_send_image", "can_send_record",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly PlatformService _platform;
    private readonly AsukaStore _store;
    private readonly OneBotContext _context;
    private readonly OneBotSegmentCodec _segments;
    private readonly OneBotEventEncoder _events;

    public OneBotProtocol(
        OneBotVersion version,
        string selfId,
        PlatformService platform,
        IProtocolAssetResolver assetResolver)
    {
        Version = version;
        SelfId = selfId;
        _platform = platform;
        _store = platform.Store;
        _context = new OneBotContext(_store);
        _segments = new OneBotSegmentCodec(version, assetResolver);
        _events = new OneBotEventEncoder(version, selfId, _segments, _context);
    }

    public OneBotVersion Version { get; }

    public string Identifier => "onebot";

    public string DisplayName => Version == OneBotVersion.V11
        ? "OneBot V11 Standard"
        : "OneBot V12 Standard";

    public string SelfId { get; }

    public IReadOnlySet<TransportMode> SupportedTransports { get; } =
        new HashSet<TransportMode> { TransportMode.WebSocketServer, TransportMode.WebSocketClient };

    public WebSocketClientHandshake ClientHandshake => Version == OneBotVersion.V11
        ? new WebSocketClientHandshake(
            "/onebot/v11/ws",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Self-ID"] = SelfId,
                ["X-Client-Role"] = "Universal",
                ["User-Agent"] = $"Asuka/{CurrentVersion}",
            },
            [])
        : new WebSocketClientHandshake(
            "/onebot/v12/ws",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = $"Asuka/{CurrentVersion}",
            },
            ["12.asuka"]);

    public TimeSpan? HeartbeatInterval => TimeSpan.FromSeconds(15);

    public async Task<ProtocolReply> HandleAsync(
        ProtocolCall request,
        CancellationToken cancellationToken = default)
    {
        var action = Version == OneBotVersion.V11 && request.Name.EndsWith("_async", StringComparison.Ordinal)
            ? request.Name[..^6]
            : request.Name;
        if (!Actions.Contains(action))
        {
            return Unsupported(action);
        }

        try
        {
            return action switch
            {
                "send_msg" or "send_message" => await SendMessageAsync(request, null, cancellationToken).ConfigureAwait(false),
                "send_private_msg" => await SendMessageAsync(request, ChatScene.Friend, cancellationToken).ConfigureAwait(false),
                "send_group_msg" => await SendMessageAsync(request, ChatScene.Group, cancellationToken).ConfigureAwait(false),
                "delete_msg" or "delete_message" => await DeleteMessageAsync(request, cancellationToken).ConfigureAwait(false),
                "get_msg" or "get_message" => await GetMessageAsync(request, cancellationToken).ConfigureAwait(false),
                "get_login_info" or "get_self_info" => await LoginInfoAsync(cancellationToken).ConfigureAwait(false),
                "get_stranger_info" or "get_user_info" => await UserInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_friend_list" => await FriendListAsync(cancellationToken).ConfigureAwait(false),
                "get_group_info" => await GroupInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_list" => await GroupListAsync(cancellationToken).ConfigureAwait(false),
                "get_group_member_info" => await MemberInfoAsync(request, cancellationToken).ConfigureAwait(false),
                "get_group_member_list" => await MemberListAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_name" => await SetGroupNameAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_card" => await SetGroupCardAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_special_title" => await SetGroupTitleAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_admin" => await SetGroupAdminAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_ban" => await SetGroupBanAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_whole_ban" => await SetGroupWholeBanAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_kick" => await KickMemberAsync(request, cancellationToken).ConfigureAwait(false),
                "set_group_leave" or "leave_group" => await LeaveGroupAsync(request, cancellationToken).ConfigureAwait(false),
                "set_friend_add_request" or "set_group_add_request" =>
                    await ResolveRequestAsync(request, cancellationToken).ConfigureAwait(false),
                "get_status" => Status(),
                "get_version_info" or "get_version" => VersionInfo(),
                "get_supported_actions" => SupportedActions(),
                "can_send_image" or "can_send_record" => ProtocolReply.Success(new JsonObject { ["yes"] = true }),
                _ => Unsupported(action),
            };
        }
        catch (PlatformException error)
        {
            return Failure(error);
        }
        catch (ArgumentException error)
        {
            return Invalid(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ProtocolReply(Version == OneBotVersion.V11 ? 1000 : 20002, Message: error.Message);
        }
    }

    public JsonObject CreateEnvelope(ProtocolReply reply, JsonNode? echo = null)
    {
        var payload = new JsonObject
        {
            ["status"] = reply.IsSuccess ? "ok" : "failed",
            ["retcode"] = reply.RetCode,
            ["data"] = reply.IsSuccess ? reply.EffectiveData.DeepClone() : null,
        };
        if (Version == OneBotVersion.V11)
        {
            if (!reply.IsSuccess && !string.IsNullOrEmpty(reply.Message))
            {
                payload["data"] = new JsonObject { ["message"] = reply.Message };
            }
        }
        else
        {
            payload["message"] = reply.Message;
        }

        if (echo is not null)
        {
            payload["echo"] = echo.DeepClone();
        }

        return payload;
    }

    public Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        return domainEvent.SelfId == SelfId
            ? _events.EncodeAsync(domainEvent, cancellationToken)
            : Task.FromResult<IReadOnlyList<OutboundFrame>>([]);
    }

    public Task<IReadOnlyList<OutboundFrame>> GetHandshakeFramesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IReadOnlyList<OutboundFrame> result = Version == OneBotVersion.V11
            ? [new OutboundFrame(new JsonObject
            {
                ["time"] = time,
                ["self_id"] = JsonExtensions.NumericId(SelfId),
                ["post_type"] = "meta_event",
                ["meta_event_type"] = "lifecycle",
                ["sub_type"] = "connect",
            })]
            : [
                new OutboundFrame(new JsonObject
                {
                    ["id"] = IdGenerator.RequestId(),
                    ["time"] = time,
                    ["type"] = "meta",
                    ["detail_type"] = "connect",
                    ["sub_type"] = string.Empty,
                    ["self"] = SelfObject(),
                    ["version"] = VersionPayload(),
                }),
                new OutboundFrame(new JsonObject
                {
                    ["id"] = IdGenerator.RequestId(),
                    ["time"] = time,
                    ["type"] = "meta",
                    ["detail_type"] = "status_update",
                    ["sub_type"] = string.Empty,
                    ["self"] = SelfObject(),
                    ["status"] = StatusPayload(),
                }),
            ];
        return Task.FromResult(result);
    }

    public Task<OutboundFrame?> GetHeartbeatFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OutboundFrame frame = Version == OneBotVersion.V11
            ? new OutboundFrame(new JsonObject
            {
                ["time"] = time,
                ["self_id"] = JsonExtensions.NumericId(SelfId),
                ["post_type"] = "meta_event",
                ["meta_event_type"] = "heartbeat",
                ["status"] = StatusPayload(),
                ["interval"] = 15_000,
            })
            : new OutboundFrame(new JsonObject
            {
                ["id"] = IdGenerator.RequestId(),
                ["time"] = time,
                ["type"] = "meta",
                ["detail_type"] = "heartbeat",
                ["sub_type"] = string.Empty,
                ["self"] = SelfObject(),
                ["interval"] = 15_000,
                ["status"] = StatusPayload(),
            });
        return Task.FromResult<OutboundFrame?>(frame);
    }

    private async Task<ProtocolReply> SendMessageAsync(
        ProtocolCall call,
        ChatScene? forcedScene,
        CancellationToken cancellationToken)
    {
        var scene = forcedScene ?? ResolveScene(call);
        var peerId = call.GetId(scene == ChatScene.Group ? "group_id" : "user_id");
        if (peerId is null)
        {
            return Invalid(scene == ChatScene.Group ? "Missing group_id" : "Missing user_id");
        }

        var raw = call.Parameters["message"];
        IReadOnlyList<MessageSegment> content = raw switch
        {
            JsonArray array => await _segments.DecodeAsync(array, cancellationToken).ConfigureAwait(false),
            System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) => [new TextSegment(text)],
            _ => [],
        };
        if (content.Count == 0)
        {
            return Invalid(raw is null ? "Missing message" : "Message content is empty");
        }

        var message = await _platform.SendMessageAsync(
            scene,
            peerId,
            SelfId,
            SelfId,
            content,
            cancellationToken).ConfigureAwait(false);
        return Version == OneBotVersion.V11
            ? ProtocolReply.Success(new JsonObject { ["message_id"] = JsonExtensions.NumericId(message.Id) })
            : ProtocolReply.Success(new JsonObject
            {
                ["message_id"] = message.Id,
                ["time"] = message.Time.ToUnixTimeSeconds(),
            });
    }

    private async Task<ProtocolReply> DeleteMessageAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var messageId = call.GetId("message_id");
        if (messageId is null)
        {
            return Invalid("Missing message_id");
        }

        _ = await _platform.RecallMessageAsync(messageId, SelfId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GetMessageAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var messageId = call.GetId("message_id");
        if (messageId is null)
        {
            return Invalid("Missing message_id");
        }

        var message = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformException(PlatformError.MessageNotFound, $"Message not found: {messageId}")
            {
                ResourceId = messageId,
            };
        var encoded = await _segments.EncodeAsync(message.Content, cancellationToken).ConfigureAwait(false);
        if (Version == OneBotVersion.V11)
        {
            return ProtocolReply.Success(new JsonObject
            {
                ["time"] = message.Time.ToUnixTimeSeconds(),
                ["message_type"] = message.Scene == ChatScene.Group ? "group" : "private",
                ["message_id"] = JsonExtensions.NumericId(message.Id),
                ["real_id"] = JsonExtensions.NumericId(message.Id),
                ["sender"] = await _context.SenderInfoAsync(
                    message.SenderId,
                    message.Scene == ChatScene.Group ? message.PeerId : null,
                    cancellationToken).ConfigureAwait(false),
                ["message"] = encoded,
            });
        }

        var result = new JsonObject
        {
            ["message_id"] = message.Id,
            ["time"] = message.Time.ToUnixTimeSeconds(),
            ["message_type"] = message.Scene == ChatScene.Group ? "group" : "private",
            ["user_id"] = message.SenderId,
            ["message"] = encoded,
            ["alt_message"] = message.Content.TextPreview(),
        };
        if (message.Scene == ChatScene.Group)
        {
            result["group_id"] = message.PeerId;
        }

        return ProtocolReply.Success(result);
    }

    private async Task<ProtocolReply> LoginInfoAsync(CancellationToken cancellationToken)
    {
        var user = await _store.GetUserAsync(SelfId, cancellationToken).ConfigureAwait(false);
        var nickname = user?.DisplayName ?? SelfId;
        return ProtocolReply.Success(Version == OneBotVersion.V11
            ? new JsonObject { ["user_id"] = JsonExtensions.NumericId(SelfId), ["nickname"] = nickname }
            : new JsonObject
            {
                ["user_id"] = SelfId,
                ["user_name"] = nickname,
                ["user_displayname"] = nickname,
            });
    }

    private async Task<ProtocolReply> UserInfoAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var userId = call.GetId("user_id");
        if (userId is null)
        {
            return Invalid("Missing user_id");
        }

        var info = await _context.UserInfoAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformException(PlatformError.UserNotFound, $"User not found: {userId}")
            {
                UserId = userId,
            };
        if (Version == OneBotVersion.V11)
        {
            info["user_id"] = JsonExtensions.NumericId(userId);
        }

        return ProtocolReply.Success(info);
    }

    private async Task<ProtocolReply> FriendListAsync(CancellationToken cancellationToken)
    {
        var friends = await _store.GetFriendsAsync(SelfId, cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var entry in friends)
        {
            result.Add(Version == OneBotVersion.V11
                ? new JsonObject
                {
                    ["user_id"] = JsonExtensions.NumericId(entry.User.Id),
                    ["nickname"] = entry.User.DisplayName,
                    ["remark"] = entry.Friendship.Remark,
                }
                : new JsonObject
                {
                    ["user_id"] = entry.User.Id,
                    ["user_name"] = entry.User.DisplayName,
                    ["user_displayname"] = entry.Friendship.Remark,
                    ["user_remark"] = entry.Friendship.Remark,
                });
        }

        return ProtocolReply.Success(result);
    }

    private async Task<ProtocolReply> GroupInfoAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var groupId = call.GetId("group_id");
        if (groupId is null)
        {
            return Invalid("Missing group_id");
        }

        var info = await _context.GroupInfoAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}")
            {
                GroupId = groupId,
            };
        if (Version == OneBotVersion.V11)
        {
            info["group_id"] = JsonExtensions.NumericId(groupId);
        }

        return ProtocolReply.Success(info);
    }

    private async Task<ProtocolReply> GroupListAsync(CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var group in await _store.GetAllGroupsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await _context.GroupInfoAsync(group.Id, cancellationToken).ConfigureAwait(false) is { } info)
            {
                if (Version == OneBotVersion.V11)
                {
                    info["group_id"] = JsonExtensions.NumericId(group.Id);
                }

                result.Add(info);
            }
        }

        return ProtocolReply.Success(result);
    }

    private async Task<ProtocolReply> MemberInfoAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var groupId = call.GetId("group_id");
        var userId = call.GetId("user_id");
        if (groupId is null || userId is null)
        {
            return Invalid("Missing group_id or user_id");
        }

        var info = await _context.MemberInfoAsync(groupId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformException(
                PlatformError.NotAMember,
                $"User {userId} is not a member of group {groupId}")
            {
                GroupId = groupId,
                UserId = userId,
            };
        if (Version == OneBotVersion.V11)
        {
            info["group_id"] = JsonExtensions.NumericId(groupId);
            info["user_id"] = JsonExtensions.NumericId(userId);
        }

        return ProtocolReply.Success(info);
    }

    private async Task<ProtocolReply> MemberListAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var groupId = call.GetId("group_id");
        if (groupId is null)
        {
            return Invalid("Missing group_id");
        }

        if (await _store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}")
            {
                GroupId = groupId,
            };
        }

        var result = new JsonArray();
        foreach (var member in await _store.GetMembersAsync(groupId, cancellationToken).ConfigureAwait(false))
        {
            if (await _context.MemberInfoAsync(groupId, member.UserId, cancellationToken).ConfigureAwait(false) is { } info)
            {
                if (Version == OneBotVersion.V11)
                {
                    info["group_id"] = JsonExtensions.NumericId(groupId);
                    info["user_id"] = JsonExtensions.NumericId(member.UserId);
                }

                result.Add(info);
            }
        }

        return ProtocolReply.Success(result);
    }

    private async Task<ProtocolReply> SetGroupNameAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetText("group_name") is not { } name)
        {
            return Invalid("Missing group_id or group_name");
        }

        await _platform.SetGroupNameAsync(groupId, SelfId, name, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupCardAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetId("user_id") is not { } userId)
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetMemberCardAsync(
            groupId,
            userId,
            SelfId,
            call.GetText("card") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupTitleAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetId("user_id") is not { } userId)
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetMemberTitleAsync(
            groupId,
            userId,
            SelfId,
            call.GetText("special_title") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupAdminAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetId("user_id") is not { } userId)
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.SetAdminAsync(
            groupId,
            userId,
            SelfId,
            call.GetBoolean("enable") ?? true,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupBanAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetId("user_id") is not { } userId)
        {
            return Invalid("Missing group_id or user_id");
        }

        await _platform.MuteMemberAsync(
            groupId,
            userId,
            SelfId,
            TimeSpan.FromSeconds(call.GetLong("duration") ?? 1800),
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetGroupWholeBanAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId)
        {
            return Invalid("Missing group_id");
        }

        await _platform.SetWholeMuteAsync(
            groupId,
            SelfId,
            call.GetBoolean("enable") ?? true,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> KickMemberAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId || call.GetId("user_id") is not { } userId)
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

    private async Task<ProtocolReply> LeaveGroupAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetId("group_id") is not { } groupId)
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

    private async Task<ProtocolReply> ResolveRequestAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (call.GetText("flag") is not { } flag)
        {
            return Invalid("Missing flag");
        }

        await _platform.ResolveRequestAsync(
            flag,
            call.GetBoolean("approve") ?? true,
            call.GetText("reason") ?? string.Empty,
            call.GetText("remark") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private ProtocolReply Status() => ProtocolReply.Success(StatusPayload());

    private ProtocolReply VersionInfo() => ProtocolReply.Success(VersionPayload());

    private static ProtocolReply SupportedActions()
    {
        var result = new JsonArray();
        foreach (var action in Actions.Order(StringComparer.Ordinal))
        {
            result.Add(action);
        }

        return ProtocolReply.Success(result);
    }

    private static ChatScene ResolveScene(ProtocolCall call)
    {
        var declared = call.GetText("message_type") ?? call.GetText("detail_type");
        return declared switch
        {
            "group" => ChatScene.Group,
            "private" => ChatScene.Friend,
            _ => call.GetId("group_id") is null ? ChatScene.Friend : ChatScene.Group,
        };
    }

    private JsonObject SelfObject() => new()
    {
        ["platform"] = "asuka",
        ["user_id"] = SelfId,
    };

    private JsonObject VersionPayload() => Version == OneBotVersion.V11
        ? new JsonObject
        {
            ["app_name"] = ImplementationName,
            ["app_version"] = CurrentVersion,
            ["protocol_version"] = "v11",
        }
        : new JsonObject
        {
            ["impl"] = ImplementationName,
            ["version"] = CurrentVersion,
            ["onebot_version"] = "12",
        };

    private JsonObject StatusPayload() => Version == OneBotVersion.V11
        ? new JsonObject { ["online"] = true, ["good"] = true }
        : new JsonObject
        {
            ["good"] = true,
            ["bots"] = new JsonArray
            {
                new JsonObject
                {
                    ["self"] = SelfObject(),
                    ["online"] = true,
                },
            },
        };

    private ProtocolReply Invalid(string detail) => new(
        Version == OneBotVersion.V11 ? 1400 : 10003,
        Message: detail);

    private ProtocolReply Unsupported(string action) => new(
        Version == OneBotVersion.V11 ? 1404 : 10002,
        Message: $"Unsupported action: {action}");

    private ProtocolReply Failure(PlatformException error)
    {
        var notFound = error.Error is PlatformError.UserNotFound
            or PlatformError.GroupNotFound
            or PlatformError.MessageNotFound
            or PlatformError.RequestNotFound
            or PlatformError.NotAMember;
        var forbidden = error.Error is PlatformError.NotPermitted
            or PlatformError.Muted
            or PlatformError.WholeGroupMuted;
        var retCode = notFound
            ? 1404
            : forbidden
                ? Version == OneBotVersion.V11 ? 1403 : 34000
                : Version == OneBotVersion.V11 ? 1400 : 10003;
        return new ProtocolReply(retCode, Message: error.Message);
    }
}
