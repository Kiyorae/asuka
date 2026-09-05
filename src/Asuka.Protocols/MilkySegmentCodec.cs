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
            if (node is JsonObject raw
                && await DecodeOutgoingAsync(raw, conversation, cancellationToken).ConfigureAwait(false) is { } segment)
            {
                result.Add(segment);
            }
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
                return Segment("face", new JsonObject { ["face_id"] = face.Id, ["is_large"] = false });
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
                        ["summary"] = resolvedAsset.Name,
                        ["sub_type"] = "normal",
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
                    if (message is null)
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
                    foreach (var node in forward.Nodes.Take(4))
                    {
                        preview.Add($"{node.SenderName}: {node.Content.TextPreview()}");
                    }

                    return Segment("forward", new JsonObject
                    {
                        ["forward_id"] = forward.Id,
                        ["title"] = "Forwarded messages",
                        ["preview"] = preview,
                        ["summary"] = $"View {forward.Nodes.Count} forwarded messages",
                    });
                }
            case UnsupportedSegment unsupported:
                return Segment("text", new JsonObject { ["text"] = $"[{unsupported.Type}]" });
            default:
                return null;
        }
    }

    private async Task<MessageSegment?> DecodeOutgoingAsync(
        JsonObject raw,
        Chat conversation,
        CancellationToken cancellationToken)
    {
        var type = raw.GetFlexibleString("type");
        var data = raw["data"] as JsonObject ?? new JsonObject();
        return type switch
        {
            "text" => new TextSegment(data.GetFlexibleString("text") ?? string.Empty),
            "mention" => new MentionSegment(data.GetFlexibleString("user_id")),
            "mention_all" => new MentionSegment(null),
            "face" when data.GetFlexibleString("face_id") is { } faceId => new FaceSegment(faceId),
            "image" => await IngestAsync(data, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false),
            "record" => await IngestAsync(data, ProtocolAssetKind.Record, cancellationToken).ConfigureAwait(false),
            "video" => await IngestAsync(data, ProtocolAssetKind.Video, cancellationToken).ConfigureAwait(false),
            "reply" when data.GetFlexibleInt64("message_seq") is { } seq =>
                await ReplyAsync(conversation, seq, cancellationToken).ConfigureAwait(false),
            "forward" => await DecodeForwardAsync(data, conversation, cancellationToken).ConfigureAwait(false),
            null => null,
            _ => new UnsupportedSegment(
                type,
                Asuka.Core.JsonValue.Parse((type == "light_app" ? data : raw).ToJsonString())),
        };
    }

    private async Task<MessageSegment?> IngestAsync(
        JsonObject data,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken)
    {
        var uri = data.GetFlexibleString("uri");
        if (uri is null)
        {
            return null;
        }

        var asset = await media.ResolveReferenceAsync(uri, null, kind, cancellationToken).ConfigureAwait(false);
        return (kind, asset) switch
        {
            (_, null) => null,
            (ProtocolAssetKind.Image, { } value) => new ImageSegment(value),
            (ProtocolAssetKind.Record, { } value) => new RecordSegment(value),
            (ProtocolAssetKind.Video, { } value) => new VideoSegment(value),
            _ => new FileSegment(asset!),
        };
    }

    private async Task<MessageSegment?> ReplyAsync(
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
        return message is null ? null : new ReplySegment(message.Id);
    }

    private async Task<MessageSegment?> DecodeForwardAsync(
        JsonObject data,
        Chat conversation,
        CancellationToken cancellationToken)
    {
        if (data["messages"] is not JsonArray messages)
        {
            return null;
        }

        var nodes = new List<ForwardNode>(messages.Count);
        foreach (var entry in messages.OfType<JsonObject>())
        {
            var senderId = entry.GetFlexibleString("user_id");
            if (senderId is null)
            {
                continue;
            }

            var content = await DecodeOutgoingAsync(
                entry["segments"] as JsonArray ?? new JsonArray(),
                conversation,
                cancellationToken).ConfigureAwait(false);
            nodes.Add(new ForwardNode(
                senderId,
                entry.GetFlexibleString("sender_name") ?? senderId,
                content,
                time: entry.GetFlexibleInt64("time") is { } time
                    ? DateTimeOffset.FromUnixTimeSeconds(time)
                    : DateTimeOffset.UtcNow));
        }

        return new ForwardSegment(IdGenerator.MessageId(), nodes);
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };

    internal static JsonNode Uin(string value) => JsonExtensions.NumericId(value);
}
