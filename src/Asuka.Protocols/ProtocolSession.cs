using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asuka.Protocols;

/// <summary>
/// Connects a protocol translator to Microsoft's native .NET networking stack.
/// No browser engine participates in protocol handling or rendering.
/// </summary>
public sealed class ProtocolSession : IAsyncDisposable
{
    private const long MaxPayloadBytes = 64L * 1024 * 1024;
    private readonly IProtocolImplementation _implementation;
    private readonly PlatformService _platform;
    private readonly AssetStore? _assets;
    private readonly ConnectionSettings _settings;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly ConcurrentDictionary<Guid, SocketPeer> _peers = new();
    private readonly ConcurrentDictionary<Guid, Task> _peerTasks = new();
    private readonly HttpClient _webhookClient;
    private CancellationTokenSource? _lifetime;
    private WebApplication? _server;
    private Task? _eventPump;
    private Task? _clientPump;
    private Task? _heartbeatPump;
    private SessionState _state = new(SessionStateKind.Idle);
    private Task? _disposeTask;
    private int _disposed;

    public ProtocolSession(
        IProtocolImplementation implementation,
        PlatformService platform,
        ConnectionSettings settings,
        AssetStore? assets = null)
    {
        _implementation = implementation;
        _platform = platform;
        _settings = settings;
        _assets = assets;
        _webhookClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public event EventHandler<SessionState>? StateChanged;

    public event EventHandler<RoundTripTimeState>? RoundTripTimeChanged;

    public event EventHandler<TrafficEntry>? TrafficObserved;

    public event EventHandler<Exception>? OutboundDeliveryFailed;

    public SessionState State => _state;

    public RoundTripTimeState RoundTripTime { get; private set; } = new(RoundTripTimeKind.Unavailable);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lifetime is not null)
            {
                throw new InvalidOperationException("The protocol session is already running.");
            }

            _settings.Validate();
            if (!_implementation.SupportedTransports.Contains(_settings.Transport))
            {
                throw new UnsupportedTransportException(_implementation.Identifier, _settings.Transport);
            }

            _platform.SetEchoesSelfEvents(_settings.PostSelfEvents);
            _lifetime = new CancellationTokenSource();
            var lifetimeToken = _lifetime.Token;
            _eventPump = PumpEventsAsync(lifetimeToken);

