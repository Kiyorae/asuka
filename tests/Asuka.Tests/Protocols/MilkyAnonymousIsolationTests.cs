using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class MilkyAnonymousIsolationTests
{
    [TestMethod]
    public async Task ProtocolSwitchDoesNotExposeAnonymousEventsOrHistory()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var message = await AnonymousAsync(fixture);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message))));
        var found = await protocol.HandleAsync(Call("get_message", message.Seq));
        Assert.IsFalse(found.IsSuccess);
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, found.EffectiveData.ToJsonString());

        var history = await protocol.HandleAsync(Call("get_history_messages"));
        Assert.IsTrue(history.IsSuccess, history.Message);
        Assert.IsEmpty(history.Data!["messages"]!.AsArray());
        var ordinary = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId, [new TextSegment("public message")]);
        var mixed = await protocol.HandleAsync(Call("get_history_messages"));
        Assert.IsTrue(mixed.IsSuccess, mixed.Message);
        Assert.HasCount(1, mixed.Data!["messages"]!.AsArray());
        Assert.AreEqual(ordinary.Seq, mixed.Data["messages"]![0]!["message_seq"]!.GetValue<long>());
        Assert.DoesNotContain("private anonymous content", mixed.EffectiveData.ToJsonString());

        await fixture.Platform.RecallMessageAsync(message.Id, ProtocolTestFixture.SelfId);
        var recalled = new MessageRecalled(message.Id, message.Scene, message.PeerId, message.SenderId,
            ProtocolTestFixture.SelfId, message.Anonymous);
        Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageRecalledEvent(recalled))));
        Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new MessageRecalledEvent(recalled with { Anonymous = null }))));
    }

    [TestMethod]
    public async Task CrossProtocolRepliesCannotRecoverTheRealAuthor()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var message = await AnonymousAsync(fixture);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var send = new ProtocolCall("send_group_message", new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["segments"] = new JsonArray
            {
                new JsonObject { ["type"] = "reply", ["data"] = new JsonObject { ["message_seq"] = message.Seq } },
                new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "reply" } },
            },
        });
        Assert.IsFalse((await protocol.HandleAsync(send)).IsSuccess);

        var quote = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId,
            [new ReplySegment(message.Id, message.SenderId), new TextSegment("public reply")]);
        var wire = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(quote)))).Single().Payload;
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, wire.ToJsonString());
        Assert.DoesNotContain("private anonymous content", wire.ToJsonString());
        Assert.HasCount(1, wire["data"]!["segments"]!.AsArray());
    }

    [TestMethod]
    public async Task AnonymousMessagesCannotLeakThroughEssenceOrForwardProfileLookup()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var message = await AnonymousAsync(fixture);
        await Assert.ThrowsExactlyAsync<PlatformException>(() => fixture.Platform.SetGroupEssenceMessageAsync(
            ProtocolTestFixture.GroupId, message.Seq, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId));
        var forward = new ForwardSegment("anonymous-forward", [new ForwardNode(message.SenderId,
            "private real name", message.Content, message.Id, message.Time)]);
        await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId, [forward]);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var found = await protocol.HandleAsync(new ProtocolCall("get_forwarded_messages",
            new JsonObject { ["forward_id"] = forward.Id }));
        Assert.IsTrue(found.IsSuccess, found.Message);
        var node = found.Data!["messages"]!.AsArray().Single()!;
        Assert.AreEqual(message.Anonymous!.Name, node["sender_name"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, node["avatar_url"]!.GetValue<string>());
        Assert.DoesNotContain("private real name", found.EffectiveData.ToJsonString());
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, found.EffectiveData.ToJsonString());
    }

    private static ProtocolCall Call(string action, long? sequence = null)
    {
        var parameters = new JsonObject { ["message_scene"] = "group", ["peer_id"] = ProtocolTestFixture.GroupId };
        if (sequence is not null) parameters["message_seq"] = sequence;
        return new ProtocolCall(action, parameters);
    }

    private static async Task<Message> AnonymousAsync(ProtocolTestFixture fixture)
    {
        await fixture.Platform.SetGroupAnonymousAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        return await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId,
            [new AnonymousSegment(), new TextSegment("private anonymous content")]);
    }
}
