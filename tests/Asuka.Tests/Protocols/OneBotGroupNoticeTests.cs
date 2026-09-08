using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Contracts: https://github.com/botuniverse/onebot-11/blob/master/event/notice.md
[TestClass]
public sealed class OneBotGroupNoticeTests
{
    private static readonly string[] UploadFields = ["time", "self_id", "post_type", "notice_type", "group_id", "user_id", "file"];
    private static readonly string[] FileFields = ["id", "name", "size", "busid"];
    private static readonly string[] PokeFields = ["time", "self_id", "post_type", "notice_type", "sub_type", "group_id", "user_id", "target_id"];

    [TestMethod]
    public async Task CachedGroupUploadPublishesCommittedFileIdentityAndOfficialV11Fields()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var bytes = Encoding.UTF8.GetBytes("群文件 · uploaded by Alice\n");
        await using var source = new MemoryStream(bytes);
        var asset = await fixture.Assets.StoreAsync(source, "群聊笔记.txt", "text/plain");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        var shared = await fixture.Platform.ShareGroupFileAsync(
            ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, asset, cancellationToken: timeout.Token);

        Assert.IsTrue(await next);
        var domainEvent = events.Current;
        Assert.IsInstanceOfType<GroupFileUploadedEvent>(domainEvent.Payload);
        Assert.AreEqual(ProtocolTestFixture.SelfId, domainEvent.SelfId);
        Assert.AreEqual(shared, await fixture.Store.GetGroupFileAsync(ProtocolTestFixture.GroupId, shared.Id));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.Assets.LocationOf(asset.Id), timeout.Token));

        var v11 = Protocol(fixture, OneBotVersion.V11);
        var frames = await v11.EncodeAsync(domainEvent, timeout.Token);
        Assert.HasCount(1, frames);
        var wire = JsonNode.Parse(frames[0].Payload.ToJsonString())!.AsObject();
        CollectionAssert.AreEquivalent(UploadFields,
            wire.Select(property => property.Key).ToArray());
        Assert.AreEqual(domainEvent.Time.ToUnixTimeSeconds(), wire["time"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, wire["self_id"]!.GetValue<long>());
        Assert.AreEqual("notice", wire["post_type"]!.GetValue<string>());
        Assert.AreEqual("group_upload", wire["notice_type"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, wire["group_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, wire["user_id"]!.GetValue<long>());
        var file = wire["file"]!.AsObject();
        CollectionAssert.AreEquivalent(FileFields, file.Select(property => property.Key).ToArray());
        Assert.AreEqual(shared.Id, file["id"]!.GetValue<string>());
        Assert.AreNotEqual(asset.Id, file["id"]!.GetValue<string>());
        Assert.AreEqual("群聊笔记.txt", file["name"]!.GetValue<string>());
        Assert.AreEqual(bytes.LongLength, file["size"]!.GetValue<long>());
        Assert.AreEqual(0L, file["busid"]!.GetValue<long>());
        Assert.IsEmpty(await Protocol(fixture, OneBotVersion.V12).EncodeAsync(domainEvent, timeout.Token));
        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task NativeGroupNudgePublishesOnlyTheOfficialV11PokeNotice()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        await fixture.Platform.PokeAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, timeout.Token);

        Assert.IsTrue(await next);
        var domainEvent = events.Current;
        Assert.IsInstanceOfType<PokeEvent>(domainEvent.Payload);
        var frames = await Protocol(fixture, OneBotVersion.V11).EncodeAsync(domainEvent, timeout.Token);
        Assert.HasCount(1, frames);
        var wire = JsonNode.Parse(frames[0].Payload.ToJsonString())!.AsObject();
        CollectionAssert.AreEquivalent(PokeFields,
            wire.Select(property => property.Key).ToArray());
        Assert.AreEqual(domainEvent.Time.ToUnixTimeSeconds(), wire["time"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, wire["self_id"]!.GetValue<long>());
        Assert.AreEqual("notice", wire["post_type"]!.GetValue<string>());
        Assert.AreEqual("notify", wire["notice_type"]!.GetValue<string>());
        Assert.AreEqual("poke", wire["sub_type"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, wire["group_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, wire["user_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, wire["target_id"]!.GetValue<long>());
        Assert.IsEmpty(await Protocol(fixture, OneBotVersion.V12).EncodeAsync(domainEvent, timeout.Token));
    }

    [TestMethod]
    public async Task NoticeSupportDoesNotInventV11UploadOrNudgeApis()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture, OneBotVersion.V11);
        foreach (var action in new[] { "upload_group_file", "send_group_nudge", "send_friend_nudge" })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall(action, new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["user_id"] = ProtocolTestFixture.SenderId,
            }));
            Assert.AreEqual(1404, reply.RetCode, action);
        }
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture, OneBotVersion version) =>
        new(version, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
}
