using System.Text.Json.Nodes;
using Matcha.Core;

namespace Matcha.Protocols;

internal sealed class MilkyEventEncoder(
    string selfId,
    MatchaStore store,
    MilkySegmentCodec segments,
    MilkyEntityEncoder entities)
{
    internal async Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        var time = domainEvent.Time.ToUnixTimeSeconds();
        JsonObject? payload;
        switch (domainEvent.Payload)
        {
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
                    payload = message is null
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
            case GroupMemberAddedEvent added:
                {
                    var data = new JsonObject
                    {
                        ["group_id"] = Uin(added.Change.GroupId),
                        ["user_id"] = Uin(added.Change.UserId),
                    };
                    if (added.Change.Reason == GroupMemberChangeReason.Invited)
                    {
                        data["invitor_id"] = Uin(added.Change.OperatorId);
                    }
                    else if (added.Change.Reason == GroupMemberChangeReason.Administrative)
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
                payload = EncodeRequest(request.Request, time);
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
                            ["reaction_type"] = "face",
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
                    ["file_id"] = file.Upload.Asset.Id,
                    ["file_name"] = file.Upload.Asset.Name,
                    ["file_size"] = file.Upload.Asset.ByteCount,
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
                ["initiator_uid"] = request.Flag,
                ["comment"] = request.Comment,
                ["via"] = "matcha",
            }),
            RequestKind.GroupJoin => Envelope(time, "group_join_request", new JsonObject
            {
                ["group_id"] = Uin(request.GroupId ?? "0"),
                ["notification_seq"] = MilkyEntityEncoder.NotificationSequence(request),
                ["is_filtered"] = false,
                ["initiator_id"] = Uin(request.RequesterId),
                ["comment"] = request.Comment,
            }),
            RequestKind.GroupInvite => Envelope(time, "group_invitation", new JsonObject
            {
                ["group_id"] = Uin(request.GroupId ?? "0"),
                ["invitation_seq"] = MilkyEntityEncoder.NotificationSequence(request),
                ["initiator_id"] = Uin(request.RequesterId),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
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
