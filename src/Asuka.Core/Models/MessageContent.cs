using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asuka.Core;

public enum AssetSourceKind
{
    Local,
    Remote,
    Inline,
}

[JsonConverter(typeof(AssetSourceJsonConverter))]
public sealed record AssetSource(AssetSourceKind Kind, string? Value = null)
{
    public static AssetSource Local(string path) => new(AssetSourceKind.Local, path);
    public static AssetSource Remote(string url) => new(AssetSourceKind.Remote, url);
    public static AssetSource Inline { get; } = new(AssetSourceKind.Inline);
}

public sealed record Asset(
    string Id,
    string Name,
    string? MimeType = null,
    long ByteCount = 0,
    AssetSource? Source = null)
{
    public AssetSource Source { get; init; } = Source ?? AssetSource.Inline;
}

[JsonConverter(typeof(MessageSegmentJsonConverter))]
public abstract record MessageSegment
{
    public abstract string TextPreview { get; }

    public static TextSegment FromText(string text) => new(text);
    public static MentionSegment Mention(string? userId = null) => new(userId);
    public static FaceSegment Face(string id, string? name = null) => new(id, name);
    public static ImageSegment Image(Asset asset) => new(asset);
    public static RecordSegment Voice(Asset asset, TimeSpan? duration = null) => new(asset, duration);
    public static VideoSegment Video(Asset asset) => new(asset);
    public static FileSegment File(Asset asset) => new(asset);
    public static ReplySegment Reply(string messageId) => new(messageId);
    public static PokeSegment Poke(string? userId = null) => new(userId);
    public static ForwardSegment Forward(string id, IEnumerable<ForwardNode> nodes) => new(id, nodes.ToArray());
    public static UnsupportedSegment Unsupported(string type, JsonValue payload) => new(type, payload);
}

public sealed record TextSegment(string Text) : MessageSegment
{
    public override string TextPreview => Text;
}

/// <summary>Send-only anonymous intent. Identity exposure requires an explicit fallback opt-in.</summary>
public sealed record AnonymousSegment(bool Ignore = false) : MessageSegment
{
    public override string TextPreview => string.Empty;
}

public sealed record MentionSegment(string? UserId) : MessageSegment
{
    public override string TextPreview => UserId is null ? "@everyone" : $"@{UserId}";
}

public sealed record FaceSegment(string Id, string? Name = null, bool IsLarge = false) : MessageSegment
{
    public override string TextPreview => Name is null ? "[Emoji]" : $"[{Name}]";
}

public sealed record ImageSegment(Asset Asset, string SubType = "normal", string? Summary = null) : MessageSegment
{
    public override string TextPreview => "[Image]";
}

public sealed record RecordSegment(Asset Asset, TimeSpan? Duration = null) : MessageSegment
{
    public override string TextPreview => "[Voice]";
}

public sealed record AudioSegment(Asset Asset) : MessageSegment
{
    public override string TextPreview => "[Audio]";
}

public sealed record LocationSegment(double Latitude, double Longitude, string Title, string Content) : MessageSegment
{
    public override string TextPreview => $"[Location: {Title}]";
}

public sealed record VideoSegment(Asset Asset, Asset? Thumbnail = null) : MessageSegment
{
    public override string TextPreview => "[Video]";
}

public sealed record FileSegment(Asset Asset) : MessageSegment
{
    public override string TextPreview => $"[File: {Asset.Name}]";
}

public sealed record ReplySegment(string MessageId, string? UserId = null) : MessageSegment
{
    public override string TextPreview => string.Empty;
}

public sealed record PokeSegment(string? UserId) : MessageSegment
{
    public override string TextPreview => "[Nudge]";
}

public sealed record ForwardNode
{
    [JsonConstructor]
    public ForwardNode(string senderId, string senderName, IReadOnlyList<MessageSegment> content, string id, DateTimeOffset time)
        : this(senderId, senderName, (IEnumerable<MessageSegment>)content, id, (DateTimeOffset?)time)
    {
    }

