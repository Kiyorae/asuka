using System.Globalization;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

// Contracts: https://11.onebot.dev/ and https://12.onebot.dev/interface/.
[TestClass]
public sealed class OneBotConformanceTests
{
    [TestMethod]
    public async Task V11CqStringsDecodeEscapesAndRespectAutoEscape()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.AppendMessageAsync(new Message(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("Reply target")],
            MessageDirection.Outgoing, id: "7"));
        var protocol = Protocol(fixture, OneBotVersion.V11);
        const string cq = "hello &amp; &#91;CQ:at,qq=all&#93;[CQ:at,qq=all][CQ:reply,id=7][CQ:image,file=a&#44;b.png]";
        foreach (var autoEscape in new[] { false, true })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("send_group_msg", new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["message"] = cq,
                ["auto_escape"] = autoEscape,
            }));
            Assert.IsTrue(reply.IsSuccess, reply.Message);
            var stored = await fixture.Store.GetMessageAsync(reply.Data!["message_id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture));
            Assert.IsNotNull(stored);
            if (autoEscape)
            {
                Assert.HasCount(1, stored.Content);
                Assert.AreEqual(cq, ((TextSegment)stored.Content[0]).Text);
            }
            else
            {
                Assert.HasCount(4, stored.Content);
                Assert.AreEqual("hello & [CQ:at,qq=all]", ((TextSegment)stored.Content[0]).Text);
                Assert.IsNull(((MentionSegment)stored.Content[1]).UserId);
                Assert.AreEqual("7", ((ReplySegment)stored.Content[2]).MessageId);
                Assert.AreEqual("a,b.png", ((ImageSegment)stored.Content[3]).Asset.Id);
            }
        }
    }

    [TestMethod]
    public async Task V11RawMessageContainsEscapedCqCodes()
    {
        await using var fixture = new ProtocolTestFixture();
        var message = new Message(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, [new TextSegment("[hi] &"), new MentionSegment(null)], MessageDirection.Outgoing);
        var frames = await Protocol(fixture, OneBotVersion.V11).EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)));
        Assert.AreEqual("&#91;hi&#93; &amp;[CQ:at,qq=all]", frames[0].Payload["raw_message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V12UserAndMemberInfoUseStandardNamesAndKeepRemarkSeparate()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new Friendship(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId, "My friend"));
        var protocol = Protocol(fixture, OneBotVersion.V12);
        var user = await protocol.HandleAsync(new ProtocolCall("get_user_info", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId }));
        Assert.IsTrue(user.IsSuccess);
        Assert.AreEqual("Sender", user.Data!["user_name"]!.GetValue<string>());
        Assert.AreEqual("Alice", user.Data["user_displayname"]!.GetValue<string>());
        Assert.AreEqual("My friend", user.Data["user_remark"]!.GetValue<string>());
        Assert.IsNull(user.Data["nickname"]);
        var friends = await protocol.HandleAsync(new ProtocolCall("get_friend_list", new JsonObject()));
        Assert.IsTrue(JsonNode.DeepEquals(user.Data, friends.Data![0]));
        var member = await protocol.HandleAsync(new ProtocolCall("get_group_member_info", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["user_id"] = ProtocolTestFixture.SenderId,
        }));
        Assert.AreEqual("Sender", member.Data!["user_name"]!.GetValue<string>());
        Assert.AreEqual("Alice Card", member.Data["user_displayname"]!.GetValue<string>());
        Assert.IsNull(member.Data["nickname"]);
    }

    [TestMethod]
    public async Task GroupListContainsOnlyGroupsJoinedByCurrentBot()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new Group("Other bot group", id: "999"));
        foreach (var version in new[] { OneBotVersion.V11, OneBotVersion.V12 })
        {
            var reply = await Protocol(fixture, version).HandleAsync(new ProtocolCall("get_group_list", new JsonObject()));
            Assert.HasCount(1, (JsonArray)reply.Data!);
        }
    }

    [TestMethod]
    public async Task FriendAddedAndRecallNoticesMatchEachVersion()
    {
        await using var fixture = new ProtocolTestFixture();
        var added = new DomainEvent(ProtocolTestFixture.SelfId, new FriendAddedEvent(ProtocolTestFixture.SenderId), id: "friend-event");
        var v11 = await Protocol(fixture, OneBotVersion.V11).EncodeAsync(added);
        Assert.HasCount(1, v11);
        Assert.AreEqual("friend_add", v11[0].Payload["notice_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_002L, v11[0].Payload["user_id"]!.GetValue<long>());
        var v12 = await Protocol(fixture, OneBotVersion.V12).EncodeAsync(added);
        Assert.AreEqual("friend-event", v12[0].Payload["id"]!.GetValue<string>());
        Assert.AreEqual("friend_increase", v12[0].Payload["detail_type"]!.GetValue<string>());
        var recall = new DomainEvent(ProtocolTestFixture.SelfId, new MessageRecalledEvent(new MessageRecalled(
            "7", ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId)));
        var recalled = await Protocol(fixture, OneBotVersion.V12).EncodeAsync(recall);
        Assert.AreEqual("delete", recalled[0].Payload["sub_type"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SelfId, recalled[0].Payload["operator_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V12DoesNotEmitNonstandardRequestEvents()
    {
        await using var fixture = new ProtocolTestFixture();
        var request = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        Assert.IsEmpty(await Protocol(fixture, OneBotVersion.V12).EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new RequestReceivedEvent(request))));
    }

    [TestMethod]
    public async Task V11DoesNotEmitPrivatePokeOrWholeMuteExtensions()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = Protocol(fixture, OneBotVersion.V11);
        var poke = new PokeInteraction(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new PokeEvent(poke))));
        Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new GroupMutedEvent(new GroupMute(ProtocolTestFixture.GroupId, null, ProtocolTestFixture.SelfId, true)))));
        var groupPoke = await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new PokeEvent(poke with { Scene = ChatScene.Group, PeerId = ProtocolTestFixture.GroupId })));
        Assert.HasCount(1, groupPoke);
        Assert.AreEqual("poke", groupPoke[0].Payload["sub_type"]!.GetValue<string>());
        var memberMute = await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new GroupMutedEvent(new GroupMute(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, true, TimeSpan.FromSeconds(60)))));
        Assert.HasCount(1, memberMute);
        Assert.AreEqual(60L, memberMute[0].Payload["duration"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task V12RejectsUnsupportedSceneAndMalformedSegmentsWithoutPersistingPartialMessages()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture, OneBotVersion.V12);
        foreach (var (detailType, message, expectedCode) in new (string?, JsonNode, int)[]
        {
            ("channel", new JsonArray(Text("text")), 10004),
            (null, new JsonArray(Text("text")), 10003),
            ("group", System.Text.Json.Nodes.JsonValue.Create("string messages are v11 only")!, 10003),
            ("group", new JsonArray(Text("valid"), new JsonObject { ["type"] = "face", ["data"] = new JsonObject { ["id"] = "1" } }), 10005),
            ("group", new JsonArray(Text("valid"), new JsonObject { ["type"] = "mention", ["data"] = new JsonObject() }), 10006),
            ("group", new JsonArray(Text("valid"), new JsonObject { ["type"] = "image", ["data"] = new JsonObject { ["file"] = "v11.png" } }), 10006),
        })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("send_message", new JsonObject
            {
                ["detail_type"] = detailType,
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["user_id"] = ProtocolTestFixture.SenderId,
                ["message"] = message,
            }));
            Assert.AreEqual(expectedCode, reply.RetCode, reply.Message);
        }
        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task V12MissingResourcesUseExecutionErrorRange()
    {
        await using var fixture = new ProtocolTestFixture();
        var reply = await Protocol(fixture, OneBotVersion.V12).HandleAsync(new ProtocolCall("get_user_info", new JsonObject { ["user_id"] = "missing" }));
        Assert.AreEqual(35000, reply.RetCode);
    }

    [TestMethod]
    public async Task V12RejectsNumericIdsInsteadOfCoercingV11Types()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture, OneBotVersion.V12);
        var user = await protocol.HandleAsync(new ProtocolCall("get_user_info", new JsonObject { ["user_id"] = 1_000_000_002L }));
        Assert.AreEqual(10003, user.RetCode);
        var send = await protocol.HandleAsync(new ProtocolCall("send_message", new JsonObject
        {
            ["detail_type"] = "group",
            ["group_id"] = 500_000_001L,
            ["message"] = new JsonArray(Text("text")),
        }));
        Assert.AreEqual(10003, send.RetCode);
    }

    [TestMethod]
    public async Task V11ForwardLookupReturnsStandardNodes()
    {
        await using var fixture = new ProtocolTestFixture();
        var forward = new ForwardSegment("forward-id", [new ForwardNode(ProtocolTestFixture.SenderId, "Alice", [new TextSegment("Forwarded text")])]);
        await fixture.Store.SaveAsync(new Message(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, [forward], MessageDirection.Outgoing));
        var reply = await Protocol(fixture, OneBotVersion.V11).HandleAsync(new ProtocolCall("get_forward_msg", new JsonObject { ["id"] = "forward-id" }));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        var node = reply.Data!["message"]![0]!;
        Assert.AreEqual("node", node["type"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SenderId, node["data"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("Alice", node["data"]!["nickname"]!.GetValue<string>());
        Assert.AreEqual("Forwarded text", node["data"]!["content"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V11RequestActionMustMatchFlagKindAndGroupSubtype()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var request = new PendingRequest(RequestKind.GroupJoin, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, ProtocolTestFixture.GroupId);
        await fixture.Store.SaveAsync(request);
        var protocol = Protocol(fixture, OneBotVersion.V11);
        var wrongAction = await protocol.HandleAsync(new ProtocolCall("set_friend_add_request", new JsonObject { ["flag"] = request.Flag }));
        Assert.AreEqual(1400, wrongAction.RetCode);
        var wrongSubtype = await protocol.HandleAsync(new ProtocolCall("set_group_add_request", new JsonObject { ["flag"] = request.Flag, ["sub_type"] = "invite" }));
        Assert.AreEqual(1400, wrongSubtype.RetCode);
        Assert.IsNull((await fixture.Store.GetRequestAsync(request.Id))!.Resolution);
    }

    [TestMethod]
    public async Task V12AudioLocationAndReplySenderRoundTrip()
    {
        await using var fixture = new ProtocolTestFixture();
        var codec = new OneBotSegmentCodec(OneBotVersion.V12, new StubProtocolAssetResolver(), fixture.Store);
        var original = new Message(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, [new TextSegment("original")], MessageDirection.Outgoing, id: "7");
        await fixture.Store.SaveAsync(original);
        var encoded = await codec.EncodeAsync([
            new AudioSegment(new Asset("audio", "song.ogg", "audio/ogg", 7, AssetSource.Inline)),
            new LocationSegment(31.032315, 121.447127, "Place", "Address"),
            new ReplySegment("7"),
        ]);
        Assert.AreEqual("audio", encoded[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("audio", encoded[0]!["data"]!["file_id"]!.GetValue<string>());
        Assert.AreEqual(31.032315, encoded[1]!["data"]!["latitude"]!.GetValue<double>());
        Assert.AreEqual(ProtocolTestFixture.SenderId, encoded[2]!["data"]!["user_id"]!.GetValue<string>());
        var decoded = await codec.DecodeAsync(encoded);
        Assert.IsInstanceOfType<AudioSegment>(decoded[0]);
        Assert.AreEqual(new LocationSegment(31.032315, 121.447127, "Place", "Address"), decoded[1]);
        Assert.AreEqual(ProtocolTestFixture.SenderId, ((ReplySegment)decoded[2]).UserId);
    }

    [TestMethod]
    public async Task V11LocationUsesStringCoordinatesAndDecodesCqParameters()
    {
        var codec = new OneBotSegmentCodec(OneBotVersion.V11, new StubProtocolAssetResolver());
        var location = new LocationSegment(31.25, 121.5, "Place", "Address");
        var encoded = await codec.EncodeAsync([location]);
        Assert.AreEqual("31.25", encoded[0]!["data"]!["lat"]!.GetValue<string>());
        Assert.AreEqual("121.5", encoded[0]!["data"]!["lon"]!.GetValue<string>());
        Assert.AreEqual(location, (await codec.DecodeAsync(encoded))[0]);
    }

    [TestMethod]
    public async Task V12VoidActionsReturnNullData()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture, OneBotVersion.V12);
        var reply = await protocol.HandleAsync(new ProtocolCall("set_group_name", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["group_name"] = "Renamed",
        }));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        Assert.IsNull(protocol.CreateEnvelope(reply)["data"]);
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture, OneBotVersion version) =>
        new(version, ProtocolTestFixture.SelfId, fixture.Platform, new StubProtocolAssetResolver());

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["data"] = new JsonObject { ["text"] = text } };
}
