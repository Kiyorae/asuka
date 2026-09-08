using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotHeartbeatSessionTests
{
    [TestMethod]
    [DataRow(OneBotVersion.V11)]
    [DataRow(OneBotVersion.V12)]
    public async Task EnabledHeartbeatUsesConfiguredIntervalAfterWebSocketHandshake(OneBotVersion version)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(version, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
        {
            HeartbeatEnabled = true,
            HeartbeatIntervalMilliseconds = 100,
        };
        var settings = new ConnectionSettings
        {
            Port = ProtocolTestFixture.ReserveEphemeralPort(),
            AccessToken = "heartbeat-fixture-token",
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer heartbeat-fixture-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(settings.BuildWebSocketUri(), timeout.Token);
        var first = await ReadAsync(socket, timeout.Token);
        Assert.AreEqual(version == OneBotVersion.V11 ? "lifecycle" : "connect",
            first[version == OneBotVersion.V11 ? "meta_event_type" : "detail_type"]!.GetValue<string>());
        if (version == OneBotVersion.V12)
            Assert.AreEqual("status_update", (await ReadAsync(socket, timeout.Token))["detail_type"]!.GetValue<string>());
        var heartbeat = await ReadAsync(socket, timeout.Token);
        Assert.AreEqual("heartbeat", heartbeat[version == OneBotVersion.V11 ? "meta_event_type" : "detail_type"]!.GetValue<string>());
        Assert.AreEqual(100, heartbeat["interval"]!.GetValue<int>());
        Assert.AreEqual(version == OneBotVersion.V11, heartbeat.ContainsKey("status"));
        await session.StopAsync().WaitAsync(timeout.Token);
    }

    [TestMethod]
    public async Task HeartbeatsDefaultToDisabledAndRejectNonpositiveIntervals()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        Assert.IsFalse(protocol.HeartbeatEnabled);
        Assert.IsNull(protocol.HeartbeatInterval);
        Assert.AreEqual(15000, protocol.HeartbeatIntervalMilliseconds);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            using var invalid = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
            {
                HeartbeatIntervalMilliseconds = 0,
            };
        });
    }

    private static async Task<JsonObject> ReadAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Assert.AreEqual(WebSocketMessageType.Text, received.MessageType);
            await body.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken);
        } while (!received.EndOfMessage);
        return JsonNode.Parse(body.ToArray())!.AsObject();
    }
}