    public ForwardNode(
        string senderId,
        string senderName,
        IEnumerable<MessageSegment> content,
        string? id = null,
        DateTimeOffset? time = null)
    {
        SenderId = senderId;
        SenderName = senderName;
        Content = content.ToArray();
        Id = id ?? IdGenerator.MessageId();
        Time = time ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; init; }
    public string SenderId { get; init; }
    public string SenderName { get; init; }
    public DateTimeOffset Time { get; init; }
    public IReadOnlyList<MessageSegment> Content { get; init; }
}

public sealed record ForwardSegment(
    string Id,
    IReadOnlyList<ForwardNode> Nodes,
    string? Title = null,
    string? Summary = null,
    IReadOnlyList<string>? Preview = null,
    string? Prompt = null) : MessageSegment
{
    public override string TextPreview => "[Forwarded messages]";
}

public sealed record UnsupportedSegment(string Type, JsonValue Payload) : MessageSegment
{
    public override string TextPreview => $"[{Type}]";
}

public static class MessageContentExtensions
{
    public static string TextPreview(this IEnumerable<MessageSegment> content) =>
        string.Concat(content.Select(segment => segment.TextPreview)).Trim();

    public static string PlainText(this IEnumerable<MessageSegment> content) =>
        string.Concat(content.OfType<TextSegment>().Select(segment => segment.Text));

    public static string? ReplyTarget(this IEnumerable<MessageSegment> content) =>
        content.OfType<ReplySegment>().Select(segment => segment.MessageId).FirstOrDefault();

    public static IReadOnlyList<string?> Mentions(this IEnumerable<MessageSegment> content) =>
        content.OfType<MentionSegment>().Select(segment => segment.UserId).ToArray();
}

