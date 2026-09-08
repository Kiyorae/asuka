using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Contracts: Milky /struct/Event#bot_offline; OneBot 11 api/public.md#get_status
// and event/meta.md; OneBot 12 /interface/meta/actions/ and /interface/meta/events/.
[TestClass]
public sealed class BotPresenceProtocolTests
{
    private static readonly DateTimeOffset EventTime = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_234);
    private static readonly string[] MilkyEventFields = ["time", "self_id", "event_type", "data"];

    [TestMethod]
    public async Task RegistrationStartsOnlineAndPresenceChangesAreAccountScoped()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        fixture.Platform.RegisterBot(ProtocolTestFixture.SenderId);
        Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
        var registered = fixture.Platform.GetBotPresence(ProtocolTestFixture.SelfId);
        Assert.IsNotNull(registered);
        Assert.IsTrue(registered.IsOnline);

        Assert.IsTrue(await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false, "连接已断开"));
        var offline = fixture.Platform.GetBotPresence(ProtocolTestFixture.SelfId);
        Assert.IsNotNull(offline);
        Assert.AreEqual(ProtocolTestFixture.SelfId, offline.SelfId);
        Assert.IsFalse(offline.IsOnline);
        Assert.AreEqual("连接已断开", offline.Reason);
        Assert.IsFalse(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
        Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SenderId));

        Assert.IsTrue(await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, true));
        Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
        Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SenderId));
    }

    [TestMethod]
    public async Task MilkyOfflineUsesTheEventReasonAndOnlineHasNoInventedEvent()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        // The live account is online: encoding an earlier offline event must use its snapshot.
        const string reason = "连接断开：请重新登录\n原始原因";
        var offline = PresenceEvent(isOnline: false, reason);
        var frames = await protocol.EncodeAsync(offline);
        Assert.HasCount(1, frames);
        Assert.AreEqual("milky_event", frames[0].EventName);
        var wire = frames[0].Payload;
        CollectionAssert.AreEquivalent(MilkyEventFields, wire.Select(item => item.Key).ToArray());
        Assert.AreEqual(EventTime.ToUnixTimeSeconds(), wire["time"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, wire["self_id"]!.GetValue<long>());
        Assert.AreEqual("bot_offline", wire["event_type"]!.GetValue<string>());
        Assert.HasCount(1, wire["data"]!.AsObject());
        Assert.AreEqual(reason, wire["data"]!["reason"]!.GetValue<string>());
        Assert.IsEmpty(await protocol.EncodeAsync(PresenceEvent(isOnline: true)));
        Assert.IsEmpty(await protocol.EncodeAsync(offline with { SelfId = ProtocolTestFixture.SenderId }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task V11StatusAndHeartbeatFollowPresenceWithoutAStateChangeEvent(bool isOnline)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, isOnline, "状态测试");
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var status = await protocol.HandleAsync(new ProtocolCall("get_status", new JsonObject()));
        Assert.IsTrue(status.IsSuccess, status.Message);
        AssertV11Status(status.Data!, isOnline);

        var heartbeat = await protocol.GetHeartbeatFrameAsync();
        Assert.IsNotNull(heartbeat);
        Assert.AreEqual("meta_event", heartbeat.Payload["post_type"]!.GetValue<string>());
        Assert.AreEqual("heartbeat", heartbeat.Payload["meta_event_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_001L, heartbeat.Payload["self_id"]!.GetValue<long>());
        Assert.AreEqual(15_000, heartbeat.Payload["interval"]!.GetValue<int>());
        AssertV11Status(heartbeat.Payload["status"]!, isOnline);
        Assert.IsEmpty(await protocol.EncodeAsync(PresenceEvent(isOnline)));
        Assert.IsEmpty(await protocol.EncodeAsync(PresenceEvent(isOnline) with { SelfId = ProtocolTestFixture.SenderId }));
        var handshake = await protocol.GetHandshakeFramesAsync();
        Assert.HasCount(1, handshake);
        Assert.AreEqual("lifecycle", handshake[0].Payload["meta_event_type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task V12StatusAndHandshakeContainOnlyThisConnectionAndHeartbeatHasNoStatus(bool isOnline)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        fixture.Platform.RegisterBot(ProtocolTestFixture.SenderId);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, isOnline);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SenderId, !isOnline);
        using var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var status = await protocol.HandleAsync(new ProtocolCall("get_status", new JsonObject()));
        Assert.IsTrue(status.IsSuccess, status.Message);
        AssertV12Status(status.Data!, isOnline);

        var handshake = await protocol.GetHandshakeFramesAsync();
        Assert.HasCount(2, handshake);
        Assert.AreEqual("connect", handshake[0].Payload["detail_type"]!.GetValue<string>());
        Assert.AreEqual("status_update", handshake[1].Payload["detail_type"]!.GetValue<string>());
        AssertV12Status(handshake[1].Payload["status"]!, isOnline);
        var heartbeat = await protocol.GetHeartbeatFrameAsync();
        Assert.IsNotNull(heartbeat);
        Assert.AreEqual("meta", heartbeat.Payload["type"]!.GetValue<string>());
        Assert.AreEqual("heartbeat", heartbeat.Payload["detail_type"]!.GetValue<string>());
        Assert.AreEqual(15_000, heartbeat.Payload["interval"]!.GetValue<int>());
        Assert.IsFalse(heartbeat.Payload.ContainsKey("status"));
        Assert.IsFalse(heartbeat.Payload.ContainsKey("online"));
        Assert.IsFalse(heartbeat.Payload.ContainsKey("bots"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task V12StatusUpdateUsesItsSnapshotEvenAfterTheLiveStateHasChanged(bool eventOnline)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        fixture.Platform.RegisterBot(ProtocolTestFixture.SenderId);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, !eventOnline);
        using var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var domainEvent = PresenceEvent(eventOnline, "Snapshot reason");
        var frames = await protocol.EncodeAsync(domainEvent);
        Assert.HasCount(1, frames);
        var wire = frames[0].Payload;
        Assert.AreEqual(domainEvent.Id, wire["id"]!.GetValue<string>());
        Assert.AreEqual(EventTime.ToUnixTimeMilliseconds() / 1000d, wire["time"]!.GetValue<double>());
        Assert.AreEqual("meta", wire["type"]!.GetValue<string>());
        Assert.AreEqual("status_update", wire["detail_type"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, wire["sub_type"]!.GetValue<string>());
        AssertV12Status(wire["status"]!, eventOnline);
        Assert.IsFalse(wire.ContainsKey("reason"));
        Assert.IsEmpty(await protocol.EncodeAsync(domainEvent with { SelfId = ProtocolTestFixture.SenderId }));
        var liveStatus = await protocol.HandleAsync(new ProtocolCall("get_status", new JsonObject()));
        AssertV12Status(liveStatus.Data!, !eventOnline);
    }

    [TestMethod]
    [DataRow(1, -500)]
    [DataRow(11, 1403)]
    [DataRow(12, 34000)]
    public async Task OfflineWritesLeaveDataIntactCachedReadsWorkAndRecoveryRestoresWrites(int protocolVersion, int offlineCode)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = CreateProtocol(protocolVersion, fixture);
        using var disposableProtocol = protocol as IDisposable;
        var initial = await protocol.HandleAsync(SendCall(protocolVersion, "Already cached"));
        Assert.IsTrue(initial.IsSuccess, initial.Message);
        var originalMessages = await fixture.Store.GetHistoryAsync(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        Assert.HasCount(1, originalMessages);

        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false, "Test disconnection");
        var send = await protocol.HandleAsync(SendCall(protocolVersion, "Must not be saved"));
        var rename = await protocol.HandleAsync(RenameCall(protocolVersion, "Must not be renamed"));
        Assert.AreEqual(offlineCode, send.RetCode, send.Message);
        Assert.AreEqual(offlineCode, rename.RetCode, rename.Message);
        var unchanged = await fixture.Store.GetHistoryAsync(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        Assert.HasCount(1, unchanged);
        Assert.AreEqual(originalMessages[0].Id, unchanged[0].Id);
        Assert.AreEqual("Already cached", unchanged[0].Content.PlainText());
        Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);

        foreach (var call in CachedReadCalls(protocolVersion, originalMessages[0]))
        {
            var reply = await protocol.HandleAsync(call);
            Assert.IsTrue(reply.IsSuccess, $"Offline cached read {call.Name}: {reply.Message}");
        }

        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, true);
        var recoveredSend = await protocol.HandleAsync(SendCall(protocolVersion, "Sent after recovery"));
        var recoveredRename = await protocol.HandleAsync(RenameCall(protocolVersion, "Renamed after recovery"));
        Assert.IsTrue(recoveredSend.IsSuccess, recoveredSend.Message);
        Assert.IsTrue(recoveredRename.IsSuccess, recoveredRename.Message);
        var recoveredMessages = await fixture.Store.GetHistoryAsync(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        Assert.HasCount(2, recoveredMessages);
        Assert.AreEqual("Sent after recovery", recoveredMessages[1].Content.PlainText());
        Assert.AreEqual(originalMessages[0].Seq + 1, recoveredMessages[1].Seq);
        Assert.AreEqual("Renamed after recovery", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);
    }

    [TestMethod]
    [DataRow(OneBotVersion.V11)]
    [DataRow(OneBotVersion.V12)]
    public async Task OfflineOneBotRejectsMediaSendBeforeResolvingAnAsset(OneBotVersion version)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var resolver = new CountingAssetResolver();
        using var protocol = new OneBotProtocol(version, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false);
        var parameters = SendCall(version == OneBotVersion.V11 ? 11 : 12, "placeholder").Parameters;
        parameters["message"] = new JsonArray(new JsonObject
        {
            ["type"] = "image",
            ["data"] = new JsonObject { [version == OneBotVersion.V11 ? "file" : "file_id"] = "cached-image" },
        });
        var reply = await protocol.HandleAsync(new ProtocolCall(version == OneBotVersion.V11 ? "send_group_msg" : "send_message", parameters));
        Assert.AreEqual(version == OneBotVersion.V11 ? 1403 : 34000, reply.RetCode, reply.Message);
        Assert.AreEqual(0, resolver.ResolveCount, "An offline write must be rejected before resource resolution/import.");
        Assert.IsEmpty(await fixture.Store.GetHistoryAsync(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId));
    }

    private static DomainEvent PresenceEvent(bool isOnline, string reason = "") => new(
        ProtocolTestFixture.SelfId,
        new BotPresenceChangedEvent(new BotPresence(ProtocolTestFixture.SelfId, isOnline, reason, EventTime)),
        id: "presence-contract-event", time: EventTime);

    private static void AssertV11Status(JsonNode status, bool isOnline)
    {
        Assert.AreEqual(isOnline, status["online"]!.GetValue<bool>());
        Assert.AreEqual(isOnline, status["good"]!.GetValue<bool>());
    }

    private static void AssertV12Status(JsonNode status, bool isOnline)
    {
        Assert.IsTrue(status["good"]!.GetValue<bool>(), "Implementation health is separate from bot connectivity.");
        var bots = status["bots"]!.AsArray();
        Assert.HasCount(1, bots);
        Assert.AreEqual(isOnline, bots[0]!["online"]!.GetValue<bool>());
        Assert.AreEqual(ProtocolTestFixture.SelfId, bots[0]!["self"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("asuka", bots[0]!["self"]!["platform"]!.GetValue<string>());
    }

    private static IProtocolImplementation CreateProtocol(int version, ProtocolTestFixture fixture) => version == 1
        ? new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
        : new OneBotProtocol(version == 11 ? OneBotVersion.V11 : OneBotVersion.V12,
            ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static ProtocolCall SendCall(int version, string text)
    {
        var parameters = new JsonObject
        {
            ["group_id"] = GroupId(version),
            ["message"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["data"] = new JsonObject { ["text"] = text },
            }),
        };
        if (version == 12) parameters["detail_type"] = "group";
        return new ProtocolCall(version == 1 ? "send_group_message" : version == 11 ? "send_group_msg" : "send_message", parameters);
    }

    private static System.Text.Json.Nodes.JsonValue GroupId(int version) => version == 12
        ? System.Text.Json.Nodes.JsonValue.Create(ProtocolTestFixture.GroupId)!
        : System.Text.Json.Nodes.JsonValue.Create(500_000_001L)!;

    private static ProtocolCall RenameCall(int version, string name) => new("set_group_name", new JsonObject
    {
        ["group_id"] = GroupId(version),
        [version == 1 ? "new_group_name" : "group_name"] = name,
    });

    private static IEnumerable<ProtocolCall> CachedReadCalls(int version, Message original)
    {
        yield return new ProtocolCall(version == 12 ? "get_self_info" : "get_login_info", new JsonObject());
        yield return new ProtocolCall(version == 1 ? "get_impl_info" : version == 11 ? "get_version_info" : "get_version", new JsonObject());
        yield return new ProtocolCall("get_group_list", new JsonObject());
        yield return new ProtocolCall("get_group_info", new JsonObject { ["group_id"] = GroupId(version) });
        yield return new ProtocolCall("get_friend_list", new JsonObject());
        if (version != 1) yield return new ProtocolCall("get_status", new JsonObject());
        if (version == 1)
        {
            yield return new ProtocolCall("get_message", new JsonObject
            {
                ["message_scene"] = "group",
                ["peer_id"] = ProtocolTestFixture.GroupId,
                ["message_seq"] = original.Seq,
            });
            yield return new ProtocolCall("get_history_messages", new JsonObject
            {
                ["message_scene"] = "group",
                ["peer_id"] = ProtocolTestFixture.GroupId,
                ["limit"] = 20,
            });
        }
        else if (version == 11)
        {
            yield return new ProtocolCall("get_msg", new JsonObject { ["message_id"] = original.Id });
        }
    }

    private sealed class CountingAssetResolver : IProtocolAssetResolver
    {
        private readonly StubProtocolAssetResolver _inner = new();
        internal int ResolveCount { get; private set; }

        public Task<ProtocolAssetReference> GetReferenceAsync(Asset asset, bool preferLocalPath, CancellationToken cancellationToken = default) =>
            _inner.GetReferenceAsync(asset, preferLocalPath, cancellationToken);

        public Task<Asset?> ResolveIdAsync(string identifier, ProtocolAssetKind kind, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return _inner.ResolveIdAsync(identifier, kind, cancellationToken);
        }

        public Task<Asset?> ResolveReferenceAsync(string reference, string? fallbackUrl, ProtocolAssetKind kind,
            CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return _inner.ResolveReferenceAsync(reference, fallbackUrl, kind, cancellationToken);
        }
    }
}
