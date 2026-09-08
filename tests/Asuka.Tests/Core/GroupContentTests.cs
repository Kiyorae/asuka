using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class GroupContentTests
{
    [TestMethod]
    public async Task GroupContentSurvivesReopenAndDeletedGroupsCascade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "asuka-group-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "state.sqlite3");
        try
        {
            string announcementId;
            string messageId;
            await using (var store = new AsukaStore(database))
            await using (var platform = new PlatformService(store))
            {
                await SeedAsync(store);
                var image = new Asset("test-image", "image.png", ByteCount: 5, Source: AssetSource.Inline);
                var announcement = await platform.SendGroupAnnouncementAsync("500", "101", "Welcome", image);
                announcementId = announcement.Id;
                var message = await platform.SendMessageAsync(ChatScene.Group, "500", "102", "101", [new TextSegment("Essential")]);
                messageId = message.Id;
                await platform.SetGroupEssenceMessageAsync("500", message.Seq, "101", "101");
                await platform.SetGroupAvatarAsync("500", "101", "file:///C:/cached/image.png");
            }
            await using (var reopened = new AsukaStore(database))
            {
                var announcement = (await reopened.GetGroupAnnouncementsAsync("500")).Single();
                Assert.AreEqual(announcementId, announcement.Id);
                Assert.AreEqual("test-image", announcement.Image!.Id);
                Assert.AreEqual("Welcome", announcement.Content);
                Assert.AreEqual("file:///C:/cached/image.png", (await reopened.GetGroupAsync("500"))!.Avatar);
                var page = await reopened.GetGroupEssenceMessagesAsync("500", "101", 0, 20);
                Assert.AreEqual(messageId, page.Messages.Single().Message.Id);
                Assert.AreEqual("Essential", page.Messages[0].Message.Content.PlainText());
                Assert.IsTrue(page.IsEnd);
                await reopened.DeleteGroupAsync("500");
                Assert.HasCount(0, await reopened.GetGroupAnnouncementsAsync("500"));
                Assert.HasCount(0, (await reopened.GetGroupEssenceMessagesAsync("500", "101", 0, 20)).Messages);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ConcurrentDuplicateSetsPreserveFirstOperatorAndPreventPaginationDuplication()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        var message = await platform.SendMessageAsync(ChatScene.Group, "500", "102", "101", [new TextSegment("Stable")]);
        await platform.SetGroupEssenceMessageAsync("500", message.Seq, "101", "102");
        var first = (await store.GetGroupEssenceMessagesAsync("500", "101", 0, 20)).Messages.Single();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => platform.SetGroupEssenceMessageAsync("500", message.Seq, "101", "101")));
        var after = (await store.GetGroupEssenceMessagesAsync("500", "101", 0, 20)).Messages.Single();
        Assert.AreEqual(first.OperatorId, after.OperatorId);
        Assert.AreEqual(first.OperationTime, after.OperationTime);
        Assert.AreEqual(first.Message.Id, after.Message.Id);
        await store.SaveAsync(message with { RecalledAt = DateTimeOffset.UtcNow, RecalledBy = "102" });
        Assert.HasCount(0, (await store.GetGroupEssenceMessagesAsync("500", "101", 0, 20)).Messages);
        await store.SaveAsync(message);
        Assert.HasCount(0, (await store.GetGroupEssenceMessagesAsync("500", "101", 0, 20)).Messages,
            "An imported unrecalled version must not resurrect removed essence state.");
    }

    [TestMethod]
    public async Task RecallPublishesEssenceRemovalOnceAfterCommittedCleanup()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        var message = await platform.SendMessageAsync(ChatScene.Group, "500", "102", "101", [new TextSegment("Essential")]);
        await platform.SetGroupEssenceMessageAsync("500", message.Seq, "101", "101");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => platform.RecallMessageAsync(message.Id, "102", timeout.Token)));
        Assert.IsTrue(await next);
        Assert.IsInstanceOfType<MessageRecalledEvent>(events.Current.Payload);
        Assert.IsFalse(await store.IsGroupEssenceMessageAsync(message.Id));
        Assert.IsTrue((await store.GetMessageAsync(message.Id))!.IsRecalled);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(new GroupEssenceMessageChangedEvent("500", message.Seq, "102", false), events.Current.Payload);

        await platform.SendMessageAsync(ChatScene.Group, "500", "102", "101", [new TextSegment("Next")]);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<MessageEvent>(events.Current.Payload);
    }

    private static async Task SeedAsync(AsukaStore store)
    {
        await store.SaveAsync(new User("Owner", id: "101"));
        await store.SaveAsync(new User("Admin", id: "102"));
        await store.SaveAsync(new Group("Group", id: "500"));
        await store.SaveAsync(new GroupMember("500", "101", role: GroupRole.Owner));
        await store.SaveAsync(new GroupMember("500", "102", role: GroupRole.Admin));
    }
}
