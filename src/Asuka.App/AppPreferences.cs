using System.Text.Json;
using System.Text.Json.Serialization;
using Asuka.Protocols;

namespace Asuka.App;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppPreferences
{
    public ProtocolKind Protocol { get; init; } = ProtocolKind.OneBotV11;
    public TransportMode Transport { get; init; } = TransportMode.WebSocketServer;
    public string Host { get; init; } = "127.0.0.1";
    public ushort Port { get; init; } = ConnectionSettings.DefaultPort;
    public string Path { get; init; } = string.Empty;
    public string? AdvertisedHost { get; init; }
    [JsonIgnore]
    public string AccessToken { get; init; } = string.Empty;
    public string WebhookUrls { get; init; } = string.Empty;
    public string OneBotWebhookUrls { get; init; } = string.Empty;
    [JsonIgnore]
    public string OneBotWebhookSecret { get; init; } = string.Empty;
    public long OneBotWebhookTimeoutMilliseconds { get; init; }
    public bool AutoReconnect { get; init; } = true;
    public int ReconnectSeconds { get; init; } = 3;
    public int OneBotRateLimitMilliseconds { get; init; } = 500;
    public bool HeartbeatEnabled { get; init; }
    public int HeartbeatIntervalMilliseconds { get; init; } = 15_000;
    public bool OneBotHttpEventsEnabled { get; init; } = true;
    public int OneBotHttpEventBufferSize { get; init; } = 256;
    public bool PostSelfEvents { get; init; }
    public string? ActiveUserId { get; init; }
    public string? BotUserId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PersonaId { get; init; }
    public AppThemePreference Theme { get; init; } = AppThemePreference.System;

    public ConnectionSettings ToConnectionSettings()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HeartbeatIntervalMilliseconds);
        var transport = Protocol == ProtocolKind.Milky ? TransportMode.MilkyService : Transport;
        return new ConnectionSettings
        {
            Transport = transport,
            Host = Host.Trim(),
            Port = Port,
            Path = Path.Trim(),
            AdvertisedHost = string.IsNullOrWhiteSpace(AdvertisedHost) ? null : AdvertisedHost.Trim(),
            AccessToken = AccessToken,
            MilkyWebhookUrls = WebhookUrls
                .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            OneBotWebhookUrls = OneBotWebhookUrls
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            OneBotWebhookSecret = OneBotWebhookSecret,
            OneBotWebhookTimeout = TimeSpan.FromMilliseconds(OneBotWebhookTimeoutMilliseconds),
            AutoReconnect = AutoReconnect,
            ReconnectInterval = TimeSpan.FromSeconds(ReconnectSeconds),
            PostSelfEvents = PostSelfEvents,
            OneBotHttpEventsEnabled = OneBotHttpEventsEnabled,
            OneBotHttpEventBufferSize = OneBotHttpEventBufferSize,
        };
    }

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
