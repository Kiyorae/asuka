using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

internal sealed class MilkySegmentCodec(MediaService media, AsukaStore store)
{
    internal async Task<JsonArray> EncodeIncomingAsync(
        IEnumerable<MessageSegment> content,
        CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var segment in content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await EncodeIncomingAsync(segment, cancellationToken).ConfigureAwait(false);
            if (encoded is not null)
            {
                result.Add(encoded);
            }
        }

        return result;
    }

    internal async Task<IReadOnlyList<MessageSegment>> DecodeOutgoingAsync(
        JsonArray segments,
        Chat conversation,
        CancellationToken cancellationToken)
    {
        var result = new List<MessageSegment>(segments.Count);
        foreach (var node in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not JsonObject raw)
            {
                throw new ArgumentException("Every message segment must be an object with type and data");
            }

            result.Add(await DecodeOutgoingAsync(raw, conversation, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<JsonObject?> EncodeIncomingAsync(
        MessageSegment segment,
        CancellationToken cancellationToken)
    {
        switch (segment)
        {
            case TextSegment text:
                return Segment("text", new JsonObject { ["text"] = text.Text });
            case MentionSegment { UserId: null }:
                return Segment("mention_all", new JsonObject());
            case MentionSegment mention:
                {
                    var user = await store.GetUserAsync(mention.UserId!, cancellationToken).ConfigureAwait(false);
                    var name = (user?.DisplayName ?? mention.UserId!).Trim();
                    name = name.StartsWith('@') ? name[1..] : name;
                    return Segment("mention", new JsonObject
                    {
                        ["user_id"] = Uin(mention.UserId!),
                        ["name"] = string.IsNullOrEmpty(name) ? mention.UserId : name,
                    });
                }
            case FaceSegment face:
                return Segment("face", new JsonObject { ["face_id"] = face.Id, ["is_large"] = face.IsLarge });
            case ImageSegment image:
                {
                    var reference = await media.GetReferenceAsync(
                        image.Asset,
                        preferLocalPath: false,
                        cancellationToken).ConfigureAwait(false);
                    var resolvedAsset = reference.ResolvedAsset ?? image.Asset;
                    var header = await media.ReadHeaderAsync(resolvedAsset, cancellationToken).ConfigureAwait(false);
                    _ = ImageDimensions.TryRead(header, out var width, out var height);
                    return Segment("image", new JsonObject
                    {
                        ["resource_id"] = reference.Identifier,
                        ["temp_url"] = reference.Url,
                        ["width"] = width,
                        ["height"] = height,
                        ["summary"] = image.Summary ?? resolvedAsset.Name,
                        ["sub_type"] = image.SubType,
                    });
                }
            case RecordSegment record:
                {
                    var reference = await media.GetReferenceAsync(
                        record.Asset,
                        preferLocalPath: false,
                        cancellationToken).ConfigureAwait(false);
                    return Segment("record", new JsonObject
                    {
                        ["resource_id"] = reference.Identifier,
                        ["temp_url"] = reference.Url,
                        ["duration"] = (long)(record.Duration?.TotalSeconds ?? 0),
                    });
                }
            case VideoSegment video:
                {
                    var reference = await media.GetReferenceAsync(
                        video.Asset,
                        preferLocalPath: false,
                        cancellationToken).ConfigureAwait(false);
                    return Segment("video", new JsonObject
                    {
                        ["resource_id"] = reference.Identifier,
                        ["temp_url"] = reference.Url,
                        ["width"] = 0,
                        ["height"] = 0,
                        ["duration"] = 0,
                    });
                }
            case FileSegment file:
                {
                    var reference = await media.GetReferenceAsync(
                        file.Asset,
                        preferLocalPath: false,
                        cancellationToken).ConfigureAwait(false);
                    var resolvedAsset = reference.ResolvedAsset ?? file.Asset;
                    return Segment("file", new JsonObject
                    {
                        ["file_id"] = reference.Identifier,
                        ["file_name"] = resolvedAsset.Name,
                        ["file_size"] = resolvedAsset.ByteCount,
                    });
                }
            case ReplySegment reply:
                {
                    var message = await store.GetMessageAsync(reply.MessageId, cancellationToken).ConfigureAwait(false);
                    if (message is null || message.IsRecalled || message.Anonymous is not null)
                    {
                        return null;
                    }

                    return Segment("reply", new JsonObject
                    {
                        ["message_seq"] = message.Seq,
                        ["sender_id"] = Uin(message.SenderId),
                        ["time"] = message.Time.ToUnixTimeSeconds(),
                        ["segments"] = await EncodeIncomingAsync(message.Content, cancellationToken).ConfigureAwait(false),
                    });
                }
            case PokeSegment:
                return null;
            case ForwardSegment forward:
                {
                    var preview = new JsonArray();
                    foreach (var line in forward.Preview ?? forward.Nodes.Take(4)
                        .Select(static node => $"{node.SenderName}: {node.Content.TextPreview()}").ToArray())
                    {
                        preview.Add(line);
                    }

                    return Segment("forward", new JsonObject
                    {
                        ["forward_id"] = forward.Id,
                        ["title"] = forward.Title ?? "Forwarded messages",
                        ["preview"] = preview,
                        ["summary"] = forward.Summary ?? $"View {forward.Nodes.Count} forwarded messages",
                    });
                }
            case UnsupportedSegment unsupported:
                return EncodeStructuredSegment(unsupported);
            default:
                return null;
        }
    }

    private async Task<MessageSegment> DecodeOutgoingAsync(
        JsonObject raw,
        Chat conversation,
        CancellationToken cancellationToken)
    {
        var type = RequiredText(raw, "type");
        var data = raw["data"] as JsonObject
            ?? throw new ArgumentException($"{type} segment requires a data object");
        return type switch
        {
            "text" => new TextSegment(RequiredText(data, "text", allowEmpty: true)),
            "mention" => new MentionSegment(RequiredText(data, "user_id")),
            "mention_all" => new MentionSegment(null),
            "face" => new FaceSegment(RequiredText(data, "face_id"),
                IsLarge: data.GetFlexibleBoolean("is_large") ?? false),
            "image" => await IngestAsync(data, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false),
            "record" => await IngestAsync(data, ProtocolAssetKind.Record, cancellationToken).ConfigureAwait(false),
            "video" => await IngestAsync(data, ProtocolAssetKind.Video, cancellationToken).ConfigureAwait(false),
            "reply" => await ReplyAsync(conversation,
                data.GetFlexibleInt64("message_seq") ?? throw new ArgumentException("reply requires message_seq"),
                cancellationToken).ConfigureAwait(false),
            "forward" => await DecodeForwardAsync(data, conversation, cancellationToken).ConfigureAwait(false),
            "light_app" => DecodeLightApp(data),
            _ => throw new ArgumentException($"Unsupported Milky outgoing segment: {type}"),
        };
    }

    private async Task<MessageSegment> IngestAsync(
        JsonObject data,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken)
    {
        var uri = RequiredText(data, "uri");
        var subType = data.GetFlexibleString("sub_type") ?? "normal";
        if (kind == ProtocolAssetKind.Image && subType is not ("normal" or "sticker"))
        {
            throw new ArgumentException("image sub_type must be normal or sticker");
        }

        var asset = await media.ResolveReferenceAsync(uri, null, kind, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unable to resolve {kind} URI");
        Asset? thumbnail = null;
        if (kind == ProtocolAssetKind.Video && data.GetFlexibleString("thumb_uri") is { } thumbnailUri)
        {
            thumbnail = await media.ResolveReferenceAsync(thumbnailUri, null,
                ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("Unable to resolve video thumbnail URI");
        }

        return kind switch
        {
            ProtocolAssetKind.Image => new ImageSegment(asset, subType, data.GetFlexibleString("summary")),
            ProtocolAssetKind.Record => new RecordSegment(asset),
            ProtocolAssetKind.Video => new VideoSegment(asset, thumbnail),
            _ => new FileSegment(asset),
        };
    }

    private async Task<MessageSegment> ReplyAsync(
        Chat conversation,
        long sequence,
        CancellationToken cancellationToken)
    {
        var message = await store.GetMessageAsync(
            conversation.Scene,
            conversation.PeerId,
            sequence,
            conversation.SelfId,
            cancellationToken).ConfigureAwait(false);
        return message is null || message.IsRecalled || message.Anonymous is not null
            ? throw new ArgumentException($"Referenced message not found: {sequence}")
            : new ReplySegment(message.Id, message.SenderId);
    }

    private async Task<MessageSegment> DecodeForwardAsync(
        JsonObject data,
        Chat conversation,
        CancellationToken cancellationToken)
    {
        if (data["messages"] is not JsonArray { Count: > 0 } messages)
        {
            throw new ArgumentException("forward requires a nonempty messages array");
        }

        var preview = data["preview"] as JsonArray;
        if (data["preview"] is not null && (preview is null || preview.Count is < 1 or > 4
            || preview.Any(static entry => entry is not System.Text.Json.Nodes.JsonValue value
                || !value.TryGetValue<string>(out _))))
        {
            throw new ArgumentException("forward preview must contain between 1 and 4 strings");
        }

        var nodes = new List<ForwardNode>(messages.Count);
        foreach (var node in messages)
        {
            if (node is not JsonObject entry || entry["segments"] is not JsonArray { Count: > 0 } nodeSegments)
            {
                throw new ArgumentException("Every forwarded message requires nonempty segments");
            }

            var senderId = RequiredText(entry, "user_id");
            var senderName = RequiredText(entry, "sender_name", allowEmpty: true);
            var content = await DecodeOutgoingAsync(
                nodeSegments,
                conversation,
                cancellationToken).ConfigureAwait(false);
            nodes.Add(new ForwardNode(
                senderId,
                senderName,
                content,
                time: entry.GetFlexibleInt64("time") is { } time
                    ? DateTimeOffset.FromUnixTimeSeconds(time)
                    : DateTimeOffset.UtcNow));
        }

        return new ForwardSegment(IdGenerator.MessageId(), nodes,
            data.GetFlexibleString("title"), data.GetFlexibleString("summary"),
            preview?.Select(static entry => entry!.GetValue<string>()).ToArray(), data.GetFlexibleString("prompt"));
    }

    private static UnsupportedSegment DecodeLightApp(JsonObject data)
    {
        var json = RequiredText(data, "json_payload");
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("light_app json_payload must encode an object");
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new ArgumentException("light_app json_payload must contain valid JSON", error);
        }

        return new UnsupportedSegment("light_app", Asuka.Core.JsonValue.Parse(new JsonObject
        {
            ["json_payload"] = json,
            ["app_name"] = payload.GetFlexibleString("app") ?? string.Empty,
        }.ToJsonString()));
    }

    private static JsonObject EncodeStructuredSegment(UnsupportedSegment segment)
    {
        if (segment.Type is "light_app" or "xml" or "markdown" or "market_face"
            && JsonNode.Parse(segment.Payload.ToJsonString()) is JsonObject payload)
        {
            var data = payload["data"] as JsonObject ?? payload;
            if (segment.Type == "light_app" && data["app_name"] is null)
            {
                data["app_name"] = string.Empty;
            }

            return Segment(segment.Type, (JsonObject)data.DeepClone());
        }

        return Segment("text", new JsonObject { ["text"] = segment.TextPreview });
    }

    private static string RequiredText(JsonObject data, string name, bool allowEmpty = false)
    {
        var value = data.GetFlexibleString(name);
        return value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value))
            ? value
            : throw new ArgumentException($"Missing or invalid {name}");
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };

    internal static JsonNode Uin(string value) => JsonExtensions.NumericId(value);
}
