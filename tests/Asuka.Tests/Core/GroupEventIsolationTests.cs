using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class GroupEventIsolationTests
{
    private static readonly string[] ExpectedRemovalRecipients = ["201", "202"];

    [TestMethod]
    public async Task GroupsWithoutBotMembersNeverBroadcastToUnrelatedAccounts()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        platform.RegisterBot("201");
        platform.RegisterBot("202");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        await platform.SetGroupNameAsync("500", "101", "Private group");
        await platform.SetAdminAsync("500", "102", "101", true);
        await platform.SetWholeMuteAsync("500", "101", true);
        await platform.AddMemberAsync("500", "103", "101");
        await platform.RemoveMemberAsync("500", "103", "101", GroupMemberChangeReason.Administrative);
        var denied = await Assert.ThrowsAsync<PlatformException>(() => platform.SendMessageAsync(
            ChatScene.Group, "500", "101", "201", [new TextSegment("Must not escape the group")], timeout.Token));
        Assert.AreEqual(PlatformError.NotAMember, denied.Error);
        var request = await platform.RequestFriendAsync("101", "201", cancellationToken: timeout.Token);

        Assert.IsTrue(await next);
        Assert.AreEqual("201", events.Current.SelfId);
        Assert.AreEqual(request.Id, ((RequestReceivedEvent)events.Current.Payload).Request.Id);
    }

    [TestMethod]
    public async Task RemovalNotifiesTheRemovedBotAndCurrentMembersAfterCommitOnly()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        await store.SaveAsync(new GroupMember("500", "201"));
        await store.SaveAsync(new GroupMember("500", "202"));
        foreach (var id in new[] { "201", "202", "203" }) platform.RegisterBot(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        await platform.RemoveMemberAsync("500", "201", "101", GroupMemberChangeReason.Administrative, timeout.Token);
        Assert.IsTrue(await next);
        Assert.IsNull(await store.GetMemberAsync("500", "201", timeout.Token));
        var recipients = new HashSet<string>(StringComparer.Ordinal) { events.Current.SelfId };
        Assert.IsInstanceOfType<GroupMemberRemovedEvent>(events.Current.Payload);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<GroupMemberRemovedEvent>(events.Current.Payload);
        recipients.Add(events.Current.SelfId);
        CollectionAssert.AreEquivalent(ExpectedRemovalRecipients, recipients.ToArray());

        await platform.RequestFriendAsync("101", "203", cancellationToken: timeout.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<RequestReceivedEvent>(events.Current.Payload);
    }

    private static async Task SeedAsync(AsukaStore store)
    {
        foreach (var id in new[] { "101", "102", "103", "201", "202", "203" })
            await store.SaveAsync(new User(id, id: id));
        await store.SaveAsync(new Group("Group", id: "500"));
        await store.SaveAsync(new GroupMember("500", "101", role: GroupRole.Owner));
        await store.SaveAsync(new GroupMember("500", "102"));
    }
}
