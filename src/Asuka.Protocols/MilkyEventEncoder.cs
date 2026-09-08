using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

internal sealed class MilkyEventEncoder(
    string selfId,
    AsukaStore store,
    MilkySegmentCodec segments,
    MilkyEntityEncoder entities)
{
    internal async Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        var time = domainEvent.Time.ToUnixTimeSeconds();
        JsonObject? payload;
        // Milky has no anonymous identity contract. A protocol switch must not
        // turn a stored anonymous event into a real-account message or recall.
        if (domainEvent.Payload is MessageEvent { Message.Anonymous: not null }
            or MessageRecalledEvent { Detail.Anonymous: not null }) return [];
        switch (domainEvent.Payload)
        {
            case BotPresenceChangedEvent changed:
                payload = changed.Presence.IsOnline ? null
                    : Envelope(time, "bot_offline", new JsonObject { ["reason"] = changed.Presence.Reason });
                break;
            case MessageEvent messageEvent:
                {
                    var encoded = await segments.EncodeIncomingAsync(
                        messageEvent.Message.Content,
                        cancellationToken).ConfigureAwait(false);
                    payload = Envelope(
                        time,
                        "message_receive",
                        await entities.IncomingMessageAsync(
                            messageEvent.Message,
                            encoded,
                            cancellationToken).ConfigureAwait(false));
                    break;
                }
            case MessageRecalledEvent recalled:
                {
                    var message = await store.GetMessageAsync(
                        recalled.Detail.MessageId,
                        cancellationToken).ConfigureAwait(false);
                    payload = message is null || message.Anonymous is not null
                        ? null
                        : Envelope(time, "message_recall", new JsonObject
                        {
                            ["message_scene"] = MilkyEntityEncoder.SceneName(recalled.Detail.Scene),
                            ["peer_id"] = Uin(recalled.Detail.PeerId),
                            ["message_seq"] = message.Seq,
                            ["sender_id"] = Uin(recalled.Detail.SenderId),
                            ["operator_id"] = Uin(recalled.Detail.OperatorId),
                            ["display_suffix"] = recalled.Detail.OperatorId == recalled.Detail.SenderId
                                ? "recalled a message"
                                : "recalled a member's message",
                        });
                    break;
                }
            case GroupDisbandedEvent disbanded:
                payload = Envelope(time, "group_disband", new JsonObject
                {
                    ["group_id"] = Uin(disbanded.GroupId),
                    ["operator_id"] = Uin(disbanded.OperatorId),
                });
                break;
            case GroupMemberAddedEvent added:
                {
                    var data = new JsonObject
                    {
                        ["group_id"] = Uin(added.Change.GroupId),
                        ["user_id"] = Uin(added.Change.UserId),
                    };
                    if (added.Change.InviterId is { } inviterId)
                    {
                        data["invitor_id"] = Uin(inviterId);
                    }
                    else if (added.Change.Reason == GroupMemberChangeReason.Invited)
                    {
                        data["invitor_id"] = Uin(added.Change.OperatorId);
                    }
                    if (added.Change.Reason == GroupMemberChangeReason.Administrative)
                    {
                        data["operator_id"] = Uin(added.Change.OperatorId);
                    }

                    payload = Envelope(time, "group_member_increase", data);
                    break;
                }
            case GroupMemberRemovedEvent removed:
                {
                    var data = new JsonObject
                    {
                        ["group_id"] = Uin(removed.Change.GroupId),
                        ["user_id"] = Uin(removed.Change.UserId),
                    };
                    if (removed.Change.Reason != GroupMemberChangeReason.Voluntary)
                    {
                        data["operator_id"] = Uin(removed.Change.OperatorId);
                    }

                    payload = Envelope(time, "group_member_decrease", data);
                    break;
                }
            case GroupAdminChangedEvent admin:
                payload = Envelope(time, "group_admin_change", new JsonObject
                {
                    ["group_id"] = Uin(admin.Change.GroupId),
                    ["user_id"] = Uin(admin.Change.UserId),
                    ["operator_id"] = Uin(admin.Change.OperatorId),
                    ["is_set"] = admin.Change.Granted,
                });
                break;
            case GroupMutedEvent muted when muted.Mute.UserId is { } userId:
                payload = Envelope(time, "group_mute", new JsonObject
                {
                    ["group_id"] = Uin(muted.Mute.GroupId),
                    ["user_id"] = Uin(userId),
                    ["operator_id"] = Uin(muted.Mute.OperatorId),
                    ["duration"] = Math.Max(0, (long)muted.Mute.Duration.TotalSeconds),
                });
                break;
            case GroupMutedEvent muted:
                payload = Envelope(time, "group_whole_mute", new JsonObject
                {
                    ["group_id"] = Uin(muted.Mute.GroupId),
                    ["operator_id"] = Uin(muted.Mute.OperatorId),
                    ["is_mute"] = muted.Mute.Muted,
                });
                break;
            case GroupNameChangedEvent renamed:
                payload = Envelope(time, "group_name_change", new JsonObject
                {
                    ["group_id"] = Uin(renamed.GroupId),
                    ["new_group_name"] = renamed.Name,
                    ["operator_id"] = Uin(renamed.OperatorId),
                });
                break;
            case FriendAddedEvent or FriendRemovedEvent:
                payload = null;
                break;
            case RequestReceivedEvent request:
                var currentRequest = request.Request.NotificationSequence == 0
                    ? await store.GetRequestAsync(request.Request.Id, cancellationToken).ConfigureAwait(false)
                        ?? request.Request
                    : request.Request;
                payload = EncodeRequest(currentRequest, time);
                break;
            case PokeEvent poke when poke.Poke.Scene == ChatScene.Group:
                payload = Envelope(time, "group_nudge", new JsonObject
                {
                    ["group_id"] = Uin(poke.Poke.PeerId),
                    ["sender_id"] = Uin(poke.Poke.SenderId),
                    ["receiver_id"] = Uin(poke.Poke.TargetId),
                    ["display_action"] = "nudged",
                    ["display_suffix"] = string.Empty,
                    ["display_action_img_url"] = string.Empty,
                });
                break;
            case PokeEvent poke:
                payload = Envelope(time, "friend_nudge", new JsonObject
                {
                    ["user_id"] = Uin(poke.Poke.PeerId),
                    ["is_self_send"] = poke.Poke.SenderId == selfId,
                    ["is_self_receive"] = poke.Poke.TargetId == selfId,
                    ["display_action"] = "nudged",
                    ["display_suffix"] = string.Empty,
                    ["display_action_img_url"] = string.Empty,
                });
                break;
            case MessageReactionEvent reaction when reaction.Reaction.Scene == ChatScene.Group:
                {
                    var message = await store.GetMessageAsync(
                        reaction.Reaction.MessageId,
                        cancellationToken).ConfigureAwait(false);
                    payload = message is null
                        ? null
                        : Envelope(time, "group_message_reaction", new JsonObject
                        {
                            ["group_id"] = Uin(reaction.Reaction.PeerId),
                            ["user_id"] = Uin(reaction.Reaction.UserId),
                            ["message_seq"] = message.Seq,
                            ["face_id"] = reaction.Reaction.Reaction,
                            ["reaction_type"] = reaction.Reaction.ReactionType,
                            ["is_add"] = reaction.Reaction.Added,
                        });
                    break;
                }
            case MessageReactionEvent:
                payload = null;
                break;
            case GroupFileUploadedEvent file:
                payload = Envelope(time, "group_file_upload", new JsonObject
                {
                    ["group_id"] = Uin(file.Upload.GroupId),
                    ["user_id"] = Uin(file.Upload.UserId),
                    ["file_id"] = file.Upload.FileId ?? file.Upload.Asset.Id,
                    ["file_name"] = file.Upload.FileName ?? file.Upload.Asset.Name,
                    ["file_size"] = file.Upload.Asset.ByteCount,
                });
                break;
            case FriendFileUploadedEvent file:
                payload = Envelope(time, "friend_file_upload", new JsonObject
                {
                    ["user_id"] = Uin(file.Upload.UserId),
                    ["is_self"] = file.Upload.SenderId == selfId,
                    ["file_id"] = file.Upload.File.Id,
                    ["file_name"] = file.Upload.File.Name,
                    ["file_size"] = file.Upload.File.Asset.ByteCount,
                    ["file_hash"] = file.Upload.File.FileHash,
                });
                break;
            case PeerPinChangedEvent pin:
                payload = Envelope(time, "peer_pin_change", new JsonObject
                {
                    ["message_scene"] = pin.Scene.ToString().ToLowerInvariant(),
                    ["peer_id"] = Uin(pin.PeerId),
                    ["is_pinned"] = pin.IsPinned,
                });
                break;
            case GroupEssenceMessageChangedEvent essence:
                payload = Envelope(time, "group_essence_message_change", new JsonObject
                {
                    ["group_id"] = Uin(essence.GroupId),
                    ["message_seq"] = essence.MessageSequence,
                    ["operator_id"] = Uin(essence.OperatorId),
                    ["is_set"] = essence.IsSet,
                });
                break;
            case ConnectedEvent:
                payload = null;
                break;
            case DisconnectedEvent:
                payload = Envelope(
                    time,
                    "bot_offline",
                    new JsonObject { ["reason"] = "Connection closed" });
                break;
            default:
                payload = null;
                break;
        }

        return payload is null ? [] : [new OutboundFrame(payload, "milky_event")];
    }

    private JsonObject EncodeRequest(PendingRequest request, long time)
    {
        return request.Kind switch
        {
            RequestKind.Friend => Envelope(time, "friend_request", new JsonObject
            {
                ["initiator_id"] = Uin(request.RequesterId),
                ["initiator_uid"] = request.RequesterId,
                ["comment"] = request.Comment,
                ["via"] = request.Via,
            }),
            RequestKind.GroupJoin => Envelope(time, "group_join_request", new JsonObject
            {
                ["group_id"] = Uin(request.GroupId ?? "0"),
                ["notification_seq"] = MilkyEntityEncoder.NotificationSequence(request),
                ["is_filtered"] = request.IsFiltered,
                ["initiator_id"] = Uin(request.RequesterId),
                ["comment"] = request.Comment,
            }),
            RequestKind.GroupInvite => EncodeGroupInvitation(request, time),
            RequestKind.GroupInvitedJoin => Envelope(time, "group_invited_join_request", new JsonObject
            {
                ["group_id"] = Uin(request.GroupId ?? "0"),
                ["notification_seq"] = MilkyEntityEncoder.NotificationSequence(request),
                ["initiator_id"] = Uin(request.RequesterId),
                ["target_user_id"] = Uin(request.TargetUserId
                    ?? throw new InvalidOperationException("An invited join request requires a target user")),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private JsonObject EncodeGroupInvitation(PendingRequest request, long time)
    {
        var data = new JsonObject
        {
            ["group_id"] = Uin(request.GroupId ?? "0"),
            ["invitation_seq"] = MilkyEntityEncoder.NotificationSequence(request),
            ["initiator_id"] = Uin(request.RequesterId),
        };
        if (request.SourceGroupId is { } sourceGroupId) data["source_group_id"] = Uin(sourceGroupId);
        return Envelope(time, "group_invitation", data);
    }

    private JsonObject Envelope(long time, string eventType, JsonObject data) => new()
    {
        ["time"] = time,
        ["self_id"] = Uin(selfId),
        ["event_type"] = eventType,
        ["data"] = data,
    };

    private static JsonNode Uin(string value) => MilkyEntityEncoder.Uin(value);
}
