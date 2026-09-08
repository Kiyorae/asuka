using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed class OneBotSegmentCodec(OneBotVersion version, IProtocolAssetResolver assetResolver, AsukaStore? store = null)
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
            else
            {
                throw new OneBotSegmentException(10006, "Each message segment must be an object");
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
            // Anonymous is a sending instruction, never a received message segment.
            case AnonymousSegment:
                return null;
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
            case AudioSegment audio when version == OneBotVersion.V12:
                return await EncodeAssetAsync("audio", audio.Asset, cancellationToken).ConfigureAwait(false);
            case AudioSegment audio:
                return Segment("text", new JsonObject { ["text"] = audio.TextPreview });
            case LocationSegment location:
                return Segment("location", new JsonObject
                {
                    [version == OneBotVersion.V11 ? "lat" : "latitude"] = version == OneBotVersion.V11
                        ? System.Text.Json.Nodes.JsonValue.Create(location.Latitude.ToString(CultureInfo.InvariantCulture))
                        : System.Text.Json.Nodes.JsonValue.Create(location.Latitude),
                    [version == OneBotVersion.V11 ? "lon" : "longitude"] = version == OneBotVersion.V11
                        ? System.Text.Json.Nodes.JsonValue.Create(location.Longitude.ToString(CultureInfo.InvariantCulture))
                        : System.Text.Json.Nodes.JsonValue.Create(location.Longitude),
                    ["title"] = location.Title,
                    ["content"] = location.Content,
                });
            case VideoSegment video:
                return await EncodeAssetAsync("video", video.Asset, cancellationToken).ConfigureAwait(false);
            case FileSegment file when version == OneBotVersion.V11:
                return Segment("text", new JsonObject { ["text"] = $"[File: {file.Asset.Name}]" });
            case FileSegment file:
                return await EncodeAssetAsync("file", file.Asset, cancellationToken).ConfigureAwait(false);
            case ReplySegment reply:
                var replyData = new JsonObject { [version == OneBotVersion.V11 ? "id" : "message_id"] = reply.MessageId };
                if (version == OneBotVersion.V12)
                {
                    var sender = reply.UserId;
                    if (store is not null)
                    {
                        var source = await store.GetMessageAsync(reply.MessageId, cancellationToken).ConfigureAwait(false);
                        // An explicit stale sender field must not defeat anonymity
                        // when a V11 message is quoted after changing protocols.
                        sender = source?.Anonymous is { } anonymous
                            ? anonymous.Id.ToString(CultureInfo.InvariantCulture)
                            : sender ?? source?.SenderId;
                    }

                    replyData["user_id"] = sender ?? string.Empty;
                }

                return Segment("reply", replyData);
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
                        if (version == OneBotVersion.V12)
                        {
                            return null;
                        }

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
        if (type is null || raw["data"] is not JsonObject data)
        {
            throw new OneBotSegmentException(10006, "Message segments require type and data");
        }

        ValidateSegment(type, data);
        return type switch
        {
            "text" => new TextSegment(data.GetFlexibleString("text") ?? string.Empty),
            "anonymous" when version == OneBotVersion.V11 => new AnonymousSegment(AnonymousIgnore(data)),
            "at" when version == OneBotVersion.V11 => new MentionSegment(data.GetFlexibleString("qq") is "all" ? null : data.GetFlexibleString("qq")),
            "mention" when version == OneBotVersion.V12 => new MentionSegment(data.GetFlexibleString("user_id")),
            "mention_all" when version == OneBotVersion.V12 => new MentionSegment(null),
            "face" when version == OneBotVersion.V11 && data.GetFlexibleString("id") is { } id => new FaceSegment(id),
            "image" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.Image, cancellationToken).ConfigureAwait(false),
            "record" or "voice" => await ResolveAssetSegmentAsync(
                data,
                ProtocolAssetKind.Record,
                cancellationToken).ConfigureAwait(false),
            "audio" when version == OneBotVersion.V12 => new AudioSegment(
                await assetResolver.ResolveIdAsync(data.GetFlexibleString("file_id")!, ProtocolAssetKind.Record, cancellationToken).ConfigureAwait(false)
                    ?? throw new OneBotSegmentException(10006, "Audio file_id could not be resolved")),
            "location" => new LocationSegment(
                Coordinate(data, version == OneBotVersion.V11 ? "lat" : "latitude"),
                Coordinate(data, version == OneBotVersion.V11 ? "lon" : "longitude"),
                data.GetFlexibleString("title") ?? string.Empty,
                data.GetFlexibleString("content") ?? string.Empty),
            "video" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.Video, cancellationToken).ConfigureAwait(false),
            "file" => await ResolveAssetSegmentAsync(data, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false),
            "reply" when (data.GetFlexibleString("id") ?? data.GetFlexibleString("message_id")) is { } messageId =>
                new ReplySegment(messageId, version == OneBotVersion.V12 ? data.GetFlexibleString("user_id") : null),
            "poke" => new PokeSegment(data.GetFlexibleString("id")),
            "forward" => new ForwardSegment(data.GetFlexibleString("id") ?? string.Empty, []),
            _ => new UnsupportedSegment(type, Asuka.Core.JsonValue.Parse(raw.ToJsonString())),
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
            (_, null) => throw new OneBotSegmentException(10006, "Message asset could not be resolved"),
            (ProtocolAssetKind.Image, { } value) => new ImageSegment(value),
            (ProtocolAssetKind.Record, { } value) => new RecordSegment(
                value,
                data.GetFlexibleInt64("duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
            (ProtocolAssetKind.Video, { } value) => new VideoSegment(value),
            (ProtocolAssetKind.File, { } value) => new FileSegment(value),
            _ => null,
        };
    }

    private void ValidateSegment(string type, JsonObject data)
    {
        if (version == OneBotVersion.V12)
        {
            switch (type)
            {
                case "text": RequireString(data, "text", allowEmpty: true); break;
                case "mention": RequireString(data, "user_id"); break;
                case "mention_all": break;
                case "image" or "voice" or "audio" or "video" or "file": RequireString(data, "file_id"); break;
                case "reply": RequireString(data, "message_id"); break;
                case "location":
                    RequireString(data, "title", allowEmpty: true);
                    RequireString(data, "content", allowEmpty: true);
                    break;
                default: throw new OneBotSegmentException(10005, $"Unsupported message segment: {type}");
            }
        }
    }

    private static void RequireString(JsonObject data, string key, bool allowEmpty = false)
    {
        if (data[key] is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var text)
            || (!allowEmpty && string.IsNullOrEmpty(text)))
        {
            throw new OneBotSegmentException(10006, $"Missing or invalid segment field: {key}");
        }
    }

    private static bool AnonymousIgnore(JsonObject data)
    {
        // The standard specifies 0/1, including CQ string values, but no default.
        // Asuka defaults to failing closed rather than revealing the sender.
        if (!data.ContainsKey("ignore")) return false;
        return data.GetFlexibleString("ignore") switch
        {
            "0" => false,
            "1" => true,
            _ => throw new OneBotSegmentException(10006, "Anonymous ignore must be 0 or 1"),
        };
    }

    private static double Coordinate(JsonObject data, string key)
    {
        if (!double.TryParse(data.GetFlexibleString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || Math.Abs(value) > (key is "lat" or "latitude" ? 90 : 180))
        {
            throw new OneBotSegmentException(10006, $"Invalid location coordinate: {key}");
        }

        return value;
    }

    internal static JsonArray ParseCqString(string text)
    {
        var result = new JsonArray();
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf("[CQ:", position, StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf(']', start);
            if (start < 0 || end < 0)
            {
                result.Add(Segment("text", new JsonObject { ["text"] = Unescape(text[position..], parameter: false) }));
                break;
            }

            if (start > position)
            {
                result.Add(Segment("text", new JsonObject { ["text"] = Unescape(text[position..start], parameter: false) }));
            }

            var parts = text[(start + 4)..end].Split(',');
            var data = new JsonObject();
            foreach (var parameter in parts.Skip(1))
            {
                var separator = parameter.IndexOf('=');
                if (separator <= 0)
                {
                    throw new OneBotSegmentException(10006, "Invalid CQ code parameter");
                }

                data[parameter[..separator]] = Unescape(parameter[(separator + 1)..], parameter: true);
            }

            result.Add(Segment(parts[0], data));
            position = end + 1;
        }

        return result;
    }

    internal static string ToCqString(JsonArray segments)
    {
        var result = new StringBuilder();
        foreach (var node in segments.OfType<JsonObject>())
        {
            var type = node.GetFlexibleString("type");
            var data = node["data"] as JsonObject ?? new JsonObject();
            if (type == "text")
            {
                result.Append(Escape(data.GetFlexibleString("text") ?? string.Empty, parameter: false));
                continue;
            }

            result.Append("[CQ:").Append(type);
            foreach (var (key, value) in data)
            {
                if (value is not null)
                {
                    result.Append(',').Append(key).Append('=').Append(Escape(data.GetFlexibleString(key) ?? value.ToJsonString(), parameter: true));
                }
            }

            result.Append(']');
        }

        return result.ToString();
    }

    private static string Escape(string value, bool parameter)
    {
        var escaped = value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("[", "&#91;", StringComparison.Ordinal).Replace("]", "&#93;", StringComparison.Ordinal);
        return parameter ? escaped.Replace(",", "&#44;", StringComparison.Ordinal) : escaped;
    }

    private static string Unescape(string value, bool parameter)
    {
        var escaped = parameter ? value.Replace("&#44;", ",", StringComparison.Ordinal) : value;
        return escaped.Replace("&#91;", "[", StringComparison.Ordinal).Replace("&#93;", "]", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };
}

internal sealed class OneBotSegmentException(int retCode, string message) : Exception(message)
{
    internal int RetCode { get; } = retCode;
}

public enum OneBotVersion
{
    V11,
    V12,
}
