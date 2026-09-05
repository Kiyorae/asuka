using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

internal sealed class OneBotContext(AsukaStore store)
{
    internal async Task<JsonObject> SenderInfoAsync(
        string userId,
        string? groupId,
        CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["user_id"] = JsonExtensions.NumericId(userId),
            ["nickname"] = user?.DisplayName ?? userId,
            ["sex"] = (user?.Sex ?? Sex.Unknown).ToString().ToLowerInvariant(),
            ["age"] = user?.Age ?? 0,
        };

        if (groupId is not null
            && await store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false) is { } member)
        {
            result["card"] = member.Card;
            result["role"] = member.Role.ToString().ToLowerInvariant();
            result["title"] = member.Title;
        }

        return result;
    }

    internal async Task<JsonObject?> UserInfoAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return user is null
            ? null
            : new JsonObject
            {
                ["user_id"] = user.Id,
                ["nickname"] = user.DisplayName,
                ["sex"] = user.Sex.ToString().ToLowerInvariant(),
                ["age"] = user.Age ?? 0,
            };
    }

    internal async Task<JsonObject?> GroupInfoAsync(string groupId, CancellationToken cancellationToken)
    {
        var group = await store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        return group is null
            ? null
            : new JsonObject
            {
                ["group_id"] = group.Id,
                ["group_name"] = group.Name,
                ["member_count"] = await store.GetMemberCountAsync(groupId, cancellationToken).ConfigureAwait(false),
                ["max_member_count"] = group.MaxMemberCount,
            };
    }

    internal async Task<JsonObject?> MemberInfoAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken)
    {
        var member = await store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return null;
        }

        var user = await store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["group_id"] = groupId,
            ["user_id"] = userId,
            ["nickname"] = user?.DisplayName ?? userId,
            ["card"] = member.Card,
            ["sex"] = (user?.Sex ?? Sex.Unknown).ToString().ToLowerInvariant(),
            ["age"] = user?.Age ?? 0,
            ["join_time"] = member.JoinedAt.ToUnixTimeSeconds(),
            ["last_sent_time"] = member.LastSentAt?.ToUnixTimeSeconds() ?? 0,
            ["role"] = member.Role.ToString().ToLowerInvariant(),
            ["title"] = member.Title,
            ["shut_up_timestamp"] = member.MutedUntil?.ToUnixTimeSeconds() ?? 0,
        };
    }
}
