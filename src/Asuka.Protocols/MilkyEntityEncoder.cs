using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

internal sealed class MilkyEntityEncoder(AsukaStore store)
{
    internal static JsonObject Friend(User user, string remark = "") => new()
    {
        ["user_id"] = Uin(user.Id),
        ["nickname"] = user.DisplayName,
        ["sex"] = user.Sex.ToString().ToLowerInvariant(),
        ["qid"] = string.Empty,
        ["remark"] = remark,
        ["category"] = new JsonObject { ["category_id"] = 0, ["category_name"] = "My Friends" },
    };

    internal async Task<JsonObject> GroupAsync(Group group, CancellationToken cancellationToken) => new()
    {
        ["group_id"] = Uin(group.Id),
        ["group_name"] = group.Name,
        ["member_count"] = await store.GetMemberCountAsync(group.Id, cancellationToken).ConfigureAwait(false),
        ["max_member_count"] = group.MaxMemberCount,
        ["remark"] = string.Empty,
        ["created_time"] = Timestamp(group.CreatedAt),
        ["description"] = group.Intro,
        ["question"] = string.Empty,
        ["announcement"] = string.Empty,
    };

    internal static JsonObject GroupMember(GroupMember member, User? user)
    {
        var result = new JsonObject
        {
            ["user_id"] = Uin(member.UserId),
            ["nickname"] = user?.DisplayName ?? member.UserId,
            ["sex"] = (user?.Sex ?? Sex.Unknown).ToString().ToLowerInvariant(),
            ["group_id"] = Uin(member.GroupId),
            ["card"] = member.Card,
            ["title"] = member.Title,
            ["level"] = 1,
            ["role"] = member.Role.ToString().ToLowerInvariant(),
            ["join_time"] = Timestamp(member.JoinedAt),
            ["last_sent_time"] = member.LastSentAt is { } lastSent ? Timestamp(lastSent) : 0,
        };
        if (member.MutedUntil is { } mutedUntil && mutedUntil > DateTimeOffset.UtcNow)
        {
            result["shut_up_end_time"] = Timestamp(mutedUntil);
        }

        return result;
    }

    internal async Task<JsonObject> IncomingMessageAsync(
        Message message,
        JsonArray segments,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["message_scene"] = SceneName(message.Scene),
            ["peer_id"] = Uin(message.PeerId),
            ["message_seq"] = message.Seq,
            ["sender_id"] = Uin(message.SenderId),
            ["time"] = Timestamp(message.Time),
            ["segments"] = segments,
        };
        if (message.Scene == ChatScene.Friend
            && await store.GetUserAsync(message.PeerId, cancellationToken).ConfigureAwait(false) is { } friend)
        {
            var relation = await store.GetFriendshipAsync(
                message.SelfId,
                message.PeerId,
                cancellationToken).ConfigureAwait(false);
            payload["friend"] = Friend(friend, relation?.Remark ?? string.Empty);
        }
        else if (message.Scene == ChatScene.Group)
        {
            if (await store.GetGroupAsync(message.PeerId, cancellationToken).ConfigureAwait(false) is { } group)
            {
                payload["group"] = await GroupAsync(group, cancellationToken).ConfigureAwait(false);
            }

            if (await store.GetMemberAsync(
                    message.PeerId,
                    message.SenderId,
                    cancellationToken).ConfigureAwait(false) is { } member)
            {
                var sender = await store.GetUserAsync(message.SenderId, cancellationToken).ConfigureAwait(false);
                payload["group_member"] = GroupMember(member, sender);
            }
        }

        return payload;
    }

    internal static JsonObject FriendRequest(PendingRequest request) => new()
    {
        ["time"] = Timestamp(request.Time),
        ["initiator_id"] = Uin(request.RequesterId),
        ["initiator_uid"] = request.Flag,
        ["target_user_id"] = Uin(request.SelfId),
        ["target_user_uid"] = request.SelfId,
        ["state"] = RequestState(request),
        ["comment"] = request.Comment,
        ["via"] = "asuka",
        ["is_filtered"] = false,
    };

    internal static JsonObject GroupNotification(PendingRequest request) => new()
    {
        ["type"] = "join_request",
        ["group_id"] = Uin(request.GroupId ?? "0"),
        ["notification_seq"] = NotificationSequence(request),
        ["is_filtered"] = false,
        ["initiator_id"] = Uin(request.RequesterId),
        ["state"] = RequestState(request),
        ["operator_id"] = null,
        ["comment"] = request.Comment,
    };

    internal static long NotificationSequence(PendingRequest request)
    {
        ulong hash = 5381;
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(request.Flag))
        {
            hash = unchecked((hash * 33) + value);
        }

        return checked((long)(hash % 9_007_199_254_740_991UL));
    }

    internal static JsonNode Uin(string value) => JsonExtensions.NumericId(value);

    internal static long Timestamp(DateTimeOffset value) => value.ToUnixTimeSeconds();

    internal static string SceneName(ChatScene scene) => scene switch
    {
        ChatScene.Friend => "friend",
        ChatScene.Group => "group",
        ChatScene.Temp => "temp",
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };

    private static string RequestState(PendingRequest request) => request.Resolution?.Status switch
    {
        null => "pending",
        RequestResolutionStatus.Accepted => "accepted",
        RequestResolutionStatus.Rejected => "rejected",
        _ => "pending",
    };
}
