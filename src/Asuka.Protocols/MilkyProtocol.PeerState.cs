using System.Globalization;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class MilkyProtocol
{
    private async Task<ProtocolReply> PeerPinsAsync(CancellationToken cancellationToken)
    {
        await RequireSelfProfileAsync(cancellationToken).ConfigureAwait(false);
        var friends = new JsonArray();
        var groups = new JsonArray();
        foreach (var state in await _store.GetPeerStatesAsync(SelfId, cancellationToken).ConfigureAwait(false))
        {
            if (!state.IsPinned) continue;
            if (state.Chat.Scene == ChatScene.Friend)
            {
                var user = await _store.GetUserAsync(state.Chat.PeerId, cancellationToken).ConfigureAwait(false);
                var friendship = await _store.GetFriendshipAsync(SelfId, state.Chat.PeerId, cancellationToken).ConfigureAwait(false);
                if (user is not null && friendship is not null)
                    friends.Add(MilkyEntityEncoder.Friend(user, friendship.Remark));
            }
            else if (state.Chat.Scene == ChatScene.Group)
            {
                var group = await _store.GetGroupAsync(state.Chat.PeerId, cancellationToken).ConfigureAwait(false);
                if (group is not null && await _store.GetMemberAsync(group.Id, SelfId, cancellationToken).ConfigureAwait(false) is not null)
                    groups.Add(await _entities.GroupAsync(group, cancellationToken).ConfigureAwait(false));
            }
        }
        // The standard returns only friend/group entities, even though temp pins
        // can be set and are represented by peer_pin_change events.
        return ProtocolReply.Success(new JsonObject { ["friends"] = friends, ["groups"] = groups });
    }

    private async Task<ProtocolReply> SetPeerPinAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryPeerChat(request, out var chat)) return Invalid("Expected message_scene and a positive peer_id");
        bool? isPinned = request.Parameters["is_pinned"] is System.Text.Json.Nodes.JsonValue pinValue
            && pinValue.TryGetValue<bool>(out var pinned) ? pinned : null;
        if (request.Parameters.ContainsKey("is_pinned") && isPinned is null)
            return Invalid("is_pinned must be a boolean");
        await _platform.SetPeerPinAsync(chat!, isPinned ?? true, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> MarkMessageAsReadAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryPeerChat(request, out var chat) || request.GetLong("message_seq") is not > 0)
            return Invalid("Expected message_scene, a positive peer_id and a positive message_seq");
        await _platform.MarkMessageAsReadAsync(chat!, request.GetLong("message_seq")!.Value, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SendProfileLikeAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var userId = request.GetId("user_id");
        if (!IsPositivePeerId(userId)) return Invalid("Expected a positive user_id");
        int? count = request.Parameters["count"] is System.Text.Json.Nodes.JsonValue countValue
            && countValue.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? countValue.TryGetValue<int>(out var integer) ? integer : request.GetInteger("count")
                : null;
        if (request.Parameters.ContainsKey("count") && count is null or <= 0)
            return Invalid("count must be a positive int32");
        await _platform.SendProfileLikeAsync(userId!, SelfId, count ?? 1, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetNicknameAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryProfileText(request, "new_nickname", out var nickname) || string.IsNullOrWhiteSpace(nickname))
            return Invalid("new_nickname must be a nonempty string");
        await _platform.SetNicknameAsync(SelfId, nickname!, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetBioAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryProfileText(request, "new_bio", out var bio)) return Invalid("new_bio must be a string");
        await _platform.SetBioAsync(SelfId, bio!, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> SetAvatarAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (!TryProfileText(request, "uri", out var uri) || string.IsNullOrWhiteSpace(uri))
            return Invalid("uri must be a nonempty image URI");
        await RequireSelfProfileAsync(cancellationToken).ConfigureAwait(false);
        var image = await _media.ResolveReferenceAsync(uri, null, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false);
        if (image is null) return Invalid("The avatar image could not be loaded");
        var header = await _media.ReadHeaderAsync(image, cancellationToken).ConfigureAwait(false);
        if (!ImageDimensions.TryRead(header, out _, out _)) return Invalid("The avatar must be a supported image");
        await _platform.SetAvatarAsync(SelfId, new Uri(_media.GetPath(image.Id)).AbsoluteUri, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> CustomFaceUrlsAsync(CancellationToken cancellationToken)
    {
        var faces = await _platform.GetCustomFacesAsync(SelfId, cancellationToken).ConfigureAwait(false);
        var urls = new JsonArray();
        foreach (var face in faces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_media.Assets.Exists(face.Asset.Id)) continue;
            urls.Add(_media.GetUrl(face.Asset.Id).AbsoluteUri);
        }
        return ProtocolReply.Success(new JsonObject { ["urls"] = urls });
    }

    private async Task RequireSelfProfileAsync(CancellationToken cancellationToken)
    {
        _ = await _store.GetUserAsync(SelfId, cancellationToken).ConfigureAwait(false)
            ?? throw Missing(PlatformError.UserNotFound, $"User not found: {SelfId}", SelfId);
    }

    private bool TryPeerChat(ProtocolCall request, out Chat? chat)
    {
        chat = null;
        if (ParseScene(request.GetText("message_scene")) is not { } scene || !IsPositivePeerId(request.GetId("peer_id")))
            return false;
        chat = new Chat(scene, request.GetId("peer_id")!, SelfId);
        return true;
    }

    private static bool IsPositivePeerId(string? id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

    private static bool TryProfileText(ProtocolCall request, string key, out string? text)
    {
        text = null;
        return request.Parameters[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out text) && text is not null;
    }
}
