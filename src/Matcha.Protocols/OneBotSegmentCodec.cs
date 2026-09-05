using System.Text.Json.Nodes;
using Matcha.Core;

namespace Matcha.Protocols;

public sealed class OneBotSegmentCodec(OneBotVersion version, IProtocolAssetResolver assetResolver)
{
    public async Task<JsonArray> EncodeAsync(
        IEnumerable<MessageSegment> content,
        CancellationToken cancellationToken = default)
    {
        var result = new JsonArray();
        foreach (var segment in content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await EncodeAsync(segment, cancellationToken).ConfigureAwait(false);
            if (encoded is not null)
            {
                result.Add(encoded);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<MessageSegment>> DecodeAsync(
        JsonArray segments,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MessageSegment>(segments.Count);
        foreach (var node in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is JsonObject raw)
            {
                var decoded = await DecodeAsync(raw, cancellationToken).ConfigureAwait(false);
                if (decoded is not null)
                {
                    result.Add(decoded);
                }
            }
        }

        return result;
    }

    private async Task<JsonObject?> EncodeAsync(MessageSegment segment, CancellationToken cancellationToken)
    {
        switch (segment)
        {
            case TextSegment text:
                return Segment("text", new JsonObject { ["text"] = text.Text });
            case MentionSegment mention when version == OneBotVersion.V11:
                return Segment("at", new JsonObject { ["qq"] = mention.UserId ?? "all" });
            case MentionSegment mention:
                return mention.UserId is null
                    ? Segment("mention_all", new JsonObject())
                    : Segment("mention", new JsonObject { ["user_id"] = mention.UserId });
            case FaceSegment face when version == OneBotVersion.V11:
                return Segment("face", new JsonObject { ["id"] = face.Id });
            case FaceSegment:
                return null;
            case ImageSegment image:
                return await EncodeAssetAsync("image", image.Asset, cancellationToken).ConfigureAwait(false);
            case RecordSegment record:
                return await EncodeAssetAsync(
                    version == OneBotVersion.V11 ? "record" : "voice",
                    record.Asset,
                    cancellationToken).ConfigureAwait(false);
            case VideoSegment video:
                return await EncodeAssetAsync("video", video.Asset, cancellationToken).ConfigureAwait(false);
            case FileSegment file when version == OneBotVersion.V11:
                return Segment("text", new JsonObject { ["text"] = $"[File: {file.Asset.Name}]" });
            case FileSegment file:
                return await EncodeAssetAsync("file", file.Asset, cancellationToken).ConfigureAwait(false);
            case ReplySegment reply:
                return Segment(
                    "reply",
                    new JsonObject { [version == OneBotVersion.V11 ? "id" : "message_id"] = reply.MessageId });
            case PokeSegment poke when version == OneBotVersion.V11:
                return Segment("poke", new JsonObject { ["type"] = "1", ["id"] = poke.UserId ?? "0" });
            case PokeSegment:
                return null;
            case ForwardSegment forward when version == OneBotVersion.V11:
                return Segment("forward", new JsonObject { ["id"] = forward.Id });
            case ForwardSegment:
                return null;
            case UnsupportedSegment unsupported:
                {
                    var raw = JsonNode.Parse(unsupported.Payload.ToJsonString());
                    if (raw is JsonObject rawObject && rawObject["type"] is not null)
                    {
                        return rawObject;
                    }

                    return Segment(unsupported.Type, raw as JsonObject ?? new JsonObject());
                }
            default:
                return null;
        }
    }

    private async Task<JsonObject> EncodeAssetAsync(
        string type,
        Asset asset,
        CancellationToken cancellationToken)
    {
        var reference = await assetResolver.GetReferenceAsync(
            asset,
            preferLocalPath: version == OneBotVersion.V11,
            cancellationToken).ConfigureAwait(false);
        return version == OneBotVersion.V11
            ? Segment(type, new JsonObject { ["file"] = reference.Identifier, ["url"] = reference.Url })
            : Segment(type, new JsonObject { ["file_id"] = reference.Identifier });
    }

    private async Task<MessageSegment?> DecodeAsync(JsonObject raw, CancellationToken cancellationToken)
    {
        var type = raw.GetFlexibleString("type");
        var data = raw["data"] as JsonObject ?? new JsonObject();
        return type switch
        {
            "text" => new TextSegment(data.GetFlexibleString("text") ?? string.Empty),
            "at" => new MentionSegment(data.GetFlexibleString("qq") is "all" ? null : data.GetFlexibleString("qq")),
            "mention" => new MentionSegment(data.GetFlexibleString("user_id")),
            "mention_all" => new MentionSegment(null),
            "face" when data.GetFlexibleString("id") is { } id => new FaceSegment(id),
            "image" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false),
            "record" or "voice" => await ResolveAssetSegmentAsync(
                data,
                ProtocolAssetKind.Record,
                cancellationToken).ConfigureAwait(false),
            "video" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.Video, cancellationToken).ConfigureAwait(false),
            "file" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false),
            "reply" when (data.GetFlexibleString("id") ?? data.GetFlexibleString("message_id")) is { } messageId =>
                new ReplySegment(messageId),
            "poke" => new PokeSegment(data.GetFlexibleString("id")),
            "forward" => new ForwardSegment(data.GetFlexibleString("id") ?? string.Empty, []),
            null => null,
            _ => new UnsupportedSegment(type, Matcha.Core.JsonValue.Parse(raw.ToJsonString())),
        };
    }

    private async Task<MessageSegment?> ResolveAssetSegmentAsync(
        JsonObject data,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken)
    {
        Asset? asset;
        if (data.GetFlexibleString("file_id") is { } fileId)
        {
            asset = await assetResolver.ResolveIdAsync(fileId, kind, cancellationToken).ConfigureAwait(false);
        }
        else if (data.GetFlexibleString("file") is { } reference)
        {
            asset = await assetResolver.ResolveReferenceAsync(
                reference,
                data.GetFlexibleString("url"),
                kind,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            asset = null;
        }

        return (kind, asset) switch
        {
            (_, null) => null,
            (ProtocolAssetKind.Image, { } value) => new ImageSegment(value),
            (ProtocolAssetKind.Record, { } value) => new RecordSegment(
                value,
                data.GetFlexibleInt64("duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
            (ProtocolAssetKind.Video, { } value) => new VideoSegment(value),
            (ProtocolAssetKind.File, { } value) => new FileSegment(value),
            _ => null,
        };
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };
}

public enum OneBotVersion
{
    V11,
    V12,
}
