using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class MilkyProtocol
{
    private async Task<ProtocolReply> SetGroupAvatarAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId)
            || !TryProfileText(request, "image_uri", out var imageUri) || string.IsNullOrWhiteSpace(imageUri))
            return Invalid("Expected a positive group_id and image_uri");
        await RequireGroupContentAccessAsync(groupId!, true, cancellationToken).ConfigureAwait(false);
        var image = await ResolveGroupContentImageAsync(imageUri!, cancellationToken).ConfigureAwait(false);
        await _platform.SetGroupAvatarAsync(groupId!, SelfId, new Uri(_media.GetPath(image.Id)).AbsoluteUri, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GetGroupAnnouncementsAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId)) return Invalid("Expected a positive group_id");
        var announcements = await _platform.GetGroupAnnouncementsAsync(groupId!, SelfId, cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var announcement in announcements)
        {
            var encoded = new JsonObject
            {
                ["group_id"] = Uin(announcement.GroupId),
                ["announcement_id"] = announcement.Id,
                ["user_id"] = Uin(announcement.UserId),
                ["time"] = announcement.Time.ToUnixTimeSeconds(),
                ["content"] = announcement.Content,
            };
            if (announcement.Image is { } image)
            {
                var reference = await _media.GetReferenceAsync(image, false, cancellationToken).ConfigureAwait(false);
                encoded["image_url"] = reference.Url;
            }
            result.Add(encoded);
        }
        return ProtocolReply.Success(new JsonObject { ["announcements"] = result });
    }

    private async Task<ProtocolReply> SendGroupAnnouncementAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId) || !TryProfileText(request, "content", out var content))
            return Invalid("Expected a positive group_id and string content");
        string? imageUri = null;
        if (request.Parameters["image_uri"] is not null
            && (!TryProfileText(request, "image_uri", out imageUri) || string.IsNullOrWhiteSpace(imageUri)))
            return Invalid("image_uri must be an image URI or null");
        if (string.IsNullOrWhiteSpace(content) && imageUri is null) return Invalid("An announcement requires text or an image");
        await RequireGroupContentAccessAsync(groupId!, true, cancellationToken).ConfigureAwait(false);
        var image = imageUri is null ? null : await ResolveGroupContentImageAsync(imageUri, cancellationToken).ConfigureAwait(false);
        await _platform.SendGroupAnnouncementAsync(groupId!, SelfId, content!, image, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> DeleteGroupAnnouncementAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId)
            || !TryProfileText(request, "announcement_id", out var id) || string.IsNullOrWhiteSpace(id))
            return Invalid("Expected a positive group_id and nonempty announcement_id");
        await _platform.DeleteGroupAnnouncementAsync(groupId!, id!, SelfId, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GetGroupEssenceMessagesAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId)
            || !TryGroupContentInteger(request, "page_index", out var pageIndex) || pageIndex < 0
            || !TryGroupContentInteger(request, "page_size", out var pageSize) || pageSize <= 0)
            return Invalid("Expected group_id, a nonnegative int32 page_index and a positive int32 page_size");
        var page = await _platform.GetGroupEssenceMessagesAsync(groupId!, SelfId, pageIndex, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var essence in page.Messages)
        {
            if (essence.Message.Anonymous is not null) continue;
            result.Add(new JsonObject
            {
                ["group_id"] = Uin(essence.Message.PeerId),
                ["message_seq"] = essence.Message.Seq,
                ["message_time"] = essence.Message.Time.ToUnixTimeSeconds(),
                ["sender_id"] = Uin(essence.Message.SenderId),
                ["sender_name"] = essence.SenderName,
                ["operator_id"] = Uin(essence.OperatorId),
                ["operator_name"] = essence.OperatorName,
                ["operation_time"] = essence.OperationTime.ToUnixTimeSeconds(),
                ["segments"] = await _segments.EncodeIncomingAsync(essence.Message.Content, cancellationToken).ConfigureAwait(false),
            });
        }
        return ProtocolReply.Success(new JsonObject { ["messages"] = result, ["is_end"] = page.IsEnd });
    }

    private async Task<ProtocolReply> SetGroupEssenceMessageAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryGroupContentId(request, out var groupId) || request.GetLong("message_seq") is not > 0)
            return Invalid("Expected positive group_id and message_seq");
        var isSet = true;
        if (request.Parameters.ContainsKey("is_set")
            && (request.Parameters["is_set"] is not System.Text.Json.Nodes.JsonValue boolean || !boolean.TryGetValue(out isSet)))
            return Invalid("is_set must be a boolean");
        await _platform.SetGroupEssenceMessageAsync(groupId!, request.GetLong("message_seq")!.Value, SelfId, SelfId, isSet, cancellationToken)
            .ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<Asset> ResolveGroupContentImageAsync(string uri, CancellationToken cancellationToken)
    {
        var image = await _media.ResolveReferenceAsync(uri, null, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("The group image could not be loaded");
        if (!ImageDimensions.TryRead(await _media.ReadHeaderAsync(image, cancellationToken).ConfigureAwait(false), out _, out _))
            throw new ArgumentException("The group image must be a supported image");
        return image;
    }

    private async Task RequireGroupContentAccessAsync(string groupId, bool administrator, CancellationToken cancellationToken)
    {
        _ = await _store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.GroupNotFound, $"Group not found: {groupId}", groupId);
        var member = await _store.GetMemberAsync(groupId, SelfId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.NotAMember, "The current account is not a group member", SelfId);
        if (administrator && member.Role <= GroupRole.Member)
            throw Missing(PlatformError.NotPermitted, "Administrator privileges are required to manage group content", SelfId);
    }

    private static bool TryGroupContentId(ProtocolCall request, out string? groupId)
    {
        groupId = request.GetId("group_id");
        return IsPositivePeerId(groupId);
    }

    private static bool TryGroupContentInteger(ProtocolCall request, string key, out int integer)
    {
        integer = 0;
        return request.Parameters[key] is System.Text.Json.Nodes.JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && (value.TryGetValue(out integer) || int.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out integer));
    }
}
