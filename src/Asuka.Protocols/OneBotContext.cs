using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

internal sealed class OneBotContext(AsukaStore store, OneBotVersion version, string selfId)
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
            result["area"] = string.Empty;
            result["level"] = string.Empty;
        }

        return result;
    }

    internal async Task<JsonObject?> UserInfoAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is not null && version == OneBotVersion.V12)
        {
            var friendship = await store.GetFriendshipAsync(selfId, userId, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["user_id"] = user.Id,
                ["user_name"] = user.Name,
                ["user_displayname"] = user.Nickname,
                ["user_remark"] = friendship?.Remark ?? string.Empty,
            };
        }

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
        if (group is not null && version == OneBotVersion.V12)
        {
            return new JsonObject { ["group_id"] = group.Id, ["group_name"] = group.Name };
        }

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
        if (version == OneBotVersion.V12)
        {
            return new JsonObject
            {
                ["user_id"] = userId,
                ["user_name"] = user?.Name ?? userId,
                ["user_displayname"] = string.IsNullOrEmpty(member.Card) ? user?.Nickname ?? string.Empty : member.Card,
            };
        }

        return new JsonObject
        {
            ["group_id"] = groupId,
            ["user_id"] = userId,
            ["nickname"] = user?.DisplayName ?? userId,
            ["card"] = member.Card,
            ["sex"] = (user?.Sex ?? Sex.Unknown).ToString().ToLowerInvariant(),
            ["age"] = user?.Age ?? 0,
            ["area"] = string.Empty,
            ["level"] = string.Empty,
            ["unfriendly"] = false,
            ["title_expire_time"] = 0,
            ["card_changeable"] = true,
            ["join_time"] = member.JoinedAt.ToUnixTimeSeconds(),
            ["last_sent_time"] = member.LastSentAt?.ToUnixTimeSeconds() ?? 0,
            ["role"] = member.Role.ToString().ToLowerInvariant(),
            ["title"] = member.Title,
            ["shut_up_timestamp"] = member.MutedUntil?.ToUnixTimeSeconds() ?? 0,
        };
    }
}
