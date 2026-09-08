using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class RequestPageTests
{
    [TestMethod]
    public async Task FriendHistoryFiltersBeforeLimitingAndIncludesResolvedRequests()
    {
        await using var store = new AsukaStore();
        var older = new PendingRequest(RequestKind.Friend, "101", "201", time: DateTimeOffset.UtcNow.AddDays(-1));
        await store.SaveAsync(older);
        await store.ResolveRequestAsync(older.Id, RequestResolution.Accepted);
        var newest = new PendingRequest(RequestKind.Friend, "102", "201");
        await store.SaveAsync(newest);
        await store.SaveAsync(new PendingRequest(RequestKind.Friend, "103", "201", isFiltered: true));
        await store.SaveAsync(new PendingRequest(RequestKind.Friend, "104", "202"));
        await store.SaveAsync(new PendingRequest(RequestKind.GroupJoin, "105", "201", "500"));

        Assert.AreEqual(newest.Id, (await store.GetFriendRequestHistoryAsync("201", false, 1)).Single().Id);
        var history = await store.GetFriendRequestHistoryAsync("201", false, 2);
        CollectionAssert.AreEqual(new[] { newest.Id, older.Id }, history.Select(request => request.Id).ToArray());
        Assert.AreEqual(RequestResolutionStatus.Accepted, history[1].Resolution!.Status);
        Assert.HasCount(1, await store.GetFriendRequestHistoryAsync("201", true, 10));
    }

    [TestMethod]
    public async Task GroupRequestCursorUsesInclusiveNextSequenceWithoutGapsOrAccountLeaks()
    {
        await using var store = new AsukaStore();
        var expected = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var request = new PendingRequest(index % 2 == 0 ? RequestKind.GroupJoin : RequestKind.GroupInvitedJoin,
                "101", "201", "500", targetUserId: "102");
            await store.SaveAsync(request);
            expected.Insert(0, request.Id);
            await store.SaveAsync(new PendingRequest(RequestKind.GroupJoin, "101", "202", "500"));
            await store.SaveAsync(new PendingRequest(RequestKind.GroupJoin, "101", "201", "500", isFiltered: true));
        }
        var first = await store.GetGroupRequestNotificationPageAsync("201", false, null, 2);
        Assert.IsNotNull(first.NextSequence);
        var second = await store.GetGroupRequestNotificationPageAsync("201", false, first.NextSequence, 2);
        Assert.IsNotNull(second.NextSequence);
        var third = await store.GetGroupRequestNotificationPageAsync("201", false, second.NextSequence, 2);
        Assert.IsNull(third.NextSequence);
        CollectionAssert.AreEqual(expected.ToArray(), first.Requests.Concat(second.Requests).Concat(third.Requests)
            .Select(request => request.Id).ToArray());
        var unlimited = await store.GetGroupRequestNotificationPageAsync("201", false, null, int.MaxValue);
        Assert.HasCount(5, unlimited.Requests);
        Assert.IsNull(unlimited.NextSequence);
    }
}
