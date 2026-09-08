using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class PrivateFileListingTests
{
    private const string Alice = "10001";
    private const string Bob = "10002";
    private const string Carol = "10003";
    private static readonly Asset Document = new("private-document", "shared.txt", "text/plain", 12);

    [TestMethod]
    public async Task ListingContainsOnlyBothDirectionsOfThePairAndHasStableNewestFirstOrdering()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var time = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.SaveSharedFileAsync(Share("old", Alice, Bob, time.AddMinutes(-1)), default);
        await store.SaveSharedFileAsync(Share("a", Bob, Alice, time), default);
        await store.SaveSharedFileAsync(Share("b", Alice, Bob, time), default);
        await store.SaveSharedFileAsync(Share("foreign-in", Carol, Alice, time), default);
        await store.SaveSharedFileAsync(Share("foreign-out", Bob, Carol, time), default);
        await store.SaveAsync(new Group("Group", id: "50001"));
        await store.SaveSharedFileAsync(new SharedFile("group-file", "50001", null, Alice,
            Document, Document.Name, "/", time), default);

        var listing = await platform.GetPrivateFileListingAsync(Alice, Bob);
        CollectionAssert.AreEqual((string[])["b", "a", "old"], listing.Select(file => file.Id).ToArray());
        CollectionAssert.AreEqual(listing.ToArray(), (await platform.GetPrivateFileListingAsync(Bob, Alice)).ToArray());
        CollectionAssert.AreEqual(listing.ToArray(), (await platform.GetPrivateFileListingAsync(Alice, Bob)).ToArray());
        Assert.IsTrue(listing.All(file => file.GroupId is null));
        Assert.HasCount(1, await platform.GetPrivateFileListingAsync(Alice, Carol));
    }

    [TestMethod]
    public async Task RemovedFriendshipDoesNotRemovePrivateFileHistoryOrParticipantAccess()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.AddFriendshipAsync(Alice, Bob);
        var file = await platform.SharePrivateFileAsync(Bob, Alice, Document, new string('a', 40));
        await platform.RemoveFriendAsync(Alice, Bob);

        Assert.IsNull(await store.GetFriendshipAsync(Alice, Bob));
        Assert.AreEqual(file.Id, (await platform.GetPrivateFileListingAsync(Alice, Bob)).Single().Id);
        Assert.AreEqual(file.Id, (await platform.GetPrivateFileListingAsync(Bob, Alice)).Single().Id);
        Assert.AreEqual(file.Id, (await platform.GetSharedFileForDownloadAsync(file.Id, Bob)).Id);
    }

    [TestMethod]
    public async Task ExpiredSharesRemainVisibleButDownloadsAndThirdPartyAccessAreRejected()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var expired = Share("expired", Alice, Bob, DateTimeOffset.UtcNow.AddDays(-2))
            with
        { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var active = Share("active", Bob, Alice, DateTimeOffset.UtcNow);
        await store.SaveSharedFileAsync(expired, default);
        await store.SaveSharedFileAsync(active, default);

        var listing = await platform.GetPrivateFileListingAsync(Alice, Bob);
        Assert.HasCount(2, listing);
        Assert.IsTrue(listing.Single(file => file.Id == expired.Id).IsExpired);
        var expirationError = await Assert.ThrowsAsync<PlatformException>(() =>
            platform.GetSharedFileForDownloadAsync(expired.Id, Bob));
        Assert.AreEqual(PlatformError.FileNotFound, expirationError.Error);
        var outsiderError = await Assert.ThrowsAsync<PlatformException>(() =>
            platform.GetSharedFileForDownloadAsync(active.Id, Carol));
        Assert.AreEqual(PlatformError.FileNotFound, outsiderError.Error);
        Assert.IsEmpty(await platform.GetPrivateFileListingAsync(Carol, Bob));
        Assert.HasCount(1, await store.GetPrivateFilesAsync(Alice, Bob));
    }

    [TestMethod]
    public async Task ListingRequiresTwoExistingParticipantsAndHonorsCancellation()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var same = await Assert.ThrowsAsync<PlatformException>(() => platform.GetPrivateFileListingAsync(Alice, Alice));
        Assert.AreEqual(PlatformError.InvalidParameter, same.Error);
        var missingUser = await Assert.ThrowsAsync<PlatformException>(() => platform.GetPrivateFileListingAsync("missing", Bob));
        Assert.AreEqual(PlatformError.UserNotFound, missingUser.Error);
        var missingPeer = await Assert.ThrowsAsync<PlatformException>(() => platform.GetPrivateFileListingAsync(Alice, "missing"));
        Assert.AreEqual(PlatformError.UserNotFound, missingPeer.Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.GetPrivateFileListingAsync(Alice, Bob, cancelled.Token));
    }

    private static SharedFile Share(string id, string sender, string recipient, DateTimeOffset time) =>
        new(id, null, recipient, sender, Document, Document.Name, "/", time, FileHash: new string('a', 40));

    private static async Task SeedAsync(AsukaStore store)
    {
        await store.SaveAsync(new User("Alice", id: Alice));
        await store.SaveAsync(new User("Bob", id: Bob));
        await store.SaveAsync(new User("Carol", id: Carol));
        await store.SaveAsync(Document);
    }
}
