using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotSchedulingSessionTests
{
    private const string AccessToken = "onebot-scheduling-session-test";

    [TestMethod]
    public async Task StopCancelsTheQueuedRateLimitedCallAndRestartCreatesAFreshQueue()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
        {
            RateLimitInterval = TimeSpan.FromSeconds(10),
        };
        var settings = Settings();
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        using (var socket = await ConnectAsync(settings.Port))
        {
            // The entire first-execution/second-enqueue/stop phase has a deadline
            // shorter than the rate-limit interval. No sleep or timer race is needed.
            using var phase = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstEcho = new JsonObject { ["request"] = "first", ["sequence"] = 1 };
            await SendAsync(socket, Rename("set_group_name_rate_limited", "First committed name", firstEcho), phase.Token);
            AssertAccepted(await ReceiveAsync(socket, phase.Token), firstEcho);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
            Assert.AreEqual("First committed name", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
            await StatusBarrierAsync(socket, "after-first", phase.Token);

            var secondEcho = new JsonObject { ["request"] = "cancel-on-stop", ["sequence"] = 2 };
            await SendAsync(socket, Rename("set_group_name_rate_limited", "Old queue must never commit", secondEcho), phase.Token);
            AssertAccepted(await ReceiveAsync(socket, phase.Token), secondEcho);
            await session.StopAsync().WaitAsync(phase.Token);
            // This also proves Stop joined/drained the delayed worker, rather than
            // leaving it asleep until its ten-second interval expires.
            await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
            Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
            Assert.AreEqual("First committed name", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
        }

        await session.StartAsync();
        using (var socket = await ConnectAsync(settings.Port))
        {
            using var phase = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var restartedEcho = new JsonObject { ["request"] = "after-restart", ["sequence"] = 3 };
            await SendAsync(socket, Rename("set_group_name_rate_limited", "Fresh queue name", restartedEcho), phase.Token);
            AssertAccepted(await ReceiveAsync(socket, phase.Token), restartedEcho);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
            Assert.AreEqual("Fresh queue name", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
            await StatusBarrierAsync(socket, "after-restart", phase.Token);
            await session.StopAsync().WaitAsync(phase.Token);
            await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
            Assert.AreEqual("Fresh queue name", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
        }
    }

    [TestMethod]
    public async Task DeferredFailureIsDiagnosticOnlyAndDoesNotProduceASecondWireResponse()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var settings = Settings();
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new ConcurrentQueue<TrafficEntry>();
        session.DeferredActionFailed += (_, error) => failure.TrySetResult(error);
        session.TrafficObserved += (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.Reply) replies.Enqueue(entry);
        };
        await session.StartAsync();
        using var socket = await ConnectAsync(settings.Port);
        using var phase = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var echo = new JsonObject { ["private_echo"] = "do-not-log-this-echo" };
        var request = Rename("set_group_name_async", "do-not-log-this-group-name", echo);
        request["params"]!["group_id"] = 999_000_001L;
        await SendAsync(socket, request, phase.Token);
        AssertAccepted(await ReceiveAsync(socket, phase.Token), echo);
        var diagnostic = await failure.Task.WaitAsync(phase.Token);
        await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
        Assert.Contains("set_group_name", diagnostic.Message);
        Assert.Contains("1404", diagnostic.Message);
        Assert.DoesNotContain("do-not-log-this-echo", diagnostic.Message);
        Assert.DoesNotContain("do-not-log-this-group-name", diagnostic.Message);

        // This reads the next frame without filtering unexpected replies away.
        // A delayed success/failure response for the first request fails its echo assertion.
        await StatusBarrierAsync(socket, "after-deferred-failure", phase.Token);
        Assert.HasCount(2, replies);
        Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcknowledgedQueuedActionsSurviveWebSocketDisconnectUntilSessionStop(bool abortConnection)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var assets = new GatedAssetResolver(fixture.Media);
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, assets)
        {
            // A blocked first action, rather than wall-clock delay, holds the queue.
            RateLimitInterval = TimeSpan.Zero,
        };
        var settings = Settings();
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        using var socket = await ConnectAsync(settings.Port);
        using var phase = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, state) =>
        {
            if (state.Kind == SessionStateKind.Listening) disconnected.TrySetResult();
        };

        var firstEcho = new JsonObject { ["barrier"] = "blocked-first-action" };
        await SendAsync(socket, new JsonObject
        {
            ["action"] = "send_group_msg_rate_limited",
            ["params"] = new JsonObject
            {
                ["group_id"] = 500_000_001L,
                ["message"] = new JsonArray(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = new JsonObject
                    {
                        ["file"] = "base64://iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jxssAAAAASUVORK5CYII=",
                    },
                }),
            },
            ["echo"] = firstEcho.DeepClone(),
        }, phase.Token);
        AssertAccepted(await ReceiveAsync(socket, phase.Token), firstEcho);
        await assets.Entered.Task.WaitAsync(phase.Token);

        var secondEcho = new JsonObject { ["barrier"] = "accepted-before-disconnect" };
        await SendAsync(socket, Rename("set_group_name_rate_limited", "Committed after client disconnect", secondEcho), phase.Token);
        AssertAccepted(await ReceiveAsync(socket, phase.Token), secondEcho);
        Assert.IsFalse(protocol.WaitForScheduledActionsAsync().IsCompleted);
        Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);

        if (abortConnection)
        {
            socket.Abort();
        }
        else
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnect after acknowledgement", phase.Token);
        }

        // The server has left the peer loop before either queued action may finish.
        // An old connection-linked scheduler token would already be cancelled here.
        await disconnected.Task.WaitAsync(phase.Token);
        assets.Release();
        await protocol.WaitForScheduledActionsAsync().WaitAsync(phase.Token);
        Assert.AreEqual(SessionStateKind.Listening, session.State.Kind);
        Assert.AreEqual("Committed after client disconnect", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId, phase.Token))!.Name);
        await session.StopAsync().WaitAsync(phase.Token);
        Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
    }

    private sealed class GatedAssetResolver(IProtocolAssetResolver inner) : IProtocolAssetResolver
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public Task<ProtocolAssetReference> GetReferenceAsync(Asset asset, bool preferLocalPath, CancellationToken cancellationToken = default) =>
            inner.GetReferenceAsync(asset, preferLocalPath, cancellationToken);

        public Task<Asset?> ResolveIdAsync(string identifier, ProtocolAssetKind kind, CancellationToken cancellationToken = default) =>
            inner.ResolveIdAsync(identifier, kind, cancellationToken);

        public async Task<Asset?> ResolveReferenceAsync(string reference, string? fallbackUrl, ProtocolAssetKind kind,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ResolveReferenceAsync(reference, fallbackUrl, kind, cancellationToken);
        }
    }

    private static ConnectionSettings Settings() => new()
    {
        Host = "127.0.0.1",
        Port = ProtocolTestFixture.ReserveEphemeralPort(),
        Transport = TransportMode.WebSocketServer,
        AccessToken = AccessToken,
        PostSelfEvents = false,
    };

    private static async Task<ClientWebSocket> ConnectAsync(ushort port)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {AccessToken}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
            var handshake = await ReceiveAsync(socket, timeout.Token);
            Assert.AreEqual("meta_event", handshake["post_type"]!.GetValue<string>());
            Assert.AreEqual("lifecycle", handshake["meta_event_type"]!.GetValue<string>());
            Assert.AreEqual("connect", handshake["sub_type"]!.GetValue<string>());
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static JsonObject Rename(string action, string name, JsonNode echo) => new()
    {
        ["action"] = action,
        ["params"] = new JsonObject { ["group_id"] = 500_000_001L, ["group_name"] = name },
        ["echo"] = echo.DeepClone(),
    };

    private static void AssertAccepted(JsonObject reply, JsonNode echo)
    {
        Assert.AreEqual("async", reply["status"]!.GetValue<string>());
        Assert.AreEqual(1, reply["retcode"]!.GetValue<int>());
        Assert.IsTrue(reply.ContainsKey("data"));
        Assert.IsNull(reply["data"]);
        Assert.IsTrue(JsonNode.DeepEquals(echo, reply["echo"]));
    }

    private static async Task StatusBarrierAsync(ClientWebSocket socket, string label, CancellationToken cancellationToken)
    {
        var echo = new JsonObject { ["barrier"] = label };
        await SendAsync(socket, new JsonObject
        {
            ["action"] = "get_status",
            ["params"] = new JsonObject(),
            ["echo"] = echo.DeepClone(),
        }, cancellationToken);
        var reply = await ReceiveAsync(socket, cancellationToken);
        Assert.IsTrue(JsonNode.DeepEquals(echo, reply["echo"]), "Every request must produce exactly one response.");
        Assert.AreEqual("ok", reply["status"]!.GetValue<string>());
        Assert.AreEqual(0, reply["retcode"]!.GetValue<int>());
        Assert.IsTrue(reply["data"]!["good"]!.GetValue<bool>());
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonObject payload, CancellationToken cancellationToken) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()).AsMemory(), WebSocketMessageType.Text,
            endOfMessage: true, cancellationToken);

    private static async Task<JsonObject> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Assert.AreEqual(WebSocketMessageType.Text, frame.MessageType);
            await content.WriteAsync(buffer.AsMemory(0, frame.Count), cancellationToken);
        } while (!frame.EndOfMessage);
        return JsonNode.Parse(content.ToArray())!.AsObject();
    }
}
