using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotRestartTests
{
    private const string Token = "restart-contract-token";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // onebot-11/api/public.md: set_restart takes a millisecond delay, default 0,
    // and returns async status with no result data because its API server restarts.
    [TestMethod]
    public async Task RestartRequiresRunningV11SessionAndValidNonnegativeDelay()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = Protocol(fixture, OneBotVersion.V12);
        Assert.AreEqual(1000, (await v11.HandleAsync(new ProtocolCall("set_restart", new JsonObject()))).RetCode);
        foreach (var action in new[] { "set_restart", "set_restart_async", "set_restart_rate_limited" })
            Assert.AreEqual(10002, (await v12.HandleAsync(new ProtocolCall(action, new JsonObject()))).RetCode);
        foreach (var invalid in new JsonNode?[] { null, -1, true, "not a number", new JsonObject(), new JsonArray(), "1e1000" })
            Assert.AreEqual(1400, (await v11.HandleAsync(new ProtocolCall("set_restart", new JsonObject { ["delay"] = invalid?.DeepClone() }))).RetCode);
    }

    [TestMethod]
    [DataRow("set_restart")]
    [DataRow("set_restart_async")]
    [DataRow("set_restart_rate_limited")]
    public async Task HttpRestartAcknowledgesAsyncAndRestartsSameListenerWithoutLosingMessages(string action)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var stored = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("survives restart")]);
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        var restarted = Signal();
        var ready = 0;
        session.StateChanged += (_, state) => { if (state.Kind == SessionStateKind.Ready && Interlocked.Increment(ref ready) == 2) restarted.TrySetResult(); };
        await session.StartAsync();
        using var http = Client(port);
        using var response = await http.GetAsync(action);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var ack = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("async", ack["status"]!.GetValue<string>());
        Assert.AreEqual(1, ack["retcode"]!.GetValue<int>());
        Assert.IsNull(ack["data"]);
        await restarted.Task.WaitAsync(Timeout);
        await session.WaitForPendingRestartAsync().WaitAsync(Timeout);
        using var status = await http.GetAsync("get_status");
        Assert.AreEqual(0, JsonNode.Parse(await status.Content.ReadAsStringAsync())!["retcode"]!.GetValue<int>());
        Assert.AreEqual("survives restart", (await fixture.Store.GetMessageAsync(stored.Id))!.Content.PlainText());
        Assert.IsNotNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
    }

    [TestMethod]
    public async Task ZeroDelayWebSocketRestartSendsEchoBeforeClosingAndAcceptsNewConnection()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform,
            Settings(port) with { Transport = TransportMode.WebSocketServer }, fixture.Assets);
        var listening = 0;
        var restarted = Signal();
        session.StateChanged += (_, state) => { if (state.Kind == SessionStateKind.Listening && Interlocked.Increment(ref listening) == 2) restarted.TrySetResult(); };
        await session.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/onebot/v11/ws"), CancellationToken.None);
        Assert.AreEqual("connect", (await ReceiveAsync(socket))["sub_type"]!.GetValue<string>());
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"action\":\"set_restart\",\"params\":{},\"echo\":\"restart-echo\"}"),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var ack = await ReceiveAsync(socket);
        Assert.AreEqual("async", ack["status"]!.GetValue<string>());
        Assert.AreEqual("restart-echo", ack["echo"]!.GetValue<string>());
        Assert.IsNull(ack["data"]);
        await restarted.Task.WaitAsync(Timeout);
        await session.WaitForPendingRestartAsync().WaitAsync(Timeout);
        using var next = new ClientWebSocket();
        next.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await next.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/onebot/v11/ws"), CancellationToken.None);
        Assert.AreEqual("connect", (await ReceiveAsync(next))["sub_type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConcurrentRestartsCoalesceAndHonorFirstDelayAndResponseBarrier()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        var ready = 0;
        session.StateChanged += (_, state) => { if (state.Kind == SessionStateKind.Ready) Interlocked.Increment(ref ready); };
        await session.StartAsync();
        var clock = Stopwatch.StartNew();
        Task restart;
        using (protocol.BeginRestartResponse())
        {
            Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart", new JsonObject { ["delay"] = 120 }))).RetCode);
            restart = session.WaitForPendingRestartAsync();
            Assert.IsTrue(session.RequestRestart(TimeSpan.Zero));
            Assert.AreSame(restart, session.WaitForPendingRestartAsync());
            Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart", new JsonObject()))).RetCode);
            Assert.IsFalse(restart.IsCompleted);
            Assert.AreEqual(1, Volatile.Read(ref ready));
        }
        await restart.WaitAsync(Timeout);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(110));
        Assert.AreEqual(2, Volatile.Read(ref ready));
    }

    [TestMethod]
    [DataRow("_async")]
    [DataRow("_rate_limited")]
    public async Task ScheduledRestartRetainsOriginalReplyBarrierWithoutWaitingForItsOwnQueue(string suffix)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        await session.StartAsync();
        Task restart;
        using (protocol.BeginRestartResponse())
        {
            Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart" + suffix, new JsonObject()))).RetCode);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(Timeout);
            restart = session.WaitForPendingRestartAsync();
            Assert.IsFalse(restart.IsCompleted);
            Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
        }
        await restart.WaitAsync(Timeout);
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
    }

    [TestMethod]
    public async Task RateLimitedRestartWaitsForBlockedPredecessorAndConfiguredInterval()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
        {
            RateLimitInterval = TimeSpan.FromMilliseconds(120),
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        await session.StartAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        var entered = Signal();
        var release = Signal();
        var held = 0;
        void BlockGroupChange(object? sender, StoreChangedEventArgs change)
        {
            if (Interlocked.Exchange(ref held, 1) != 0) return;
            entered.TrySetResult();
            release.Task.Wait(timeout.Token);
        }
        fixture.Store.Changed += BlockGroupChange;
        try
        {
            Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_group_name_rate_limited", new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["group_name"] = "before restart",
            }))).RetCode);
            await entered.Task.WaitAsync(timeout.Token);
            using (protocol.BeginRestartResponse())
            {
                Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart_rate_limited", new JsonObject()))).RetCode);
                Assert.IsTrue(session.WaitForPendingRestartAsync().IsCompleted);
                Assert.IsFalse(protocol.WaitForScheduledActionsAsync().IsCompleted);
                var interval = Stopwatch.StartNew();
                release.TrySetResult();
                await protocol.WaitForScheduledActionsAsync().WaitAsync(timeout.Token);
                Assert.IsTrue(interval.Elapsed >= TimeSpan.FromMilliseconds(110));
                Assert.IsFalse(session.WaitForPendingRestartAsync().IsCompleted);
            }
            await session.WaitForPendingRestartAsync().WaitAsync(timeout.Token);
            Assert.AreEqual("before restart", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);
            Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
        }
        finally
        {
            release.TrySetResult();
            fixture.Store.Changed -= BlockGroupChange;
        }
    }

    [TestMethod]
    public async Task ManualStopCancelsLongDelayAndOldGenerationCannotRestartAfterManualStart()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        Assert.IsFalse(session.RequestRestart());
        await session.StartAsync();
        Assert.IsTrue(session.RequestRestart(TimeSpan.FromDays(60)));
        var pending = session.WaitForPendingRestartAsync();
        Assert.IsFalse(pending.IsCompleted);
        await session.StopAsync().WaitAsync(Timeout);
        Assert.IsTrue(pending.IsCompletedSuccessfully);
        Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
        Assert.IsFalse(session.RequestRestart());
        Assert.AreEqual(1000, (await protocol.HandleAsync(new ProtocolCall("set_restart", new JsonObject()))).RetCode);
        await session.StartAsync();
        Assert.IsTrue(session.WaitForPendingRestartAsync().IsCompletedSuccessfully);
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
    }

    [TestMethod]
    public async Task DisposeCancelsPendingResponseBarrierWithoutResurrection()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        await session.StartAsync();
        using (protocol.BeginRestartResponse())
        {
            Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart", new JsonObject()))).RetCode);
            var pending = session.WaitForPendingRestartAsync();
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
            Assert.IsTrue(pending.IsCompletedSuccessfully);
            Assert.IsFalse(session.RequestRestart());
            Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
    }

    [TestMethod]
    public async Task StopCancellationAfterShutdownBeginsCannotLeaveConcurrentStartWithoutRestartHandler()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        var entered = Signal();
        var release = Signal();
        using var timeout = new CancellationTokenSource(Timeout);
        using var stopCancellation = new CancellationTokenSource();
        void HoldStart(object? sender, SessionState state)
        {
            if (state.Kind != SessionStateKind.Ready) return;
            entered.TrySetResult();
            release.Task.Wait(timeout.Token);
        }
        session.StateChanged += HoldStart;
        var start = Task.Run(() => session.StartAsync(timeout.Token));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var stop = session.StopAsync(stopCancellation.Token);
            Assert.IsFalse(stop.IsCompleted);
            stopCancellation.Cancel();
            release.TrySetResult();
            await start.WaitAsync(timeout.Token);
            await stop.WaitAsync(timeout.Token);
            Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
            Assert.IsFalse(session.RequestRestart());
        }
        finally
        {
            release.TrySetResult();
            session.StateChanged -= HoldStart;
            await start.WaitAsync(Timeout);
        }
        await session.StartAsync();
        Assert.IsTrue(session.RequestRestart(TimeSpan.FromDays(1)));
        await session.StopAsync().WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task RestartRemainsAvailableWhileBotAccountIsOffline()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(ProtocolTestFixture.ReserveEphemeralPort()), fixture.Assets);
        await session.StartAsync();
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false);
        Assert.AreEqual(1, (await protocol.HandleAsync(new ProtocolCall("set_restart", new JsonObject { ["delay"] = "0.5" }))).RetCode);
        await session.WaitForPendingRestartAsync().WaitAsync(Timeout);
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
        Assert.IsFalse(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
    }

    [TestMethod]
    public async Task RestartFailureReportsFixedDiagnosticAndLeavesRecoverableStoppedSession()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        var listener = new TcpListener(IPAddress.Loopback, port);
        var occupied = false;
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DeferredActionFailed += (_, _) => throw new InvalidOperationException("observer failure");
        session.DeferredActionFailed += (_, error) => failure.TrySetResult(error);
        void OccupyStoppedPort(object? sender, SessionState state)
        {
            if (state.Kind == SessionStateKind.Idle && !occupied)
            {
                listener.Start();
                occupied = true;
            }
        }
        session.StateChanged += OccupyStoppedPort;
        try
        {
            Assert.IsTrue(session.RequestRestart());
            var diagnostic = await failure.Task.WaitAsync(Timeout);
            await session.WaitForPendingRestartAsync().WaitAsync(Timeout);
            Assert.AreEqual("The deferred protocol restart failed.", diagnostic.Message);
            Assert.IsNull(diagnostic.InnerException);
            Assert.DoesNotContain(Token, diagnostic.ToString());
            Assert.DoesNotContain("127.0.0.1", diagnostic.ToString());
            Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
        }
        finally
        {
            session.StateChanged -= OccupyStoppedPort;
            listener.Stop();
        }
        await session.StartAsync();
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
    }

    [TestMethod]
    public async Task RestartProducesWebhookDisableEnableAndResumesEventDelivery()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var webhook = new LifecycleProbe();
        var protocol = Protocol(fixture);
        await using var session = new ProtocolSession(protocol, fixture.Platform,
            Settings(ProtocolTestFixture.ReserveEphemeralPort()) with { OneBotWebhookUrls = [webhook.Endpoint] }, fixture.Assets);
        await session.StartAsync();
        Assert.AreEqual("enable", (await webhook.NextAsync())["sub_type"]!.GetValue<string>());
        // Delivery is FIFO; seeing a subsequent message proves startup was
        // acknowledged and therefore requires the disable lifecycle on restart.
        await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("before")]);
        Assert.AreEqual("message", (await webhook.NextAsync())["post_type"]!.GetValue<string>());
        Assert.IsTrue(session.RequestRestart());
        await session.WaitForPendingRestartAsync().WaitAsync(Timeout);
        Assert.AreEqual("disable", (await webhook.NextAsync())["sub_type"]!.GetValue<string>());
        Assert.AreEqual("enable", (await webhook.NextAsync())["sub_type"]!.GetValue<string>());
        await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("after")]);
        Assert.AreEqual("after", (await webhook.NextAsync())["raw_message"]!.GetValue<string>());
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture, OneBotVersion version = OneBotVersion.V11) =>
        new(version, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static ConnectionSettings Settings(ushort port) => new()
    {
        Transport = TransportMode.OneBotHttpServer,
        Host = "127.0.0.1",
        Port = port,
        AccessToken = Token,
    };

    private static HttpClient Client(ushort port)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<JsonObject> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(output.ToArray())!.AsObject();
    }

    private sealed class LifecycleProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<JsonObject> _requests = Channel.CreateUnbounded<JsonObject>();
        private readonly Task _worker;
        internal LifecycleProbe()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _worker = RunAsync();
        }
        internal string Endpoint { get; }
        internal async Task<JsonObject> NextAsync() => await _requests.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        private async Task RunAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    await using var stream = client.GetStream();
                    var header = new List<byte>();
                    var single = new byte[1];
                    while (header.Count < 65536)
                    {
                        await stream.ReadExactlyAsync(single, _lifetime.Token);
                        header.Add(single[0]);
                        if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
                    }
                    var contentLength = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var body = new byte[int.Parse(contentLength.AsSpan("Content-Length:".Length).Trim(), CultureInfo.InvariantCulture)];
                    await stream.ReadExactlyAsync(body, _lifetime.Token);
                    await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                    _requests.Writer.TryWrite(JsonNode.Parse(body)!.AsObject());
                }
            }
            catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            await _worker;
            _lifetime.Dispose();
        }
    }
}
