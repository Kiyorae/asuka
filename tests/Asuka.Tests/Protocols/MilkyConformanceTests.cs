using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Contract cases from https://milky.ntqqrev.org/struct/OutgoingSegment and /api/friend.
[TestClass]
public sealed class MilkyConformanceTests
{
    [TestMethod]
    public async Task RecalledMessagesCannotReappearInGetHistoryOrReply()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var sent = await protocol.HandleAsync(Send(Segment("text", new JsonObject { ["text"] = "withdraw this" })));
        var sequence = sent.Data!["message_seq"]!.GetValue<long>();
        var recalled = await protocol.HandleAsync(new ProtocolCall("recall_group_message", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["message_seq"] = sequence,
        }));
        Assert.IsTrue(recalled.IsSuccess);
        Assert.IsFalse((await protocol.HandleAsync(Get(sequence))).IsSuccess);
        var history = await protocol.HandleAsync(new ProtocolCall("get_history_messages", new JsonObject
        {
            ["message_scene"] = "group",
            ["peer_id"] = ProtocolTestFixture.GroupId,
        }));
        Assert.IsTrue(history.IsSuccess);
        Assert.HasCount(0, (JsonArray)history.Data!["messages"]!);
        var reply = await protocol.HandleAsync(Send(Segment("reply", new JsonObject { ["message_seq"] = sequence }),
            Segment("text", new JsonObject { ["text"] = "cannot quote withdrawn message" })));
        Assert.IsFalse(reply.IsSuccess);
    }

    [TestMethod]
    public async Task ReactionTypeSeparatesFaceFromEmojiAndSurvivesEventEncoding()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var sent = await protocol.HandleAsync(Send(Segment("text", new JsonObject { ["text"] = "react here" })));
        var sequence = sent.Data!["message_seq"]!.GetValue<long>();
        var stored = (await fixture.Store.GetMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            sequence, ProtocolTestFixture.SelfId))!;
        foreach (var reactionType in new[] { "face", "emoji" })
        {
            var added = await protocol.HandleAsync(new ProtocolCall("send_group_message_reaction", new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["message_seq"] = sequence,
                ["reaction"] = "128077",
                ["reaction_type"] = reactionType,
            }));
            Assert.IsTrue(added.IsSuccess, added.Message);
            var frames = await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
                new MessageReactionEvent(new MessageReaction(stored.Id, ChatScene.Group,
                    ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, "128077", true, reactionType))));
            Assert.AreEqual(reactionType, frames[0].Payload["data"]!["reaction_type"]!.GetValue<string>());
        }

        Assert.HasCount(2, await fixture.Store.GetMessageReactionsAsync(stored.Id));
        var removed = await protocol.HandleAsync(new ProtocolCall("send_group_message_reaction", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["message_seq"] = sequence,
            ["reaction"] = "128077",
            ["reaction_type"] = "emoji",
            ["is_add"] = false,
        }));
        Assert.IsTrue(removed.IsSuccess);
        var remaining = await fixture.Store.GetMessageReactionsAsync(stored.Id);
        Assert.HasCount(1, remaining);
        Assert.AreEqual("face", remaining[0].ReactionType);
        var invalid = await protocol.HandleAsync(new ProtocolCall("send_group_message_reaction", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["message_seq"] = sequence,
            ["reaction"] = "128077",
            ["reaction_type"] = "custom",
        }));
        Assert.AreEqual(-400, invalid.RetCode);
    }

    [TestMethod]
    public async Task LargeFaceStickerAndForwardMetadataSurviveStorageAndGetMessage()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var sent = await protocol.HandleAsync(Send(
            Segment("face", new JsonObject { ["face_id"] = "66", ["is_large"] = true }),
            Segment("image", new JsonObject
            {
                ["uri"] = "base64://AQID",
                ["sub_type"] = "sticker",
                ["summary"] = "A sticker",
            }),
            Segment("forward", new JsonObject
            {
                ["title"] = "Highlights",
                ["summary"] = "One message",
                ["prompt"] = "Open highlights",
                ["preview"] = new JsonArray("Custom preview"),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["user_id"] = ProtocolTestFixture.SenderId,
                    ["sender_name"] = "Alice",
                    ["time"] = 1_700_000_000L,
                    ["segments"] = new JsonArray(Segment("text", new JsonObject { ["text"] = "hello" })),
                }),
            })));
        Assert.IsTrue(sent.IsSuccess, sent.Message);
        var reply = await protocol.HandleAsync(Get(sent.Data!["message_seq"]!.GetValue<long>()));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        var segments = (JsonArray)reply.Data!["message"]!["segments"]!;
        Assert.IsTrue(segments[0]!["data"]!["is_large"]!.GetValue<bool>());
        Assert.AreEqual("sticker", segments[1]!["data"]!["sub_type"]!.GetValue<string>());
        Assert.AreEqual("A sticker", segments[1]!["data"]!["summary"]!.GetValue<string>());
        Assert.AreEqual("Highlights", segments[2]!["data"]!["title"]!.GetValue<string>());
        Assert.AreEqual("One message", segments[2]!["data"]!["summary"]!.GetValue<string>());
        Assert.AreEqual("Custom preview", segments[2]!["data"]!["preview"]![0]!.GetValue<string>());
        var forward = await protocol.HandleAsync(new ProtocolCall("get_forwarded_messages", new JsonObject
        {
            ["forward_id"] = segments[2]!["data"]!["forward_id"]!.GetValue<string>(),
        }));
        Assert.IsTrue(forward.IsSuccess);
        Assert.AreEqual(1_700_000_000L, forward.Data!["messages"]![0]!["time"]!.GetValue<long>());
        Assert.AreEqual("hello", forward.Data!["messages"]![0]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MalformedOrUnsupportedOutgoingSegmentsFailWithoutSendingPartialMessage()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        JsonNode?[] invalidSegments =
        [
            null,
            System.Text.Json.Nodes.JsonValue.Create("not a segment"),
            new JsonObject { ["type"] = "text" },
            Segment("mention", new JsonObject()),
            Segment("face", new JsonObject()),
            Segment("image", new JsonObject()),
            Segment("reply", new JsonObject { ["message_seq"] = 999L }),
            Segment("xml", new JsonObject { ["xml_payload"] = "<msg/>" }),
            Segment("unknown_extension", new JsonObject()),
            Segment("forward", new JsonObject { ["messages"] = new JsonArray() }),
        ];

        foreach (var invalid in invalidSegments)
        {
            var response = await protocol.HandleAsync(Send(
                Segment("text", new JsonObject { ["text"] = "must not be sent" }), invalid));
            Assert.IsFalse(response.IsSuccess, invalid?.ToJsonString() ?? "null");
        }

        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(
            new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task LightAppPreservesJsonPayloadOnSendAndReceive()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        const string payload = "{\"app\":\"com.tencent.miniapp\",\"meta\":{\"detail\":\"sample\"}}";
        var sent = await protocol.HandleAsync(Send(Segment("light_app", new JsonObject
        {
            ["json_payload"] = payload,
        })));
        Assert.IsTrue(sent.IsSuccess, sent.Message);
        var reply = await protocol.HandleAsync(Get(sent.Data!["message_seq"]!.GetValue<long>()));
        var segment = reply.Data!["message"]!["segments"]![0]!;
        Assert.AreEqual("light_app", segment["type"]!.GetValue<string>());
        Assert.AreEqual(payload, segment["data"]!["json_payload"]!.GetValue<string>());
        Assert.AreEqual("com.tencent.miniapp", segment["data"]!["app_name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task IncomingRichSegmentsKeepTheirOfficialTypesAndFields()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var rich = new[]
        {
            Segment("xml", new JsonObject { ["service_id"] = 60, ["xml_payload"] = "<msg/>" }),
            Segment("markdown", new JsonObject { ["content"] = "**hello**" }),
            Segment("market_face", new JsonObject
            {
                ["emoji_package_id"] = 7, ["emoji_id"] = "abc", ["key"] = "key",
                ["summary"] = "sticker", ["url"] = "https://example.com/sticker.gif",
            }),
        };
        var message = new Message(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId,
            rich.Select(item => new UnsupportedSegment(item["type"]!.GetValue<string>(),
                Asuka.Core.JsonValue.Parse(item["data"]!.ToJsonString()))), MessageDirection.Outgoing, seq: 1);
        var frames = await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)));
        var segments = (JsonArray)frames[0].Payload["data"]!["segments"]!;
        Assert.HasCount(3, segments);
        for (var index = 0; index < rich.Length; index++)
        {
            Assert.IsTrue(JsonNode.DeepEquals(rich[index], segments[index]));
        }
    }

    [TestMethod]
    public async Task FriendRequestResolutionCannotResolveAnotherBotsOrGroupRequests()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var foreign = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId,
            "1000000099", flag: "foreign-request");
        var group = new PendingRequest(RequestKind.GroupJoin, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.GroupId, flag: "group-request");
        await fixture.Store.SaveAsync(foreign);
        await fixture.Store.SaveAsync(group);
        foreach (var pending in new[] { foreign, group })
        {
            var response = await protocol.HandleAsync(new ProtocolCall("reject_friend_request", new JsonObject
            {
                ["initiator_uid"] = pending.Flag,
            }));
            Assert.IsFalse(response.IsSuccess);
            Assert.IsNull((await fixture.Store.GetRequestByFlagAsync(pending.Flag))!.Resolution);
        }
    }

    [TestMethod]
    public async Task FilteredFriendQueriesAndResolutionsDoNotUseUnfilteredRequests()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var pending = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, flag: "unfiltered-request");
        await fixture.Store.SaveAsync(pending);
        var listed = await protocol.HandleAsync(new ProtocolCall("get_friend_requests", new JsonObject
        {
            ["is_filtered"] = true,
        }));
        Assert.IsTrue(listed.IsSuccess);
        Assert.HasCount(0, (JsonArray)listed.Data!["requests"]!);
        var resolved = await protocol.HandleAsync(new ProtocolCall("reject_friend_request", new JsonObject
        {
            ["initiator_uid"] = pending.Flag,
            ["is_filtered"] = true,
        }));
        Assert.IsFalse(resolved.IsSuccess);
        Assert.IsNull((await fixture.Store.GetRequestByFlagAsync(pending.Flag))!.Resolution);
    }

    [TestMethod]
    public async Task UnavailableQqCredentialsReturnFailure()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        foreach (var api in new[] { "get_cookies", "get_csrf_token" })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall(api, new JsonObject { ["domain"] = "qun.qq.com" }));
            Assert.IsFalse(reply.IsSuccess);
            Assert.AreEqual("failed", protocol.CreateEnvelope(reply)["status"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task UnimplementedReadMarkersAndProfileLikesDoNotReportSuccess()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var read = await protocol.HandleAsync(new ProtocolCall("mark_message_as_read", new JsonObject
        {
            ["message_scene"] = "group",
            ["peer_id"] = ProtocolTestFixture.GroupId,
            ["message_seq"] = 1L,
        }));
        var like = await protocol.HandleAsync(new ProtocolCall("send_profile_like", new JsonObject
        {
            ["user_id"] = ProtocolTestFixture.SenderId,
            ["count"] = 1,
        }));
        Assert.IsFalse(read.IsSuccess);
        Assert.IsFalse(like.IsSuccess);
    }

    private static ProtocolCall Send(params JsonNode?[] segments) => new("send_group_message", new JsonObject
    {
        ["group_id"] = ProtocolTestFixture.GroupId,
        ["message"] = new JsonArray(segments),
    });

    private static ProtocolCall Get(long sequence) => new("get_message", new JsonObject
    {
        ["message_scene"] = "group",
        ["peer_id"] = ProtocolTestFixture.GroupId,
        ["message_seq"] = sequence,
    });

    private static JsonObject Segment(string type, JsonObject data) => new() { ["type"] = type, ["data"] = data };
}
