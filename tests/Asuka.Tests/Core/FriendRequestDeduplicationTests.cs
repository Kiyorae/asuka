using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class FriendRequestDeduplicationTests
{
    [TestMethod]
    public async Task AcceptingOneLegacyRequestIgnoresDuplicatePendingRequestsEvenAfterUnfriending()
    {
        await using var store = new AsukaStore();
        await store.SaveAsync(new User("Requester", id: "10001"));
        await store.SaveAsync(new User("Bot", id: "10002"));
        await using var platform = new PlatformService(store);
        var accepted = new PendingRequest(RequestKind.Friend, "10001", "10002");
        var duplicate = new PendingRequest(RequestKind.Friend, "10001", "10002", isFiltered: true);
        var reverse = new PendingRequest(RequestKind.Friend, "10002", "10001");
        var historical = new PendingRequest(RequestKind.Friend, "10001", "10002",
            resolution: RequestResolution.Rejected("keep old history"));
        await store.SaveAsync(accepted);
        await store.SaveAsync(duplicate);
        await store.SaveAsync(reverse);
        await store.SaveAsync(historical);
        await platform.ResolveRequestAsync(accepted.Flag, true);
        Assert.AreEqual(RequestResolutionStatus.Ignored, (await store.GetRequestAsync(duplicate.Id))!.Resolution!.Status);
        Assert.AreEqual(RequestResolutionStatus.Ignored, (await store.GetRequestAsync(reverse.Id))!.Resolution!.Status);
        Assert.AreEqual(historical.Resolution, (await store.GetRequestAsync(historical.Id))!.Resolution);

        await platform.RemoveFriendAsync("10002", "10001");
        var stale = await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveRequestAsync(duplicate.Flag, true));
        Assert.AreEqual(PlatformError.NotPermitted, stale.Error);
        Assert.IsNull(await store.GetFriendshipAsync("10002", "10001"));
        var fresh = await platform.RequestFriendAsync("10001", "10002");
        Assert.IsNull(fresh.Resolution);
        Assert.AreNotEqual(duplicate.Id, fresh.Id);
    }

    [TestMethod]
    public async Task ConcurrentRequestsForTheSamePairCreateOnlyOnePendingRecordAcrossServices()
    {
        await using var store = new AsukaStore();
        await store.SaveAsync(new User("Requester", id: "10001"));
        await store.SaveAsync(new User("Bot", id: "10002"));
        await using var firstService = new PlatformService(store);
        await using var secondService = new PlatformService(store);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                var service = index % 2 == 0 ? firstService : secondService;
                await service.RequestFriendAsync("10001", "10002", isFiltered: index % 2 == 0);
                return true;
            }
            catch (PlatformException error) when (error.Error == PlatformError.AlreadyExists) { return false; }
        }));
        Assert.AreEqual(1, outcomes.Count(success => success));
        var pending = (await store.GetPendingRequestsAsync("10002", RequestKind.Friend)).Single();
        var reverse = await Assert.ThrowsAsync<PlatformException>(() => secondService.RequestFriendAsync("10002", "10001"));
        Assert.AreEqual(PlatformError.AlreadyExists, reverse.Error);

        await firstService.ResolveRequestAsync(pending.Flag, false, "first request rejected");
        var retry = await secondService.RequestFriendAsync("10001", "10002");
        Assert.AreNotEqual(pending.Id, retry.Id);
        Assert.AreEqual(RequestResolution.Rejected("first request rejected"),
            (await store.GetRequestAsync(pending.Id))!.Resolution);
        Assert.HasCount(2, await store.GetRequestsAsync("10002", RequestKind.Friend));
    }

    [TestMethod]
    public async Task ImportedPendingRequestCannotOverwriteAnExistingFriendshipOrItsRemark()
    {
        await using var store = new AsukaStore();
        await store.SaveAsync(new User("Requester", id: "10001"));
        await store.SaveAsync(new User("Bot", id: "10002"));
        await using var platform = new PlatformService(store);
        await platform.AddFriendshipAsync("10002", "10001", "keep this remark");
        var friendship = (await store.GetFriendshipAsync("10002", "10001"))!;
        var imported = new PendingRequest(RequestKind.Friend, "10001", "10002");
        await store.SaveAsync(imported);
        var error = await Assert.ThrowsAsync<PlatformException>(() =>
            platform.ResolveRequestAsync(imported.Flag, true, remark: "must not overwrite"));
        Assert.AreEqual(PlatformError.AlreadyExists, error.Error);
        Assert.AreEqual(friendship, await store.GetFriendshipAsync("10002", "10001"));
        Assert.IsNull((await store.GetRequestAsync(imported.Id))!.Resolution);
    }
}
