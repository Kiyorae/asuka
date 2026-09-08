using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Asuka.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asuka.Protocols;

/// <summary>
/// Connects a protocol translator to Microsoft's native .NET networking stack.
/// No browser engine participates in protocol handling or rendering.
/// </summary>
public sealed partial class ProtocolSession : IAsyncDisposable
{
    private const long MaxPayloadBytes = 64L * 1024 * 1024;
    private readonly IProtocolImplementation _implementation;
    private readonly PlatformService _platform;
    private readonly AssetStore? _assets;
    private readonly ConnectionSettings _settings;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly ConcurrentDictionary<Guid, SocketPeer> _peers = new();
    private readonly ConcurrentDictionary<Guid, SsePeer> _ssePeers = new();
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
        if (_implementation is OneBotProtocol oneBot) oneBot.ScheduledActionFailed += OnScheduledActionFailed;
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
    public event EventHandler<Exception>? DeferredActionFailed;

    private void OnScheduledActionFailed(object? sender, OneBotScheduledActionFailure failure) =>
        DeferredActionFailed?.Invoke(this, new InvalidOperationException($"Deferred action {failure.Action} failed with code {failure.RetCode}."));

    public SessionState State => _state;

    public RoundTripTimeState RoundTripTime { get; private set; } = new(RoundTripTimeKind.Unavailable);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // The lifecycle gate is held by the public entry points and restart worker.
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        if (_implementation is OneBotProtocol oneBot) oneBot.ResumeScheduledActions();
        ConfigureOneBotHttpEvents(_settings.OneBotHttpEventsEnabled, _settings.OneBotHttpEventBufferSize);
        _lifetime = new CancellationTokenSource();
        var lifetimeToken = _lifetime.Token;
        InstallRestartHandler();

        try
        {
            ConfigureOneBotWebhooks(lifetimeToken);
            _eventPump = PumpEventsAsync(lifetimeToken);
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
                case TransportMode.OneBotHttpServer:
                    await StartServerAsync(milky: false, lifetimeToken, cancellationToken).ConfigureAwait(false);
                    SetRoundTripTime(new RoundTripTimeState(RoundTripTimeKind.Unsupported));
                    SetState(new SessionState(SessionStateKind.Ready, _settings.Port));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported transport: {_settings.Transport}.");
            }

            await PublishOneBotWebhookStartupAsync(lifetimeToken).ConfigureAwait(false);

