using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class MilkyProtocolTests
{
    private const long MaxSafeJsonInteger = 9_007_199_254_740_991;
    private static readonly string[] SuccessEnvelopeKeys = ["status", "retcode", "data"];
    private static readonly string[] FailureEnvelopeKeys = ["status", "retcode", "message"];
    private static readonly string[] EventEnvelopeKeys = ["time", "self_id", "event_type", "data"];

    [TestMethod]
    public async Task EnvelopeOmitsMessageOnSuccessAndDataOnFailure()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

        var success = protocol.CreateEnvelope(
            ProtocolReply.Success(new JsonObject { ["answer"] = 42 }));
        CollectionAssert.AreEquivalent(
            SuccessEnvelopeKeys,
            success.Select(static item => item.Key).ToArray());
        Assert.AreEqual("ok", success["status"]!.GetValue<string>());
        Assert.AreEqual(42, success["data"]!["answer"]!.GetValue<int>());

        var failure = protocol.CreateEnvelope(new ProtocolReply(-400, Message: "invalid"));
        CollectionAssert.AreEquivalent(
            FailureEnvelopeKeys,
            failure.Select(static item => item.Key).ToArray());
        Assert.AreEqual("failed", failure["status"]!.GetValue<string>());
        Assert.AreEqual(-400, failure["retcode"]!.GetValue<int>());
        Assert.AreEqual("invalid", failure["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MessageApisSendPersistAndReadTheMilkyShape()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

        var login = await protocol.HandleAsync(new ProtocolCall("get_login_info", new JsonObject()));
        Assert.IsTrue(login.IsSuccess);
        Assert.AreEqual(1_000_000_001L, login.Data!["uin"]!.GetValue<long>());
        Assert.AreEqual("Asuka Bot", login.Data!["nickname"]!.GetValue<string>());

        var send = await protocol.HandleAsync(new ProtocolCall(
            "send_group_message",
            new JsonObject
            {
                ["group_id"] = 500_000_001L,
                ["message"] = new JsonArray
                {
                    Segment("text", new JsonObject { ["text"] = "hello from Milky" }),
                },
            }));
        Assert.IsTrue(send.IsSuccess);
        Assert.AreEqual(1L, send.Data!["message_seq"]!.GetValue<long>());
        Assert.IsGreaterThan(0L, send.Data!["time"]!.GetValue<long>());

        var stored = await fixture.Store.GetMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            1,
            ProtocolTestFixture.SelfId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(MessageDirection.Incoming, stored.Direction);
        Assert.AreEqual("hello from Milky", stored.Content.PlainText());

        var get = await protocol.HandleAsync(new ProtocolCall(
            "get_message",
            new JsonObject
            {
                ["message_scene"] = "group",
                ["peer_id"] = ProtocolTestFixture.GroupId,
                ["message_seq"] = 1L,
            }));
        Assert.IsTrue(get.IsSuccess);
        var incoming = (JsonObject)get.Data!["message"]!;
        Assert.AreEqual("group", incoming["message_scene"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, incoming["peer_id"]!.GetValue<long>());
        Assert.AreEqual(1L, incoming["message_seq"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, incoming["sender_id"]!.GetValue<long>());
        Assert.IsNotNull(incoming["group"]);
        Assert.IsNotNull(incoming["group_member"]);

        var unknown = await protocol.HandleAsync(new ProtocolCall("not_an_api", new JsonObject()));
        Assert.AreEqual(-404, unknown.RetCode);
        Assert.AreEqual(404, unknown.HttpStatus);
    }

    [TestMethod]
    public async Task MessageEventsAreFlatAndAllWireTimesAndDurationsAreIntegers()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var message = new Message(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("hello"), new MentionSegment(ProtocolTestFixture.SelfId)],
            MessageDirection.Outgoing,
            id: "70000000001",
            seq: 9,
            time: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_987));
        var domainEvent = new DomainEvent(
            ProtocolTestFixture.SelfId,
            new MessageEvent(message),
            time: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_999));

        var frames = await protocol.EncodeAsync(domainEvent);
        Assert.HasCount(1, frames);
        var payload = frames[0].Payload;
        CollectionAssert.AreEquivalent(
            EventEnvelopeKeys,
            payload.Select(static item => item.Key).ToArray());
        Assert.AreEqual(1_700_000_001L, payload["time"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, payload["self_id"]!.GetValue<long>());
        Assert.AreEqual("message_receive", payload["event_type"]!.GetValue<string>());

        var data = (JsonObject)payload["data"]!;
        Assert.IsFalse(data.ContainsKey("message"));
        Assert.AreEqual(1_700_000_000L, data["time"]!.GetValue<long>());
        Assert.AreEqual("group", data["message_scene"]!.GetValue<string>());
        Assert.AreEqual(9L, data["message_seq"]!.GetValue<long>());
        Assert.IsNotNull(data["group"]);
        Assert.IsNotNull(data["group_member"]);
        var segments = (JsonArray)data["segments"]!;
        Assert.AreEqual(1_000_000_001L, segments[1]!["data"]!["user_id"]!.GetValue<long>());

        var muteFrames = await protocol.EncodeAsync(new DomainEvent(
            ProtocolTestFixture.SelfId,
            new GroupMutedEvent(new GroupMute(
                ProtocolTestFixture.GroupId,
                ProtocolTestFixture.SenderId,
                ProtocolTestFixture.SelfId,
                muted: true,
                duration: TimeSpan.FromMilliseconds(12_750)))));
        Assert.HasCount(1, muteFrames);
        Assert.AreEqual(12L, muteFrames[0].Payload["data"]!["duration"]!.GetValue<long>());

        var foreign = domainEvent with { SelfId = "1000000999" };
        Assert.IsEmpty(await protocol.EncodeAsync(foreign));
        Assert.IsNull(protocol.HeartbeatInterval);
    }

    [TestMethod]
    public async Task NotificationSequencesAreStableDistinctSafeAndExcludeInvitations()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var first = new PendingRequest(
            RequestKind.GroupJoin,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            ProtocolTestFixture.GroupId,
            id: "request-a",
            flag: "join-a");
        var second = new PendingRequest(
            RequestKind.GroupJoin,
            "1000000003",
            ProtocolTestFixture.SelfId,
            ProtocolTestFixture.GroupId,
            id: "request-b",
            flag: "join-b");
        var invitation = new PendingRequest(
            RequestKind.GroupInvite,
            "1000000004",
            ProtocolTestFixture.SelfId,
            ProtocolTestFixture.GroupId,
            id: "request-c",
            flag: "invite-c");
        await fixture.Store.SaveAsync(first);
        await fixture.Store.SaveAsync(second);
        await fixture.Store.SaveAsync(invitation);

        var initial = await protocol.HandleAsync(
            new ProtocolCall("get_group_notifications", new JsonObject { ["limit"] = 20L }));
        var repeated = await protocol.HandleAsync(
            new ProtocolCall("get_group_notifications", new JsonObject { ["limit"] = 20L }));
        var initialNotifications = (JsonArray)initial.Data!["notifications"]!;
        var repeatedNotifications = (JsonArray)repeated.Data!["notifications"]!;
        Assert.HasCount(2, initialNotifications);
        Assert.IsTrue(initialNotifications.All(
            static item => item!["type"]!.GetValue<string>() == "join_request"));

        var initialSequences = initialNotifications
            .Select(static item => item!["notification_seq"]!.GetValue<long>())
            .ToArray();
        var repeatedSequences = repeatedNotifications
            .Select(static item => item!["notification_seq"]!.GetValue<long>())
            .ToArray();
        CollectionAssert.AreEqual(initialSequences, repeatedSequences);
        Assert.AreEqual(2, initialSequences.Distinct().Count());
        Assert.IsTrue(initialSequences.All(static value => value >= 0 && value <= MaxSafeJsonInteger));

        var requestFrame = await protocol.EncodeAsync(new DomainEvent(
            ProtocolTestFixture.SelfId,
            new RequestReceivedEvent(first)));
        Assert.HasCount(1, requestFrame);
        var encodedSequence = requestFrame[0].Payload["data"]!["notification_seq"]!.GetValue<long>();
        CollectionAssert.Contains(initialSequences, encodedSequence);

        var invitationFrame = await protocol.EncodeAsync(new DomainEvent(
            ProtocolTestFixture.SelfId,
            new RequestReceivedEvent(invitation)));
        Assert.HasCount(1, invitationFrame);
        var invitationSequence = invitationFrame[0].Payload["data"]!["invitation_seq"]!.GetValue<long>();
        Assert.IsTrue(invitationSequence >= 0 && invitationSequence <= MaxSafeJsonInteger);
    }

    [TestMethod]
    public async Task MissingCachedFileIsRehydratedWithCurrentIdentifierAndSize()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"asuka-rehydrate-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);
            var staleAsset = new Asset(
                new string('0', 64),
                "payload.bin",
                "application/octet-stream",
                ByteCount: 1,
                AssetSource.Local(sourcePath));
            var message = new Message(
                ChatScene.Group,
                ProtocolTestFixture.GroupId,
                ProtocolTestFixture.SenderId,
                ProtocolTestFixture.SelfId,
                [new FileSegment(staleAsset)],
                MessageDirection.Outgoing,
                id: "70000000002",
                seq: 10);
            var protocol = new MilkyProtocol(
                ProtocolTestFixture.SelfId,
                fixture.Platform,
                fixture.Media);

            var frames = await protocol.EncodeAsync(new DomainEvent(
                ProtocolTestFixture.SelfId,
                new MessageEvent(message)));

            Assert.HasCount(1, frames);
            var segments = (JsonArray)frames[0].Payload["data"]!["segments"]!;
            var data = (JsonObject)segments[0]!["data"]!;
            Assert.AreNotEqual(staleAsset.Id, data["file_id"]!.GetValue<string>());
            Assert.AreEqual("payload.bin", data["file_name"]!.GetValue<string>());
            Assert.AreEqual(5L, data["file_size"]!.GetValue<long>());
            Assert.IsTrue(fixture.Assets.Exists(data["file_id"]!.GetValue<string>()));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [TestMethod]
    public async Task ImageSegmentsEmitDimensionsFromPngHeader()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var asset = await fixture.Assets.StoreAsync(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 2, 128, 0, 0, 1, 224 },
            "640x480.png");
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var message = new Message(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new ImageSegment(asset)],
            MessageDirection.Outgoing,
            id: "70000000003",
            seq: 11);

        var frames = await protocol.EncodeAsync(new DomainEvent(
            ProtocolTestFixture.SelfId,
            new MessageEvent(message)));

        var data = (JsonObject)((JsonArray)frames[0].Payload["data"]!["segments"]!)[0]!["data"]!;
        Assert.AreEqual(640, data["width"]!.GetValue<int>());
        Assert.AreEqual(480, data["height"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ImageSegmentsUseZeroDimensionsForMalformedHeaders()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var asset = await fixture.Assets.StoreAsync(new byte[] { 137, 80, 78, 71, 0, 0, 0 }, "invalid.png");
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var message = new Message(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new ImageSegment(asset)],
            MessageDirection.Outgoing,
            id: "70000000004",
            seq: 12);

        var frames = await protocol.EncodeAsync(new DomainEvent(
            ProtocolTestFixture.SelfId,
            new MessageEvent(message)));

        var data = (JsonObject)((JsonArray)frames[0].Payload["data"]!["segments"]!)[0]!["data"]!;
        Assert.AreEqual(0, data["width"]!.GetValue<int>());
        Assert.AreEqual(0, data["height"]!.GetValue<int>());
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };
}