internal sealed class MessageSegmentJsonConverter : JsonConverter<MessageSegment>
{
    public override MessageSegment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "unknown" : "unknown";
        return type switch
        {
            "text" => new TextSegment(GetString(root, "text") ?? string.Empty),
            "anonymous" => new AnonymousSegment(root.TryGetProperty("ignore", out var ignore) && ignore.GetBoolean()),
            "mention" => new MentionSegment(GetString(root, "user_id")),
            "face" => new FaceSegment(GetString(root, "id") ?? string.Empty, GetString(root, "name"),
                root.TryGetProperty("is_large", out var large) && large.GetBoolean()),
            "image" => new ImageSegment(GetAsset(root, options), GetString(root, "sub_type") ?? "normal", GetString(root, "summary")),
            "record" => new RecordSegment(GetAsset(root, options), GetDuration(root)),
            "audio" => new AudioSegment(GetAsset(root, options)),
            "location" => new LocationSegment(root.GetProperty("latitude").GetDouble(), root.GetProperty("longitude").GetDouble(),
                GetString(root, "title") ?? string.Empty, GetString(root, "content") ?? string.Empty),
            "video" => new VideoSegment(GetAsset(root, options),
                root.TryGetProperty("thumbnail", out var thumbnail) ? thumbnail.Deserialize<Asset>(options) : null),
            "file" => new FileSegment(GetAsset(root, options)),
            "reply" => new ReplySegment(GetString(root, "message_id") ?? string.Empty, GetString(root, "user_id")),
            "poke" => new PokeSegment(GetString(root, "user_id")),
            "forward" => new ForwardSegment(
                GetString(root, "id") ?? string.Empty,
                root.TryGetProperty("nodes", out var nodes)
                    ? nodes.Deserialize<IReadOnlyList<ForwardNode>>(options) ?? []
                    : [],
                GetString(root, "title"), GetString(root, "summary"),
                root.TryGetProperty("preview", out var preview) ? preview.Deserialize<IReadOnlyList<string>>(options) : null,
                GetString(root, "prompt")),
            "unsupported" => new UnsupportedSegment(
                GetString(root, "original_type") ?? "unknown",
                root.TryGetProperty("payload", out var payload) ? new JsonValue(payload) : JsonValue.Null),
            _ => new UnsupportedSegment(type, JsonValue.Null),
        };
    }

    public override void Write(Utf8JsonWriter writer, MessageSegment value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextSegment text:
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                break;
            case AnonymousSegment anonymous:
                writer.WriteString("type", "anonymous");
                writer.WriteBoolean("ignore", anonymous.Ignore);
                break;
            case MentionSegment mention:
                writer.WriteString("type", "mention");
                WriteOptionalString(writer, "user_id", mention.UserId);
                break;
            case FaceSegment face:
                writer.WriteString("type", "face");
                writer.WriteString("id", face.Id);
                WriteOptionalString(writer, "name", face.Name);
                writer.WriteBoolean("is_large", face.IsLarge);
                break;
            case ImageSegment image:
                WriteAsset(writer, "image", image.Asset, options);
                writer.WriteString("sub_type", image.SubType);
                WriteOptionalString(writer, "summary", image.Summary);
                break;
            case RecordSegment record:
                WriteAsset(writer, "record", record.Asset, options);
                if (record.Duration is { } duration)
                {
                    writer.WriteNumber("duration", duration.TotalSeconds);
                }
                break;
            case AudioSegment audio:
                WriteAsset(writer, "audio", audio.Asset, options);
                break;
            case LocationSegment location:
                writer.WriteString("type", "location");
                writer.WriteNumber("latitude", location.Latitude);
                writer.WriteNumber("longitude", location.Longitude);
                writer.WriteString("title", location.Title);
                writer.WriteString("content", location.Content);
                break;
            case VideoSegment video:
                WriteAsset(writer, "video", video.Asset, options);
                if (video.Thumbnail is { } thumbnail)
                {
                    writer.WritePropertyName("thumbnail");
                    JsonSerializer.Serialize(writer, thumbnail, options);
                }
                break;
            case FileSegment file:
                WriteAsset(writer, "file", file.Asset, options);
                break;
            case ReplySegment reply:
                writer.WriteString("type", "reply");
                writer.WriteString("message_id", reply.MessageId);
                WriteOptionalString(writer, "user_id", reply.UserId);
                break;
            case PokeSegment poke:
                writer.WriteString("type", "poke");
                WriteOptionalString(writer, "user_id", poke.UserId);
                break;
            case ForwardSegment forward:
                writer.WriteString("type", "forward");
                writer.WriteString("id", forward.Id);
                writer.WritePropertyName("nodes");
                JsonSerializer.Serialize(writer, forward.Nodes, options);
                WriteOptionalString(writer, "title", forward.Title);
                WriteOptionalString(writer, "summary", forward.Summary);
                WriteOptionalString(writer, "prompt", forward.Prompt);
                if (forward.Preview is { } preview)
                {
                    writer.WritePropertyName("preview");
                    JsonSerializer.Serialize(writer, preview, options);
                }
                break;
            case UnsupportedSegment unsupported:
                writer.WriteString("type", "unsupported");
                writer.WriteString("original_type", unsupported.Type);
                writer.WritePropertyName("payload");
                unsupported.Payload.WriteTo(writer);
                break;
            default:
                throw new JsonException($"Unknown message segment type {value.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static Asset GetAsset(JsonElement root, JsonSerializerOptions options) =>
        root.GetProperty("asset").Deserialize<Asset>(options) ?? throw new JsonException("Missing asset.");

    private static TimeSpan? GetDuration(JsonElement root) =>
        root.TryGetProperty("duration", out var value) ? TimeSpan.FromSeconds(value.GetDouble()) : null;

    private static void WriteAsset(Utf8JsonWriter writer, string type, Asset asset, JsonSerializerOptions options)
    {
        writer.WriteString("type", type);
        writer.WritePropertyName("asset");
        JsonSerializer.Serialize(writer, asset, options);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

internal sealed class AssetSourceJsonConverter : JsonConverter<AssetSource>
{
    public override AssetSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        var value = root.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
        return kind?.ToLowerInvariant() switch
        {
            "local" => AssetSource.Local(value ?? string.Empty),
            "remote" => AssetSource.Remote(value ?? string.Empty),
            _ => AssetSource.Inline,
        };
    }

    public override void Write(Utf8JsonWriter writer, AssetSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        if (value.Value is not null)
        {
            writer.WriteString("value", value.Value);
        }

        writer.WriteEndObject();
    }
}
