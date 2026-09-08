using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private async Task<ProtocolReply> GetGroupHonorInfoAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.GetLong("group_id") is not > 0) return Invalid("group_id must be a positive QQ group number");
        if (request.Parameters["type"] is not System.Text.Json.Nodes.JsonValue value
            || !value.TryGetValue<string>(out var type)
            || type is not ("talkative" or "performer" or "legend" or "strong_newbie" or "emotion" or "all"))
            return Invalid("type must be talkative, performer, legend, strong_newbie, emotion or all");
        var info = await _platform.GetGroupHonorInfoAsync(request.GetId("group_id")!, SelfId, cancellationToken).ConfigureAwait(false);
        var result = new JsonObject { ["group_id"] = JsonExtensions.NumericId(info.GroupId) };
        if (type is "talkative" or "all")
        {
            result["current_talkative"] = info.CurrentTalkative is { } current ? new JsonObject
            {
                ["user_id"] = JsonExtensions.NumericId(current.UserId),
                ["nickname"] = current.Nickname,
                ["avatar"] = HonorAvatarUrl(current.Avatar),
                ["day_count"] = current.DayCount,
            } : null;
            result["talkative_list"] = EncodeHonorList(info.TalkativeList);
        }
        if (type is "performer" or "all") result["performer_list"] = EncodeHonorList(info.PerformerList);
        if (type is "legend" or "all") result["legend_list"] = EncodeHonorList(info.LegendList);
        if (type is "strong_newbie" or "all") result["strong_newbie_list"] = EncodeHonorList(info.StrongNewbieList);
        if (type is "emotion" or "all") result["emotion_list"] = EncodeHonorList(info.EmotionList);
        return ProtocolReply.Success(result);
    }

    private static JsonArray EncodeHonorList(IReadOnlyList<GroupHonorEntry> entries) => new(entries.Select(entry =>
        (JsonNode)new JsonObject
        {
            ["user_id"] = JsonExtensions.NumericId(entry.UserId),
            ["nickname"] = entry.Nickname,
            ["avatar"] = HonorAvatarUrl(entry.Avatar),
            ["description"] = entry.Description,
        }).ToArray());

    private static string HonorAvatarUrl(string avatar) => Uri.TryCreate(avatar, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? avatar : string.Empty;
}
