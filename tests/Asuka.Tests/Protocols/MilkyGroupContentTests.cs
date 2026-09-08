using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// https://milky.ntqqrev.org/api/group and /struct/Group{AnnouncementEntity,EssenceMessage}.
[TestClass]
public sealed class MilkyGroupContentTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [TestMethod]
    public async Task AnnouncementsPersistTheirOfficialFieldsAndDeleteWithinTheGroup()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Create(fixture);
        var sent = await protocol.HandleAsync(Call("send_group_announcement", ("content", "Release notes"), ("image_uri", "base64://" + Pixel)));
        Assert.IsTrue(sent.IsSuccess, sent.Message);
        Assert.AreEqual("{}", sent.EffectiveData.ToJsonString());
        var listed = await protocol.HandleAsync(Call("get_group_announcements"));
        Assert.IsTrue(listed.IsSuccess, listed.Message);
        var announcement = listed.Data!["announcements"]!.AsArray().Single()!;
        Assert.AreEqual(500000001L, announcement["group_id"]!.GetValue<long>());
        Assert.AreEqual(1000000001L, announcement["user_id"]!.GetValue<long>());
        Assert.AreEqual("Release notes", announcement["content"]!.GetValue<string>());
        Assert.IsGreaterThan(1_000_000_000L, announcement["time"]!.GetValue<long>());
        Assert.IsTrue(announcement["image_url"]!.GetValue<string>().StartsWith("http://", StringComparison.Ordinal));
        var id = announcement["announcement_id"]!.GetValue<string>();
        var wrongGroup = Call("delete_group_announcement", ("announcement_id", id));
        wrongGroup.Parameters["group_id"] = "999";
        Assert.IsFalse((await protocol.HandleAsync(wrongGroup)).IsSuccess);
        Assert.IsTrue((await protocol.HandleAsync(Call("delete_group_announcement", ("announcement_id", id)))).IsSuccess);
        Assert.HasCount(0, (await protocol.HandleAsync(Call("get_group_announcements"))).Data!["announcements"]!.AsArray());
    }

    [TestMethod]
    public async Task ImageOnlyAnnouncementsPreserveTheRequiredEmptyStringContent()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var reply = await Create(fixture).HandleAsync(Call("send_group_announcement", ("content", ""), ("image_uri", "base64://" + Pixel)));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        var announcement = (await fixture.Store.GetGroupAnnouncementsAsync(ProtocolTestFixture.GroupId)).Single();
        Assert.AreEqual("", announcement.Content);
        Assert.IsNotNull(announcement.Image);
    }

    [TestMethod]
    public async Task AvatarIsValidatedAndCachedAndAllGroupMutationsRequireAuthority()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Create(fixture);
        Assert.IsTrue((await protocol.HandleAsync(Call("set_group_avatar", ("image_uri", "base64://" + Pixel)))).IsSuccess);
        var avatar = (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Avatar;
        Assert.IsTrue(Uri.TryCreate(avatar, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath));
        Assert.IsFalse((await protocol.HandleAsync(Call("set_group_avatar", ("image_uri", "base64://bm90IGFuIGltYWdl")))).IsSuccess);
        Assert.AreEqual(avatar, (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Avatar);
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId));
        foreach (var action in new[] { "send_group_announcement", "set_group_avatar", "delete_group_announcement", "set_group_essence_message" })
        {
            var call = Call(action, ("content", "blocked"), ("image_uri", "base64://" + Pixel), ("announcement_id", "missing"));
            call.Parameters["message_seq"] = 1;
            Assert.IsFalse((await protocol.HandleAsync(call)).IsSuccess, action);
        }
        Assert.HasCount(0, await fixture.Store.GetGroupAnnouncementsAsync(ProtocolTestFixture.GroupId));
    }

    [TestMethod]
    public async Task EssenceIncludesOfficialMessageMetadataAndPaginatesWithoutDuplicates()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Create(fixture);
        for (var index = 0; index < 3; index++)
        {
            var message = await SendAsync(fixture, "Message " + index);
            var set = Call("set_group_essence_message");
            set.Parameters["message_seq"] = message.Seq;
            Assert.IsTrue((await protocol.HandleAsync(set)).IsSuccess);
        }
        var first = await protocol.HandleAsync(Page(0, 2));
        Assert.IsTrue(first.IsSuccess, first.Message);
        Assert.HasCount(2, first.Data!["messages"]!.AsArray());
        Assert.IsFalse(first.Data!["is_end"]!.GetValue<bool>());
        var last = await protocol.HandleAsync(Page(1, 2));
        Assert.HasCount(1, last.Data!["messages"]!.AsArray());
        Assert.IsTrue(last.Data!["is_end"]!.GetValue<bool>());
        var beyond = await protocol.HandleAsync(Page(int.MaxValue, int.MaxValue));
        Assert.HasCount(0, beyond.Data!["messages"]!.AsArray());
        Assert.IsTrue(beyond.Data!["is_end"]!.GetValue<bool>());
        var item = first.Data!["messages"]![0]!;
        Assert.AreEqual("Alice Card", item["sender_name"]!.GetValue<string>());
        Assert.AreEqual("Asuka Bot", item["operator_name"]!.GetValue<string>());
        Assert.AreEqual(1000000002L, item["sender_id"]!.GetValue<long>());
        Assert.AreEqual(1000000001L, item["operator_id"]!.GetValue<long>());
        Assert.AreEqual(500000001L, item["group_id"]!.GetValue<long>());
        Assert.IsGreaterThanOrEqualTo(item["message_time"]!.GetValue<long>(), item["operation_time"]!.GetValue<long>());
        Assert.AreEqual("text", item["segments"]![0]!["type"]!.GetValue<string>());
        Assert.AreEqual(3, first.Data!["messages"]!.AsArray().Concat(last.Data!["messages"]!.AsArray())
            .Select(node => node!["message_seq"]!.GetValue<long>()).Distinct().Count());
    }

    [TestMethod]
    public async Task EssenceEventsOnlyDescribeActualChangesAndUseTheOfficialShape()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var message = await SendAsync(fixture, "Pin me");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator();
        var next = events.MoveNextAsync().AsTask();
        await fixture.Platform.SetGroupEssenceMessageAsync(ProtocolTestFixture.GroupId, message.Seq,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
        Assert.IsTrue(await next);
        var frames = await Create(fixture).EncodeAsync(events.Current);
        Assert.HasCount(1, frames);
        Assert.AreEqual("group_essence_message_change", frames[0].Payload["event_type"]!.GetValue<string>());
        Assert.AreEqual(message.Seq, frames[0].Payload["data"]!["message_seq"]!.GetValue<long>());
        Assert.AreEqual(1000000002L, frames[0].Payload["data"]!["operator_id"]!.GetValue<long>());
        Assert.IsTrue(frames[0].Payload["data"]!["is_set"]!.GetValue<bool>());
        next = events.MoveNextAsync().AsTask();
        await fixture.Platform.SetGroupEssenceMessageAsync(ProtocolTestFixture.GroupId, message.Seq,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
        await fixture.Platform.SetGroupEssenceMessageAsync(ProtocolTestFixture.GroupId, message.Seq,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId, false);
        Assert.IsTrue(await next);
        Assert.IsFalse(((GroupEssenceMessageChangedEvent)events.Current.Payload).IsSet);
    }

    [TestMethod]
    public async Task RecallHistoryDeletionAndAccountScopingCannotExposeStaleEssence()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var first = await SendAsync(fixture, "Recall me");
        await fixture.Platform.SetGroupEssenceMessageAsync(ProtocolTestFixture.GroupId, first.Seq, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId);
        var other = new MilkyProtocol(ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        Assert.HasCount(0, (await other.HandleAsync(Page(0, 10))).Data!["messages"]!.AsArray());
        await fixture.Platform.RecallMessageAsync(first.Id, ProtocolTestFixture.SenderId);
        Assert.HasCount(0, (await Create(fixture).HandleAsync(Page(0, 10))).Data!["messages"]!.AsArray());
        var invalidSet = Call("set_group_essence_message");
        invalidSet.Parameters["message_seq"] = first.Seq;
        Assert.IsFalse((await Create(fixture).HandleAsync(invalidSet)).IsSuccess);
        var second = await SendAsync(fixture, "Delete me");
        await fixture.Platform.SetGroupEssenceMessageAsync(ProtocolTestFixture.GroupId, second.Seq, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId);
        await fixture.Platform.ClearMessageHistoryAsync(second.Chat);
        Assert.HasCount(0, (await Create(fixture).HandleAsync(Page(0, 10))).Data!["messages"]!.AsArray());
    }

    [TestMethod]
    public async Task InvalidFieldsAndNonmembersAreRejectedWithoutMutation()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Create(fixture);
        foreach (var call in new[] { Page(-1, 2), Page(0, 0), Call("get_group_essence_messages"),
            Call("send_group_announcement"), Call("send_group_announcement", ("content", " ")) })
            Assert.IsFalse((await protocol.HandleAsync(call)).IsSuccess, call.Name);
        var message = await SendAsync(fixture, "Original");
        var badBoolean = Call("set_group_essence_message", ("is_set", "true"));
        badBoolean.Parameters["message_seq"] = message.Seq;
        Assert.IsFalse((await protocol.HandleAsync(badBoolean)).IsSuccess);
        var badPage = Page(0, 2);
        badPage.Parameters["page_index"] = "0";
        Assert.IsFalse((await protocol.HandleAsync(badPage)).IsSuccess);
        await fixture.Store.RemoveMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        Assert.IsFalse((await protocol.HandleAsync(Call("get_group_announcements"))).IsSuccess);
        Assert.IsFalse((await protocol.HandleAsync(Page(0, 2))).IsSuccess);
    }

    private static MilkyProtocol Create(ProtocolTestFixture fixture) => new(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
    private static Task<Message> SendAsync(ProtocolTestFixture fixture, string text) => fixture.Platform.SendMessageAsync(
        ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment(text)]);
    private static ProtocolCall Call(string name, params (string Key, string Value)[] fields)
    {
        var parameters = new JsonObject { ["group_id"] = 500000001L };
        foreach (var (key, value) in fields) parameters[key] = value;
        return new ProtocolCall(name, parameters);
    }
    private static ProtocolCall Page(int index, int size)
    {
        var call = Call("get_group_essence_messages");
        call.Parameters["page_index"] = index;
        call.Parameters["page_size"] = size;
        return call;
    }
}
