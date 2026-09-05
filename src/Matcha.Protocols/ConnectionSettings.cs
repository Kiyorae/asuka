using System.Net;

namespace Matcha.Protocols;

public enum ProtocolKind
{
    OneBotV11,
    OneBotV12,
    Milky,
}

public enum TransportMode
{
    WebSocketServer,
    WebSocketClient,
    MilkyService,
}

public sealed record ConnectionSettings
{
    public const ushort DefaultPort = 5700;

    public TransportMode Transport { get; init; } = TransportMode.WebSocketServer;

    public string Host { get; init; } = IPAddress.Loopback.ToString();

    public ushort Port { get; init; } = DefaultPort;

    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Optional host placed in generated media URLs when it differs from the bind address.
    /// This is useful when binding a wildcard address or on a multi-homed machine.
    /// </summary>
    public string? AdvertisedHost { get; init; }

    public string AccessToken { get; init; } = string.Empty;

    public IReadOnlyList<string> MilkyWebhookUrls { get; init; } = [];

    public bool AutoReconnect { get; init; } = true;

    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(3);

    public bool PostSelfEvents { get; init; }

    /// <summary>
    /// Allows an unauthenticated listener or a clear-text listener beyond loopback.
    /// This deliberately unsafe override is only appropriate for an isolated test network.
    /// </summary>
    public bool AllowInsecureRemoteAccess { get; init; }

    public Uri BuildWebSocketUri(string defaultPath = "/")
    {
        var configuredPath = string.IsNullOrWhiteSpace(Path) ? defaultPath : Path.Trim();
        configuredPath = string.IsNullOrEmpty(configuredPath) || configuredPath == "/"
            ? "/"
            : configuredPath.StartsWith('/') ? configuredPath : $"/{configuredPath}";

        return new UriBuilder(Uri.UriSchemeWs, Host, Port, configuredPath).Uri;
    }

    public Uri EventStreamUri => new UriBuilder(Uri.UriSchemeWs, Host, Port, "/event").Uri;

    public IReadOnlyList<Uri> GetWebhookEndpoints()
    {
        var result = new List<Uri>(MilkyWebhookUrls.Count);
        foreach (var raw in MilkyWebhookUrls)
        {
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new ArgumentException($"Invalid Milky WebHook URL: {raw}", nameof(MilkyWebhookUrls));
            }

            if (uri.Scheme == Uri.UriSchemeHttp
                && !IsLoopbackHost(uri.Host)
                && !AllowInsecureRemoteAccess)
            {
                throw new InvalidOperationException(
                    "A non-loopback Milky WebHook must use HTTPS. "
                    + "Set AllowInsecureRemoteAccess only for an isolated test network.");
            }

            result.Add(uri);
        }

        return result;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host cannot be empty.", nameof(Host));
        }

        if (AdvertisedHost is { } advertisedHost
            && (string.IsNullOrWhiteSpace(advertisedHost)
                || Uri.CheckHostName(advertisedHost.Trim('[', ']')) == UriHostNameType.Unknown))
        {
            throw new ArgumentException("Advertised host must be a DNS name or IP address.", nameof(AdvertisedHost));
        }

        if (AdvertisedHost is { } configuredAdvertisedHost
            && !IsLoopbackHost(configuredAdvertisedHost.Trim('[', ']'))
            && !AllowInsecureRemoteAccess)
        {
            throw new InvalidOperationException(
                "A non-loopback advertised host would publish clear-text asset URLs. "
                + "Set AllowInsecureRemoteAccess only for an isolated test network.");
        }

        if (ReconnectInterval < TimeSpan.FromSeconds(1) || ReconnectInterval > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReconnectInterval),
                "Reconnect interval must be between 1 and 60 seconds.");
        }

        if (Transport == TransportMode.MilkyService)
        {
            _ = GetWebhookEndpoints();
        }

        if (Transport is TransportMode.WebSocketServer or TransportMode.MilkyService)
        {
            if (string.IsNullOrWhiteSpace(AccessToken) && !AllowInsecureRemoteAccess)
            {
                throw new InvalidOperationException(
                    "A server listener requires a non-empty Access Token. "
                    + "Set AllowInsecureRemoteAccess only for an isolated test network.");
            }

            if (!IsLoopbackHost(Host) && !AllowInsecureRemoteAccess)
            {
                throw new InvalidOperationException(
                    "A clear-text listener may only bind to loopback. "
                    + "Set AllowInsecureRemoteAccess only for an isolated test network.");
            }
        }

        if (Transport == TransportMode.WebSocketClient
            && !IsLoopbackHost(Host)
            && !AllowInsecureRemoteAccess)
        {
            throw new InvalidOperationException(
                "A non-loopback WebSocket client would use clear-text ws://. "
                + "Set AllowInsecureRemoteAccess only for an isolated test network.");
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
