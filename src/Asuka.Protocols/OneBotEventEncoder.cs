using System.Text.Json.Nodes;
using System.Text.Json;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

internal sealed class OneBotEventEncoder(
    OneBotVersion version,
    string selfId,
    OneBotSegmentCodec segments,
    OneBotContext context)
{
    internal async Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        var time = domainEvent.Time.ToUnixTimeSeconds();
        JsonObject? payload = domainEvent.Payload switch
        {
            MessageEvent message when version == OneBotVersion.V11 || message.Message.Anonymous is null =>
                await EncodeMessageAsync(message.Message, time, cancellationToken).ConfigureAwait(false),
            MessageRecalledEvent recalled when version == OneBotVersion.V11 || recalled.Detail.Anonymous is null =>
                EncodeRecall(recalled.Detail, time),
            GroupMemberAddedEvent added => EncodeMemberChange(added.Change, time, joined: true),
            GroupMemberRemovedEvent removed => EncodeMemberChange(removed.Change, time, joined: false),
            GroupDisbandedEvent disbanded => EncodeDisband(disbanded, time),
            GroupAdminChangedEvent admin => EncodeAdminChange(admin.Change, time),
            GroupMutedEvent muted => EncodeMute(muted.Mute, time),
            GroupNameChangedEvent => null,
            FriendAddedEvent added when version == OneBotVersion.V11 =>
                Merge(Base(time, "notice", "friend_add"), ("user_id", Id(added.UserId))),
            FriendAddedEvent added when version == OneBotVersion.V12 =>
                Merge(Base(time, "notice", "friend_increase"), ("user_id", added.UserId)),
            FriendRemovedEvent removed when version == OneBotVersion.V12 =>
                Merge(Base(time, "notice", "friend_decrease"), ("user_id", removed.UserId)),
            RequestReceivedEvent request => EncodeRequest(request.Request, time),
            PokeEvent poke when version == OneBotVersion.V11 && poke.Poke.Scene == ChatScene.Group => EncodePoke(poke.Poke, time),
            GroupFileUploadedEvent upload when version == OneBotVersion.V11 => EncodeGroupFile(upload.Upload, time),
            GroupHonorChangedEvent honor when version == OneBotVersion.V11 => EncodeGroupHonor(honor.Change, time),
            GroupLuckyKingEvent lucky when version == OneBotVersion.V11 => Merge(
                Base(time, "notice", "notify"), ("sub_type", "lucky_king"),
                ("group_id", Id(lucky.Result.GroupId)), ("user_id", Id(lucky.Result.SenderId)), ("target_id", Id(lucky.Result.TargetId))),
            _ => null,
        };

        if (payload is not null && version == OneBotVersion.V12)
        {
            payload["id"] = domainEvent.Id;
            payload["time"] = domainEvent.Time.ToUnixTimeMilliseconds() / 1000d;
        }

        return payload is null ? [] : [new OutboundFrame(payload)];
    }

    private async Task<JsonObject> EncodeMessageAsync(
        Message message,
        long time,
        CancellationToken cancellationToken)
    {
        var encodedSegments = await segments.EncodeAsync(message.Content, cancellationToken).ConfigureAwait(false);
        var preview = message.Content.TextPreview();
        if (version == OneBotVersion.V11)
        {
            var payload = new JsonObject
            {
                ["time"] = time,
                ["self_id"] = Id(selfId),
                ["post_type"] = "message",
                ["message_type"] = message.Scene == ChatScene.Group ? "group" : "private",
                ["sub_type"] = message.Scene switch
                {
                    ChatScene.Group => message.Anonymous is null ? "normal" : "anonymous",
                    ChatScene.Friend => "friend",
                    _ => "other",
                },
                ["message_id"] = Id(message.Id),
                ["user_id"] = message.Anonymous is { } anonymousSender ? JsonValue.Create(anonymousSender.Id) : Id(message.SenderId),
                ["message"] = encodedSegments,
                ["raw_message"] = OneBotSegmentCodec.ToCqString(encodedSegments),
                ["font"] = 0,
                ["sender"] = message.Anonymous is { } anonymous ? OneBotAnonymousEncoder.Sender(anonymous) : await context.SenderInfoAsync(
                    message.SenderId,
                    message.Scene == ChatScene.Group ? message.PeerId : null,
                    cancellationToken).ConfigureAwait(false),
            };
            if (message.Scene == ChatScene.Group)
            {
                payload["group_id"] = Id(message.PeerId);
                payload["anonymous"] = message.Anonymous is { } identity ? OneBotAnonymousEncoder.Identity(identity) : null;
            }

            return payload;
        }

        var result = new JsonObject
        {
            ["id"] = message.Id,
            ["time"] = time,
            ["type"] = "message",
            ["detail_type"] = message.Scene == ChatScene.Group ? "group" : "private",
            ["sub_type"] = string.Empty,
            ["self"] = SelfObject(),
            ["message_id"] = message.Id,
            ["user_id"] = message.SenderId,
            ["message"] = encodedSegments,
            ["alt_message"] = preview,
        };
        if (message.Scene == ChatScene.Group)
        {
            result["group_id"] = message.PeerId;
        }

        return result;
    }

    private JsonObject EncodeRecall(MessageRecalled recall, long time)
    {
        if (version == OneBotVersion.V11)
        {
            return recall.Scene == ChatScene.Group
                ? Merge(
                    Base(time, "notice", "group_recall"),
                    ("group_id", Id(recall.PeerId)),
                    ("user_id", recall.Anonymous is { } anonymous ? JsonValue.Create(anonymous.Id) : Id(recall.SenderId)),
                    ("operator_id", recall.Anonymous is { } selfRecall && recall.OperatorId == recall.SenderId
                        ? JsonValue.Create(selfRecall.Id) : Id(recall.OperatorId)),
                    ("message_id", Id(recall.MessageId)))
                : Merge(
                    Base(time, "notice", "friend_recall"),
                    ("user_id", Id(recall.SenderId)),
                    ("message_id", Id(recall.MessageId)));
        }

        var result = Merge(
            Base(
                time,
                "notice",
                recall.Scene == ChatScene.Group ? "group_message_delete" : "private_message_delete"),
            ("message_id", recall.MessageId),
            ("user_id", recall.SenderId),
            ("operator_id", recall.OperatorId));
        if (recall.Scene == ChatScene.Group)
        {
            result["group_id"] = recall.PeerId;
            result["sub_type"] = recall.OperatorId == recall.SenderId ? "recall" : "delete";
        }
        else
        {
            result.Remove("operator_id");
        }

        return result;
    }

    private JsonObject EncodeDisband(GroupDisbandedEvent disbanded, long time)
    {
        var selfDisbanded = disbanded.OperatorId == selfId;
        var result = EncodeMemberChange(new GroupMemberChange(disbanded.GroupId, selfId, disbanded.OperatorId,
            selfDisbanded ? GroupMemberChangeReason.Voluntary : GroupMemberChangeReason.Administrative), time, joined: false);
        // V12 reserves an empty subtype for a departure other than a regular leave or kick.
        if (version == OneBotVersion.V12 && !selfDisbanded) result["sub_type"] = string.Empty;
        return result;
    }

    private JsonObject EncodeMemberChange(GroupMemberChange change, long time, bool joined)
    {
        if (version == OneBotVersion.V11)
        {
            var subType = joined
                ? change.Reason == GroupMemberChangeReason.Invited ? "invite" : "approve"
                : change.Reason == GroupMemberChangeReason.Voluntary
                    ? "leave"
                    : change.UserId == selfId ? "kick_me" : "kick";
            return Merge(
                Base(time, "notice", joined ? "group_increase" : "group_decrease"),
                ("sub_type", subType),
                ("group_id", Id(change.GroupId)),
                ("operator_id", Id(change.OperatorId)),
                ("user_id", Id(change.UserId)));
        }

        var v12SubType = joined
            ? change.Reason == GroupMemberChangeReason.Invited || change.InviterId is not null ? "invite" : "join"
            : change.Reason == GroupMemberChangeReason.Voluntary ? "leave" : "kick";
        return Merge(
            Base(time, "notice", joined ? "group_member_increase" : "group_member_decrease"),
            ("sub_type", v12SubType),
            ("group_id", change.GroupId),
            ("operator_id", change.OperatorId),
            ("user_id", change.UserId));
    }

    private JsonObject? EncodeAdminChange(GroupAdminChange change, long time)
    {
        return version == OneBotVersion.V11
            ? Merge(
                Base(time, "notice", "group_admin"),
                ("sub_type", change.Granted ? "set" : "unset"),
                ("group_id", Id(change.GroupId)),
                ("user_id", Id(change.UserId)))
            : null;
    }

    private JsonObject? EncodeMute(GroupMute mute, long time)
    {
        return version == OneBotVersion.V11 && mute.UserId is not null
            ? Merge(
                Base(time, "notice", "group_ban"),
                ("sub_type", mute.Muted ? "ban" : "lift_ban"),
                ("group_id", Id(mute.GroupId)),
                ("user_id", Id(mute.UserId ?? "0")),
                ("operator_id", Id(mute.OperatorId)),
                ("duration", Math.Max(0, (long)mute.Duration.TotalSeconds)))
            : null;
    }

    private JsonObject EncodePoke(PokeInteraction poke, long time)
    {
        var result = Merge(
            Base(time, "notice", "notify"),
            ("sub_type", "poke"),
            ("user_id", Id(poke.SenderId)),
            ("target_id", Id(poke.TargetId)));
        if (poke.Scene == ChatScene.Group)
        {
            result["group_id"] = Id(poke.PeerId);
        }

        return result;
    }

    private JsonObject? EncodeGroupHonor(GroupHonorChange change, long time)
    {
        var type = change.Type switch
        {
            GroupHonorType.Talkative => "talkative",
            GroupHonorType.Performer => "performer",
            GroupHonorType.Emotion => "emotion",
            _ => null,
        };
        return type is null ? null : Merge(Base(time, "notice", "notify"), ("sub_type", "honor"),
            ("group_id", Id(change.GroupId)), ("honor_type", type), ("user_id", Id(change.UserId)));
    }

    private JsonObject EncodeGroupFile(GroupFileUpload upload, long time)
    {
        return Merge(
            Base(time, "notice", "group_upload"),
            ("group_id", Id(upload.GroupId)),
            ("user_id", Id(upload.UserId)),
            ("file", new JsonObject
            {
                ["id"] = upload.FileId ?? upload.Asset.Id,
                ["name"] = upload.FileName ?? upload.Asset.Name,
                ["size"] = upload.Asset.ByteCount,
                ["busid"] = 0,
            }));
    }

    private JsonObject? EncodeRequest(PendingRequest request, long time)
    {
        if (version == OneBotVersion.V11)
        {
            return request.Kind == RequestKind.Friend
                ? Merge(
                    Base(time, "request", "friend"),
                    ("user_id", Id(request.RequesterId)),
                    ("comment", request.Comment),
                    ("flag", request.Flag))
                : Merge(
                    Base(time, "request", "group"),
                    ("sub_type", request.Kind is RequestKind.GroupJoin or RequestKind.GroupInvitedJoin ? "add" : "invite"),
                    ("group_id", Id(request.GroupId ?? "0")),
                    ("user_id", Id(request.Kind == RequestKind.GroupInvitedJoin ? request.TargetUserId ?? request.RequesterId : request.RequesterId)),
                    ("comment", request.Comment),
                    ("flag", request.Flag));
        }

        return null;
    }

    private JsonObject Base(long time, string type, string detailType)
    {
        if (version == OneBotVersion.V11)
        {
            return new JsonObject
            {
                ["time"] = time,
                ["self_id"] = Id(selfId),
                ["post_type"] = type,
                [type == "message" ? "message_type" : $"{type}_type"] = detailType,
            };
        }

        return new JsonObject
        {
            ["id"] = IdGenerator.RequestId(),
            ["time"] = time,
            ["type"] = type,
            ["detail_type"] = detailType,
            ["sub_type"] = string.Empty,
            ["self"] = SelfObject(),
        };
    }

    private JsonObject SelfObject() => new()
    {
        ["platform"] = "asuka",
        ["user_id"] = selfId,
    };

    private JsonNode Id(string value) => version == OneBotVersion.V11
        ? JsonExtensions.NumericId(value)
        : System.Text.Json.Nodes.JsonValue.Create(value)!;

    private static JsonObject Merge(JsonObject target, params (string Key, object? Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            target[key] = value switch
            {
                null => null,
                JsonNode node => node,
                string text => text,
                bool boolean => boolean,
                int integer => integer,
                long integer => integer,
                double number => number,
                _ => JsonSerializer.SerializeToNode(value),
            };
        }

        return target;
    }
}
