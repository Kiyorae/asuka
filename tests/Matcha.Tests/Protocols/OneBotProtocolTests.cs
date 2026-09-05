using System.Globalization;
using System.Text.Json.Nodes;
using Matcha.Core;
using Matcha.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Matcha.Tests.Protocols;

[TestClass]
public sealed class OneBotProtocolTests
{
    private static readonly string[] ExpectedActions =
    [
        "send_msg", "send_private_msg", "send_group_msg", "send_message",
        "delete_msg", "delete_message", "get_msg", "get_message",
        "get_login_info", "get_self_info", "get_stranger_info", "get_user_info", "get_friend_list",
        "get_group_info", "get_group_list", "get_group_member_info", "get_group_member_list",
        "set_group_name", "set_group_card", "set_group_special_title", "set_group_admin",
        "set_group_ban", "set_group_whole_ban", "set_group_kick", "set_group_leave", "leave_group",
        "set_friend_add_request", "set_group_add_request",
        "get_status", "get_version_info", "get_version", "get_supported_actions",
        "can_send_image", "can_send_record",
    ];

    [TestMethod]
    public async Task EnvelopesAndHandshakeFramesFollowVersionSpecificWireContract()
    {
        await using var fixture = new ProtocolTestFixture();
        var resolver = new StubProtocolAssetResolver();
        var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var echo = new JsonObject { ["trace"] = 7 };

        var v11Success = v11.CreateEnvelope(
            ProtocolReply.Success(new JsonObject { ["message_id"] = 70_000_000_001L }));
        Assert.AreEqual("ok", v11Success["status"]!.GetValue<string>());
        Assert.AreEqual(0, v11Success["retcode"]!.GetValue<int>());
        Assert.AreEqual(70_000_000_001L, v11Success["data"]!["message_id"]!.GetValue<long>());
        Assert.IsFalse(v11Success.ContainsKey("message"));

        var v12Success = v12.CreateEnvelope(
            ProtocolReply.Success(new JsonObject { ["message_id"] = "70000000001" }));
        Assert.AreEqual("ok", v12Success["status"]!.GetValue<string>());
        Assert.AreEqual("70000000001", v12Success["data"]!["message_id"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, v12Success["message"]!.GetValue<string>());

        var v11Failure = v11.CreateEnvelope(new ProtocolReply(1404, Message: "missing"), echo);
        Assert.AreEqual("failed", v11Failure["status"]!.GetValue<string>());
        Assert.AreEqual(1404, v11Failure["retcode"]!.GetValue<int>());
        Assert.AreEqual("missing", v11Failure["data"]!["message"]!.GetValue<string>());
        Assert.IsFalse(v11Failure.ContainsKey("message"));
        Assert.AreEqual(7, v11Failure["echo"]!["trace"]!.GetValue<int>());

        var v12Failure = v12.CreateEnvelope(new ProtocolReply(10002, Message: "missing"), echo);
        Assert.AreEqual("failed", v12Failure["status"]!.GetValue<string>());
        Assert.AreEqual(10002, v12Failure["retcode"]!.GetValue<int>());
        Assert.IsNull(v12Failure["data"]);
        Assert.AreEqual("missing", v12Failure["message"]!.GetValue<string>());
        Assert.AreEqual(7, v12Failure["echo"]!["trace"]!.GetValue<int>());

        var v11Handshake = await v11.GetHandshakeFramesAsync();
        Assert.HasCount(1, v11Handshake);
        Assert.AreEqual(1_000_000_001L, v11Handshake[0].Payload["self_id"]!.GetValue<long>());
        Assert.AreEqual("meta_event", v11Handshake[0].Payload["post_type"]!.GetValue<string>());
        Assert.AreEqual("connect", v11Handshake[0].Payload["sub_type"]!.GetValue<string>());

        var v12Handshake = await v12.GetHandshakeFramesAsync();
        Assert.HasCount(2, v12Handshake);
        Assert.AreEqual("connect", v12Handshake[0].Payload["detail_type"]!.GetValue<string>());
        Assert.AreEqual(
            ProtocolTestFixture.SelfId,
            v12Handshake[0].Payload["self"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("status_update", v12Handshake[1].Payload["detail_type"]!.GetValue<string>());
        Assert.AreEqual("12", v12Handshake[0].Payload["version"]!["onebot_version"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V11AsyncSuffixReturnsTheCompleteActionManifestButV12RejectsIt()
    {
        await using var fixture = new ProtocolTestFixture();
        var resolver = new StubProtocolAssetResolver();
        var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, resolver);

        var reply = await v11.HandleAsync(new ProtocolCall("get_supported_actions_async", new JsonObject()));
        Assert.IsTrue(reply.IsSuccess);
        var actions = ((JsonArray)reply.Data!).Select(static node => node!.GetValue<string>()).ToArray();
        Assert.HasCount(ExpectedActions.Length, actions);
        CollectionAssert.AreEquivalent(ExpectedActions, actions);

        var unsupported = await v12.HandleAsync(
            new ProtocolCall("get_supported_actions_async", new JsonObject()));
        Assert.AreEqual(10002, unsupported.RetCode);
        Assert.IsFalse(unsupported.IsSuccess);
    }

    [TestMethod]
    public async Task MessageEventsUseNumericV11IdsStringV12IdsAndFilterForeignSelf()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var resolver = new StubProtocolAssetResolver();
        var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var voice = new Asset("voice-asset", "voice.amr", "audio/amr", 4, AssetSource.Inline);
        var message = new Message(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [
                new MentionSegment(ProtocolTestFixture.SelfId),
                new ReplySegment("70000000000"),
                new RecordSegment(voice, TimeSpan.FromSeconds(3)),
            ],
            MessageDirection.Outgoing,
            id: "70000000001",
            seq: 7,
            time: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_987));
        var domainEvent = new DomainEvent(
            ProtocolTestFixture.SelfId,
            new MessageEvent(message),
            time: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_999));

        var v11Frames = await v11.EncodeAsync(domainEvent);
        Assert.HasCount(1, v11Frames);
        var v11Payload = v11Frames[0].Payload;
        Assert.AreEqual(1_000_000_001L, v11Payload["self_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, v11Payload["user_id"]!.GetValue<long>());
        Assert.AreEqual(500_000_001L, v11Payload["group_id"]!.GetValue<long>());
        Assert.AreEqual(70_000_000_001L, v11Payload["message_id"]!.GetValue<long>());
        Assert.AreEqual("Alice Card", v11Payload["sender"]!["card"]!.GetValue<string>());
        Assert.AreEqual("admin", v11Payload["sender"]!["role"]!.GetValue<string>());
        Assert.AreEqual("Maintainer", v11Payload["sender"]!["title"]!.GetValue<string>());
        var v11Segments = (JsonArray)v11Payload["message"]!;
        Assert.AreEqual("at", v11Segments[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SelfId, v11Segments[0]!["data"]!["qq"]!.GetValue<string>());
        Assert.AreEqual("70000000000", v11Segments[1]!["data"]!["id"]!.GetValue<string>());
        Assert.AreEqual("record", v11Segments[2]!["type"]!.GetValue<string>());
        Assert.AreEqual("local/voice-asset", v11Segments[2]!["data"]!["file"]!.GetValue<string>());

        var v12Frames = await v12.EncodeAsync(domainEvent);
        Assert.HasCount(1, v12Frames);
        var v12Payload = v12Frames[0].Payload;
        Assert.AreEqual(ProtocolTestFixture.SelfId, v12Payload["self"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SenderId, v12Payload["user_id"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.GroupId, v12Payload["group_id"]!.GetValue<string>());
        Assert.AreEqual("70000000001", v12Payload["message_id"]!.GetValue<string>());
        Assert.IsFalse(v12Payload.ContainsKey("sender"));
        Assert.IsFalse(v12Payload.ContainsKey("post_type"));
        var v12Segments = (JsonArray)v12Payload["message"]!;
        Assert.AreEqual("mention", v12Segments[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SelfId, v12Segments[0]!["data"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("70000000000", v12Segments[1]!["data"]!["message_id"]!.GetValue<string>());
        Assert.AreEqual("voice", v12Segments[2]!["type"]!.GetValue<string>());
        Assert.AreEqual("voice-asset", v12Segments[2]!["data"]!["file_id"]!.GetValue<string>());

        var foreign = domainEvent with { SelfId = "1000000999" };
        Assert.IsEmpty(await v11.EncodeAsync(foreign));
        Assert.IsEmpty(await v12.EncodeAsync(foreign));
    }

    [TestMethod]
    public async Task SendGroupMessagePersistsAndReturnsVersionSpecificMessageIds()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var resolver = new StubProtocolAssetResolver();
        var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, resolver);
        var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, resolver);

        var v11Reply = await v11.HandleAsync(new ProtocolCall(
            "send_group_msg",
            new JsonObject
            {
                ["group_id"] = 500_000_001L,
                ["message"] = "v11 plain text",
            }));
        Assert.IsTrue(v11Reply.IsSuccess);
        var v11MessageId = v11Reply.Data!["message_id"]!.GetValue<long>();
        var v11Stored = await fixture.Store.GetMessageAsync(v11MessageId.ToString(CultureInfo.InvariantCulture));
        Assert.IsNotNull(v11Stored);
        Assert.AreEqual(MessageDirection.Incoming, v11Stored.Direction);
        Assert.AreEqual("v11 plain text", v11Stored.Content.PlainText());

        var v12Reply = await v12.HandleAsync(new ProtocolCall(
            "send_group_msg",
            new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["message"] = new JsonArray
                {
                    Segment("text", new JsonObject { ["text"] = "v12 segments" }),
                },
            }));
        Assert.IsTrue(v12Reply.IsSuccess);
        var v12MessageId = v12Reply.Data!["message_id"]!.GetValue<string>();
        Assert.IsTrue(long.TryParse(v12MessageId, out _));
        Assert.IsGreaterThan(0L, v12Reply.Data!["time"]!.GetValue<long>());
        var v12Stored = await fixture.Store.GetMessageAsync(v12MessageId);
        Assert.IsNotNull(v12Stored);
        Assert.AreEqual(MessageDirection.Incoming, v12Stored.Direction);
        Assert.AreEqual("v12 segments", v12Stored.Content.PlainText());
        Assert.AreEqual(2L, v12Stored.Seq);
    }

    [TestMethod]
    public async Task SegmentDecoderAcceptsVersionSpecificMentionReplyAndVoiceFields()
    {
        var resolver = new StubProtocolAssetResolver();
        var v11 = new OneBotSegmentCodec(OneBotVersion.V11, resolver);
        var v12 = new OneBotSegmentCodec(OneBotVersion.V12, resolver);

        var v11Decoded = await v11.DecodeAsync(new JsonArray
        {
            Segment("at", new JsonObject { ["qq"] = "all" }),
            Segment("reply", new JsonObject { ["id"] = "70000000001" }),
            Segment("record", new JsonObject { ["file"] = "record.amr", ["duration"] = 9L }),
        });
        Assert.IsNull(((MentionSegment)v11Decoded[0]).UserId);
        Assert.AreEqual("70000000001", ((ReplySegment)v11Decoded[1]).MessageId);
        Assert.AreEqual(TimeSpan.FromSeconds(9), ((RecordSegment)v11Decoded[2]).Duration);

        var v12Decoded = await v12.DecodeAsync(new JsonArray
        {
            Segment("mention", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId }),
            Segment("reply", new JsonObject { ["message_id"] = "70000000002" }),
            Segment("voice", new JsonObject { ["file_id"] = "voice-id" }),
        });
        Assert.AreEqual(ProtocolTestFixture.SenderId, ((MentionSegment)v12Decoded[0]).UserId);
        Assert.AreEqual("70000000002", ((ReplySegment)v12Decoded[1]).MessageId);
        Assert.AreEqual("voice-id", ((RecordSegment)v12Decoded[2]).Asset.Id);
    }

    private static JsonObject Segment(string type, JsonObject data) => new()
    {
        ["type"] = type,
        ["data"] = data,
    };
}