            try
            {
                switch (_settings.Transport)
                {
                    case TransportMode.WebSocketServer:
                        await StartServerAsync(milky: false, lifetimeToken, cancellationToken).ConfigureAwait(false);
                        SetState(new SessionState(SessionStateKind.Listening, _settings.Port));
                        break;
                    case TransportMode.WebSocketClient:
                        SetState(new SessionState(SessionStateKind.Connecting));
                        _clientPump = RunClientLoopAsync(lifetimeToken);
                        break;
                    case TransportMode.MilkyService:
                        await StartServerAsync(milky: true, lifetimeToken, cancellationToken).ConfigureAwait(false);
                        SetRoundTripTime(new RoundTripTimeState(RoundTripTimeKind.Unsupported));
                        SetState(new SessionState(SessionStateKind.Ready, _settings.Port));
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported transport: {_settings.Transport}.");
                }

                if (_implementation.HeartbeatInterval is { } interval && interval > TimeSpan.Zero)
                {
                    _heartbeatPump = RunHeartbeatLoopAsync(interval, lifetimeToken);
                }
            }
            catch
            {
                _lifetime.Cancel();
                _lifetime.Dispose();
                _lifetime = null;
                SetState(new SessionState(SessionStateKind.Idle));
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lifetime = _lifetime;
            if (lifetime is null)
            {
                SetState(new SessionState(SessionStateKind.Idle));
                return;
            }

            _lifetime = null;
            lifetime.Cancel();
            try
            {
                var closeTasks = _peers.Values.Select(peer => peer.CloseAsync(CancellationToken.None));
                await IgnoreCancellationAsync(closeTasks).ConfigureAwait(false);
                _peers.Clear();

                if (_server is { } server)
                {
                    _server = null;
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await server.StopAsync(stopTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                }

                var pumps = new[] { _eventPump, _clientPump, _heartbeatPump }
                    .Concat(_peerTasks.Values)
                    .Where(static task => task is not null)
                    .Cast<Task>()
                    .ToArray();
                _eventPump = null;
                _clientPump = null;
                _heartbeatPump = null;
                _peerTasks.Clear();
                await IgnoreCancellationAsync(pumps).ConfigureAwait(false);
            }
            finally
            {
                lifetime.Dispose();
                _server = null;
                _eventPump = null;
                _clientPump = null;
                _heartbeatPump = null;
                _peerTasks.Clear();
                _peers.Clear();
                SetRoundTripTime(new RoundTripTimeState(RoundTripTimeKind.Unavailable));
                SetState(new SessionState(SessionStateKind.Idle));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartServerAsync(
        bool milky,
        CancellationToken sessionToken,
        CancellationToken startupToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProtocolSession).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => ConfigureListener(options, _settings.Host, _settings.Port));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        app.Run(context => milky
            ? HandleMilkyRequestAsync(context, sessionToken)
            : HandleOneBotUpgradeAsync(context, sessionToken));

        try
        {
            await app.StartAsync(startupToken).ConfigureAwait(false);
            _server = app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ConfigureListener(KestrelServerOptions options, string host, ushort port)
    {
        options.Limits.MaxRequestBodySize = MaxPayloadBytes;
        options.Limits.MaxRequestHeadersTotalSize = 64 * 1024;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port, static listen => listen.Protocols = HttpProtocols.Http1);
            return;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            throw new ArgumentException(
                "A listening host must be localhost or a numeric IPv4/IPv6 address.",
                nameof(host));
        }

        options.Listen(address, port, static listen => listen.Protocols = HttpProtocols.Http1);
    }

    private async Task HandleOneBotUpgradeAsync(HttpContext context, CancellationToken sessionToken)
    {
        if (RejectBrowserOrigin(context))
        {
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (!context.WebSockets.IsWebSocketRequest && path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await HandleAssetRequestAsync(context, path[8..], sessionToken).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsync("A WebSocket upgrade is required.", sessionToken).ConfigureAwait(false);
            return;
        }

        if (!TokenAuthentication.IsOneBotAuthorized(context.Request, _settings.AccessToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var acceptedSubprotocol = SelectServerSubprotocol(context.Request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        var socket = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol).ConfigureAwait(false);
        var peer = new SocketPeer(socket);
        await RunBidirectionalPeerAsync(peer, linked.Token).ConfigureAwait(false);
    }

    private static string? SelectServerSubprotocol(HttpRequest request)
    {
        foreach (var protocol in request.Headers["Sec-WebSocket-Protocol"]
                     .SelectMany(static value => (value ?? string.Empty).Split(','))
                     .Select(static value => value.Trim()))
        {
            if (protocol.Equals("12.asuka", StringComparison.Ordinal))
            {
                return protocol;
            }
        }

        return null;
    }

    private async Task HandleMilkyRequestAsync(HttpContext context, CancellationToken sessionToken)
    {
        if (RejectBrowserOrigin(context))
        {
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (path.Equals("/event", StringComparison.Ordinal))
        {
            await HandleMilkyEventSocketAsync(context, sessionToken).ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            await WriteMilkyFailureAsync(
                context,
                -400,
                "Transfer-Encoding is not supported",
                StatusCodes.Status400BadRequest,
                sessionToken).ConfigureAwait(false);
            return;
        }

        if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken))
        {
            await WriteMilkyFailureAsync(
                context,
                -403,
                "Access Token validation failed",
                StatusCodes.Status401Unauthorized,
                sessionToken).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            await HandleAssetRequestAsync(context, path[8..], sessionToken).ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            || !path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await WriteMilkyFailureAsync(
                context,
                -404,
                $"Path not found: {path}",
                StatusCodes.Status404NotFound,
                sessionToken).ConfigureAwait(false);
            return;
        }

        var action = path[5..];
        if (string.IsNullOrWhiteSpace(action))
        {
            await WriteMilkyFailureAsync(
                context,
                -404,
                "No API name specified",
                StatusCodes.Status404NotFound,
                sessionToken).ConfigureAwait(false);
            return;
        }

        if ((context.Request.ContentLength ?? 0) > 0
            && !(context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            await WriteMilkyFailureAsync(
                context,
                -400,
                "Request body must be application/json",
                StatusCodes.Status415UnsupportedMediaType,
                sessionToken).ConfigureAwait(false);
            return;
        }

        JsonObject parameters;
        try
        {
            parameters = (context.Request.ContentLength ?? 0) == 0
                ? new JsonObject()
                : await JsonNode.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted).ConfigureAwait(false) as JsonObject
                    ?? throw new JsonException("Expected an object.");
        }
        catch (JsonException)
        {
            await WriteMilkyFailureAsync(
                context,
                -400,
                "Request body is not valid JSON",
                StatusCodes.Status200OK,
                sessionToken).ConfigureAwait(false);
            return;
        }

        ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, parameters));
        var reply = await _implementation.HandleAsync(
            new ProtocolCall(action, parameters),
            context.RequestAborted).ConfigureAwait(false);
        var envelope = _implementation.CreateEnvelope(reply);
        ObserveTraffic(new TrafficEntry(TrafficDirection.Reply, $"{action} → {reply.RetCode}", envelope));

        context.Response.StatusCode = reply.HttpStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(envelope.ToCompactJson(), context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleMilkyEventSocketAsync(HttpContext context, CancellationToken sessionToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMilkyFailureAsync(
                context,
                -400,
                "/event only accepts GET",
                StatusCodes.Status405MethodNotAllowed,
                sessionToken).ConfigureAwait(false);
            return;
        }

        if (!TokenAuthentication.IsMilkyEventAuthorized(context.Request, _settings.AccessToken))
        {
            await WriteMilkyFailureAsync(
                context,
                -403,
                "Access Token validation failed",
                StatusCodes.Status401Unauthorized,
                sessionToken).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteMilkyFailureAsync(
                context,
                -400,
                "/event requires a WebSocket upgrade",
                StatusCodes.Status426UpgradeRequired,
                sessionToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var peer = new SocketPeer(socket);
        _peers[peer.Id] = peer;
        try
        {
            await DrainPushOnlyPeerAsync(peer, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _peers.TryRemove(peer.Id, out _);
            await peer.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleAssetRequestAsync(HttpContext context, string assetId, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || _assets is null || string.IsNullOrWhiteSpace(assetId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            var bytes = await _assets.GetBytesAsync(assetId, cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>
    /// Asuka endpoints are native bot transports, never browser-facing APIs.  A browser
    /// supplies Origin during fetch/WebSocket requests, so reject it before authentication
    /// and before returning any protocol or asset data.
    /// </summary>
    private static bool RejectBrowserOrigin(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("Origin"))
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return true;
    }

    private static async Task WriteMilkyFailureAsync(
        HttpContext context,
        int retCode,
        string message,
        int httpStatus,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["status"] = "failed",
            ["retcode"] = retCode,
            ["message"] = message,
        };
        context.Response.StatusCode = httpStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(payload.ToCompactJson(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunClientLoopAsync(CancellationToken cancellationToken)
    {
        var firstAttempt = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!firstAttempt)
            {
                SetState(new SessionState(SessionStateKind.Connecting));
            }

            firstAttempt = false;
            using var socket = new ClientWebSocket();
            ConfigureClient(socket.Options);
            try
            {
                await socket.ConnectAsync(
                    _settings.BuildWebSocketUri(_implementation.ClientHandshake.DefaultPath),
                    cancellationToken).ConfigureAwait(false);
                var peer = new SocketPeer(socket, ownsSocket: false);
                await RunBidirectionalPeerAsync(peer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (_settings.AutoReconnect && !cancellationToken.IsCancellationRequested)
            {
                SetState(new SessionState(SessionStateKind.Connecting, Error: error.Message));
            }
            catch (Exception error)
            {
                SetState(new SessionState(SessionStateKind.Failed, Error: error.Message));
                return;
            }

            if (!_settings.AutoReconnect || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(_settings.ReconnectInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ConfigureClient(ClientWebSocketOptions options)
    {
        foreach (var (name, value) in _implementation.ClientHandshake.Headers)
        {
            options.SetRequestHeader(name, value);
        }

        if (!string.IsNullOrEmpty(_settings.AccessToken))
        {
            options.SetRequestHeader("Authorization", $"Bearer {_settings.AccessToken}");
        }

        foreach (var subprotocol in _implementation.ClientHandshake.Subprotocols)
        {
            options.AddSubProtocol(subprotocol);
        }

        options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
    }

    private async Task RunBidirectionalPeerAsync(SocketPeer peer, CancellationToken cancellationToken)
    {
        var handshakeFrames = await _implementation.GetHandshakeFramesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var frame in handshakeFrames)
        {
            await SendAsync(peer, frame, observe: true, cancellationToken).ConfigureAwait(false);
        }

        _peers[peer.Id] = peer;
        SetState(new SessionState(SessionStateKind.Connected));
        SetRoundTripTime(new RoundTripTimeState(RoundTripTimeKind.Unavailable));
        try
        {
            while (!cancellationToken.IsCancellationRequested && peer.Socket.State == WebSocketState.Open)
            {
                var text = await peer.ReceiveTextAsync(MaxPayloadBytes, cancellationToken).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                await HandleOneBotFrameAsync(peer, text, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _peers.TryRemove(peer.Id, out _);
            await peer.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            if (_settings.Transport == TransportMode.WebSocketServer && _peers.IsEmpty)
            {
                SetState(new SessionState(SessionStateKind.Listening, _settings.Port));
            }
        }
    }

    private static async Task DrainPushOnlyPeerAsync(SocketPeer peer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && peer.Socket.State == WebSocketState.Open)
        {
            var text = await peer.ReceiveTextAsync(MaxPayloadBytes, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return;
            }
        }
    }

    private async Task HandleOneBotFrameAsync(
        SocketPeer peer,
        string text,
        CancellationToken cancellationToken)
    {
        JsonObject payload;
        try
        {
            payload = JsonExtensions.ParseObject(text);
        }
        catch (JsonException)
        {
            ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, "Unparseable frame", text));
            return;
        }

        var action = payload.GetFlexibleString("action");
        if (string.IsNullOrWhiteSpace(action))
        {
            ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, "Unparseable frame", payload));
            return;
        }

        var parameters = payload["params"] as JsonObject
            ?? payload["data"] as JsonObject
            ?? new JsonObject();
        var echo = payload["echo"]?.DeepClone();
        ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, payload));
        var reply = await _implementation.HandleAsync(
            new ProtocolCall(action, (JsonObject)parameters.DeepClone(), echo),
            cancellationToken).ConfigureAwait(false);
        var envelope = _implementation.CreateEnvelope(reply, echo);
        ObserveTraffic(new TrafficEntry(TrafficDirection.Reply, $"{action} → {reply.RetCode}", envelope));
        await SendJsonAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var domainEvent in _platform.Events(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<OutboundFrame> frames;
                try
                {
                    frames = await _implementation.EncodeAsync(domainEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    OutboundDeliveryFailed?.Invoke(this, error);
                    continue;
                }
                foreach (var frame in frames)
                {
                    ObserveTraffic(new TrafficEntry(
                        TrafficDirection.OutboundEvent,
                        Summarize(frame),
                        frame.Payload));

                    var peerSends = _peers.Values.Select(
                        peer => SendAsync(peer, frame, observe: false, cancellationToken));
                    await Task.WhenAll(peerSends).ConfigureAwait(false);

                    if (_settings.Transport == TransportMode.MilkyService)
                    {
                        var deliveries = _settings.GetWebhookEndpoints().Select(
                            endpoint => PostWebhookAsync(endpoint, frame, cancellationToken));
                        await Task.WhenAll(deliveries).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunHeartbeatLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var frame = await _implementation.GetHeartbeatFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    continue;
                }

                var sends = _peers.Values.Select(peer => SendAsync(peer, frame, observe: true, cancellationToken));
                await Task.WhenAll(sends).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PostWebhookAsync(
        Uri endpoint,
        OutboundFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(frame.Payload.ToCompactJson(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_settings.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
            }

            using var response = await _webhookClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"WebHook returned HTTP {(int)response.StatusCode}.",
                    inner: null,
                    response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            OutboundDeliveryFailed?.Invoke(this, error);
        }
    }

    private async Task SendAsync(
        SocketPeer peer,
        OutboundFrame frame,
        bool observe,
        CancellationToken cancellationToken)
    {
        if (observe)
        {
            ObserveTraffic(new TrafficEntry(TrafficDirection.OutboundEvent, Summarize(frame), frame.Payload));
        }

        try
        {
            await SendJsonAsync(peer, frame.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _peers.TryRemove(peer.Id, out _);
            await peer.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            OutboundDeliveryFailed?.Invoke(this, error);
            RefreshSocketState();
        }
    }

    private static Task SendJsonAsync(SocketPeer peer, JsonNode payload, CancellationToken cancellationToken)
    {
        return peer.SendTextAsync(payload.ToCompactJson(), cancellationToken);
    }

    private static string Summarize(OutboundFrame frame)
    {
        var payload = frame.Payload;
        var eventType = payload.GetFlexibleString("event_type");
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            return eventType;
        }

        var postType = payload.GetFlexibleString("post_type");
        if (!string.IsNullOrWhiteSpace(postType))
        {
            var detail = payload.GetFlexibleString("message_type")
                ?? payload.GetFlexibleString("notice_type")
                ?? payload.GetFlexibleString("meta_event_type");
            return detail is null ? postType : $"{postType}.{detail}";
        }

        var type = payload.GetFlexibleString("type");
        if (!string.IsNullOrWhiteSpace(type))
        {
            var detail = payload.GetFlexibleString("detail_type");
            return detail is null ? type : $"{type}.{detail}";
        }

        return frame.EventName ?? "Event";
    }

    private void SetState(SessionState value)
    {
        _state = value;
        StateChanged?.Invoke(this, value);
    }

    private void SetRoundTripTime(RoundTripTimeState value)
    {
        RoundTripTime = value;
        RoundTripTimeChanged?.Invoke(this, value);
    }

    private void ObserveTraffic(TrafficEntry entry) => TrafficObserved?.Invoke(this, entry);

    private void RefreshSocketState()
    {
        if (!_peers.IsEmpty)
        {
            return;
        }

        if (_settings.Transport == TransportMode.WebSocketServer && _lifetime is not null)
        {
            SetState(new SessionState(SessionStateKind.Listening, _settings.Port));
        }
    }

    private static async Task IgnoreCancellationAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The authoritative failure was already surfaced as state/diagnostics.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _webhookClient.Dispose();
    }

    private sealed class SocketPeer(WebSocket socket, bool ownsSocket = true) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly bool _ownsSocket = ownsSocket;
        private int _closed;

        internal Guid Id { get; } = Guid.NewGuid();

        internal WebSocket Socket { get; } = socket;

        internal async Task<string?> ReceiveTextAsync(long maxBytes, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage)
                    {
                        return string.Empty;
                    }

                    continue;
                }

                if (stream.Length + result.Count > maxBytes)
                {
                    await Socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "Payload exceeded the 64 MiB limit.",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                }
            }
        }

        internal async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _closed) == 0 && Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }

        internal async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Asuka session stopped.",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                if (_ownsSocket)
                {
                    Socket.Dispose();
                }

                _sendGate.Release();
            }
        }

        public ValueTask DisposeAsync() => new(CloseAsync(CancellationToken.None));
    }
}

internal static class TokenAuthentication
{
    internal static bool IsOneBotAuthorized(HttpRequest request, string configuredToken)
    {
        if (string.IsNullOrEmpty(configuredToken))
        {
            return true;
        }

        var presented = BearerToken(request);
        if (presented is null && request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
        {
            presented = protocols
                .SelectMany(static value => (value ?? string.Empty).Split(','))
                .Select(static value => value.Trim())
                .FirstOrDefault(static value => value.StartsWith("token.", StringComparison.Ordinal))?[6..];
        }

        return FixedEquals(configuredToken, presented);
    }

    internal static bool IsBearerAuthorized(HttpRequest request, string configuredToken)
    {
        return string.IsNullOrEmpty(configuredToken) || FixedEquals(configuredToken, BearerToken(request));
    }

    internal static bool IsMilkyEventAuthorized(HttpRequest request, string configuredToken)
    {
        if (string.IsNullOrEmpty(configuredToken))
        {
            return true;
        }

        var presented = BearerToken(request) ?? request.Query["access_token"].FirstOrDefault();
        return FixedEquals(configuredToken, presented);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? value[prefix.Length..]
            : null;
    }

    private static bool FixedEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