            if ((_settings.Transport != TransportMode.OneBotHttpServer || _settings.OneBotWebhookUrls.Count > 0)
                && _implementation.HeartbeatInterval is { } interval && interval > TimeSpan.Zero)
            {
                _heartbeatPump = RunHeartbeatLoopAsync(interval, lifetimeToken);
            }
        }
        catch
        {
            _ = CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops the owned runtime and cancels pending restarts. Cancellation is
    /// observed before shutdown begins; once begun, cleanup runs to completion.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restart = CancelPendingRestart();
        Task laterRestart = Task.CompletedTask;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A concurrent Start may have completed while Stop waited for the
            // lifecycle gate. Invalidate that generation as well.
            laterRestart = CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
        await Task.WhenAll(restart, laterRestart).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        DetachRestartHandler();
        var lifetime = _lifetime;
        if (lifetime is null)
        {
            await StopOneBotWebhooksAsync().ConfigureAwait(false);
            if (_implementation is OneBotProtocol oneBot)
            {
                await oneBot.StopScheduledActionsAsync().ConfigureAwait(false);
                oneBot.ClearPendingFileTransfers();
            }
            ResetOneBotHttpEvents();
            SetState(new SessionState(SessionStateKind.Idle));
            return;
        }

        _lifetime = null;
        lifetime.Cancel();
        try
        {
            await StopOneBotWebhooksAsync().ConfigureAwait(false);
            if (_implementation is OneBotProtocol stoppedOneBot)
                await stoppedOneBot.StopScheduledActionsAsync().ConfigureAwait(false);
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
            _ssePeers.Clear();
            if (_implementation is OneBotProtocol oneBot) oneBot.ClearPendingFileTransfers();
            ResetOneBotHttpEvents();
            SetRoundTripTime(new RoundTripTimeState(RoundTripTimeKind.Unavailable));
            SetState(new SessionState(SessionStateKind.Idle));
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
            : _settings.Transport == TransportMode.OneBotHttpServer
                ? HandleOneBotHttpRequestAsync(context, sessionToken)
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
            if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken)
                && !IsScopedDownloadAuthorized(context.Request, path))
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
        await RunBidirectionalPeerAsync(peer, linked.Token, sessionToken).ConfigureAwait(false);
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
            await HandleMilkyEventAsync(context, sessionToken).ConfigureAwait(false);
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

        if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken)
            && !IsScopedDownloadAuthorized(context.Request, path))
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

        if (path.StartsWith("/files/", StringComparison.Ordinal))
        {
            await HandleSharedFileRequestAsync(context, path[7..], sessionToken).ConfigureAwait(false);
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

    private async Task HandleMilkyEventAsync(HttpContext context, CancellationToken sessionToken)
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
            if (context.Request.Headers.Upgrade
                .SelectMany(static value => (value ?? string.Empty).Split(','))
                .Any(static value => value.Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteMilkyFailureAsync(
                    context,
                    -400,
                    "Invalid WebSocket upgrade request",
                    StatusCodes.Status400BadRequest,
                    sessionToken).ConfigureAwait(false);
                return;
            }
            await HandleMilkyEventStreamAsync(context, sessionToken).ConfigureAwait(false);
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

    private async Task HandleMilkyEventStreamAsync(HttpContext context, CancellationToken sessionToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        using var peer = new SsePeer(linked.Token);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        // Register before flushing headers so a client can immediately trigger an event
        // after observing HTTP 200 without missing its first delivery.
        _ssePeers[peer.Id] = peer;
        try
        {
            await context.Response.StartAsync(linked.Token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
            await peer.WriteAsync(context.Response.Body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested || peer.IsStopped)
        {
        }
        catch (IOException) when (linked.IsCancellationRequested || peer.IsStopped)
        {
        }
        catch (Exception error)
        {
            OutboundDeliveryFailed?.Invoke(this, error);
        }
        finally
        {
            _ssePeers.TryRemove(peer.Id, out _);
            if (peer.BacklogExceeded)
            {
                OutboundDeliveryFailed?.Invoke(this, new IOException(
                    "A Milky SSE client could not keep up with event delivery and was disconnected."));
            }
        }
    }

    private bool IsScopedDownloadAuthorized(HttpRequest request, string path)
    {
        if (_assets is null || !HttpMethods.IsGet(request.Method)
            || !request.Query.TryGetValue(AssetStore.DownloadTokenQueryParameter, out var grants)
            || grants.Count != 1) return false;
        return path.StartsWith("/assets/", StringComparison.Ordinal)
            ? _assets.ValidateAssetDownloadToken(path[8..], grants[0])
            : path.StartsWith("/files/", StringComparison.Ordinal)
                && _assets.ValidateSharedFileDownloadToken(path[7..], _implementation.SelfId, grants[0]);
    }

    private async Task HandleAssetRequestAsync(HttpContext context, string assetId, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || _assets is null || !_assets.Exists(assetId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        try
        {
            await using var stream = await _assets.OpenReadAsync(assetId, linked.Token).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = stream.Length;
            await stream.CopyToAsync(context.Response.Body, 64 * 1024, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (!context.Response.HasStarted && error is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task HandleSharedFileRequestAsync(HttpContext context, string fileId, CancellationToken sessionToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || _assets is null || string.IsNullOrWhiteSpace(fileId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        try
        {
            var file = await _platform.GetSharedFileForDownloadAsync(fileId, _implementation.SelfId, cancellation.Token)
                .ConfigureAwait(false);
            await using var stream = await _assets.OpenReadAsync(file.Asset.Id, cancellation.Token).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = stream.Length;
            await stream.CopyToAsync(context.Response.Body, 64 * 1024, cancellation.Token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellation.Token).ConfigureAwait(false);
            if (file.GroupId is { } groupId)
            {
                await _platform.RecordGroupFileDownloadAsync(groupId, file.Id, file.Asset.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error) when (!context.Response.HasStarted
            && error is PlatformException or FileNotFoundException or DirectoryNotFoundException or ArgumentException)
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
                await RunBidirectionalPeerAsync(peer, cancellationToken, cancellationToken).ConfigureAwait(false);
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

    private async Task RunBidirectionalPeerAsync(SocketPeer peer, CancellationToken cancellationToken, CancellationToken sessionToken)
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

                await HandleOneBotFrameAsync(peer, text, cancellationToken, sessionToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        CancellationToken sessionToken)
    {
        if (_implementation is OneBotProtocol { Version: OneBotVersion.V12 })
        {
            await HandleOneBotV12FrameAsync(peer, text, cancellationToken).ConfigureAwait(false);
            return;
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        var actionToken = _implementation is OneBotProtocol { Version: OneBotVersion.V11 }
            && IsV11DeferredAction(action) ? sessionToken : cancellationToken;
        using var restartResponse = (_implementation as OneBotProtocol)?.BeginRestartResponse();
        var reply = await _implementation.HandleAsync(
            new ProtocolCall(action, (JsonObject)parameters.DeepClone(), echo),
            actionToken).ConfigureAwait(false);
        var envelope = _implementation.CreateEnvelope(reply, echo);
        ObserveTraffic(new TrafficEntry(TrafficDirection.Reply, $"{action} → {reply.RetCode}", envelope));
        await SendJsonAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleOneBotV12FrameAsync(SocketPeer peer, string text, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(text); }
        catch (JsonException) { parsed = null; }
        var failure = OneBotV12ActionRequest.Validate(parsed, out var call);
        var payload = parsed as JsonObject;
        var echo = call?.Echo ?? (payload is null ? null : ParseV12Echo(payload));
        var action = call?.Name ?? "Invalid action request";
        ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, call is null ? new JsonObject() : payload!));
        var reply = failure ?? ValidateV12Self(payload!)
            ?? await _implementation.HandleAsync(call!, cancellationToken).ConfigureAwait(false);
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
                    if (_settings.Transport == TransportMode.OneBotHttpServer)
                        BufferOneBotHttpEvent(frame);
                    BufferOneBotWebhookEvent(frame);

                    ObserveTraffic(new TrafficEntry(
                        TrafficDirection.OutboundEvent,
                        Summarize(frame),
                        frame.Payload));

                    var peerSends = _peers.Values.Select(
                        peer => SendAsync(peer, frame, observe: false, cancellationToken));
                    await Task.WhenAll(peerSends).ConfigureAwait(false);

                    if (_settings.Transport == TransportMode.MilkyService)
                    {
                        if (!_ssePeers.IsEmpty)
                        {
                            // Compact JSON escapes embedded newlines, preserving the SSE
                            // record boundary and the protocol's fixed event name.
                            var eventBytes = Encoding.UTF8.GetBytes($"event: milky_event\ndata: {frame.Payload.ToCompactJson()}\n\n");
                            foreach (var peer in _ssePeers.Values)
                            {
                                if (!peer.TryEnqueue(eventBytes))
                                {
                                    _ssePeers.TryRemove(peer.Id, out _);
                                }
                            }
                        }
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

                var hasWebhooks = _implementation is OneBotProtocol && _settings.OneBotWebhookUrls.Count > 0;
                if (hasWebhooks)
                {
                    BufferOneBotWebhookEvent(frame);
                    ObserveTraffic(new TrafficEntry(TrafficDirection.OutboundEvent, Summarize(frame), frame.Payload));
                }
                var sends = _peers.Values.Select(peer => SendAsync(peer, frame, observe: !hasWebhooks, cancellationToken));
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

    private void ObserveTraffic(TrafficEntry entry) => TrafficObserved?.Invoke(this, ProtocolTrafficRedaction.Redact(entry));

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
        if (_implementation is OneBotProtocol oneBot) oneBot.ScheduledActionFailed -= OnScheduledActionFailed;
        if (_implementation is IDisposable disposable) disposable.Dispose();
        _webhookClient.Dispose();
    }

    private sealed class SsePeer : IDisposable
    {
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
        private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();
        private readonly Channel<byte[]> _pending = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly object _sync = new();
        private long _pendingBytes;

        internal SsePeer(CancellationToken cancellationToken)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _token = _lifetime.Token;
        }

        internal Guid Id { get; } = Guid.NewGuid();

        internal bool IsStopped => _token.IsCancellationRequested;

        internal bool BacklogExceeded { get; private set; }

        internal bool TryEnqueue(byte[] bytes)
        {
            lock (_sync)
            {
                if (IsStopped)
                {
                    return false;
                }
                // Bound both item count and bytes; a stalled client must not retain an
                // unbounded event history or stall other subscribers and WebHooks.
                if (Interlocked.Add(ref _pendingBytes, bytes.Length) <= MaxPayloadBytes
                    && _pending.Writer.TryWrite(bytes))
                {
                    return true;
                }

                Interlocked.Add(ref _pendingBytes, -bytes.Length);
                BacklogExceeded = true;
                _pending.Writer.TryComplete();
                _lifetime.Cancel();
                return false;
            }
        }

        internal async Task WriteAsync(Stream output)
        {
            var token = _token;
            using var timer = new PeriodicTimer(KeepAliveInterval);
            var nextEvent = _pending.Reader.WaitToReadAsync(token).AsTask();
            var nextKeepAlive = timer.WaitForNextTickAsync(token).AsTask();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.WhenAny(nextEvent, nextKeepAlive).ConfigureAwait(false);
                    if (nextEvent.IsCompleted)
                    {
                        if (!await nextEvent.ConfigureAwait(false))
                        {
                            return;
                        }
                        while (_pending.Reader.TryRead(out var bytes))
                        {
                            try
                            {
                                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                                await output.FlushAsync(token).ConfigureAwait(false);
                            }
                            finally
                            {
                                Interlocked.Add(ref _pendingBytes, -bytes.Length);
                            }
                        }
                        nextEvent = _pending.Reader.WaitToReadAsync(token).AsTask();
                    }
                    if (nextKeepAlive.IsCompleted)
                    {
                        if (!await nextKeepAlive.ConfigureAwait(false))
                        {
                            return;
                        }
                        // Milky defines no heartbeat event. SSE comments keep an idle
                        // connection alive without inventing an Event payload.
                        await output.WriteAsync(KeepAlive, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                        nextKeepAlive = timer.WaitForNextTickAsync(token).AsTask();
                    }
                }
            }
            finally
            {
                _lifetime.Cancel();
                await IgnoreCancellationAsync([nextEvent, nextKeepAlive]).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _pending.Writer.TryComplete();
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
        }
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
