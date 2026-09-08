using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class BotPresenceTests
{
    [TestMethod]
    public async Task RegistrationDefaultsOnlineAndRepeatedRegistrationPreservesAnOfflineAccount()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        Assert.IsTrue(platform.IsBotOnline("100"));
        Assert.IsTrue(platform.IsBotOnline("200"));
        Assert.IsFalse(platform.IsBotOnline("300"));
        Assert.IsNull(platform.GetBotPresence("300"));

        Assert.IsTrue(await platform.SetBotPresenceAsync("100", false, "Signed out locally"));
        var offline = platform.GetBotPresence("100");
        platform.RegisterBot("100");
        Assert.AreEqual(offline, platform.GetBotPresence("100"));
        Assert.IsFalse(await platform.SetBotPresenceAsync("100", false, "A duplicate request"));
        Assert.AreEqual(offline, platform.GetBotPresence("100"));
        Assert.IsTrue(platform.IsBotOnline("200"));
        platform.SetRegisteredBot("100");
        Assert.AreEqual(offline, platform.GetBotPresence("100"));
        Assert.IsNull(platform.GetBotPresence("200"));
        platform.UnregisterBot("100");
        Assert.IsNull(platform.GetBotPresence("100"));
        platform.RegisterBot("100");
        Assert.IsTrue(platform.IsBotOnline("100"));
    }

    [TestMethod]
    public async Task PresenceTransitionsAreDeliveredOnceEvenWhenSelfEchoIsDisabledAndAnObserverThrows()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = events.MoveNextAsync().AsTask();
        var observed = new List<BotPresence>();
        platform.BotPresenceChanged += (_, _) => throw new InvalidOperationException("Broken observer");
        platform.BotPresenceChanged += (_, presence) => observed.Add(presence);

        Assert.IsTrue(await platform.SetBotPresenceAsync("100", false, "Local logout", timeout.Token));
        Assert.IsTrue(await first);
        Assert.AreEqual("100", events.Current.SelfId);
        var offline = ((BotPresenceChangedEvent)events.Current.Payload).Presence;
        Assert.IsFalse(offline.IsOnline);
        Assert.AreEqual("Local logout", offline.Reason);
        Assert.AreEqual(offline.ChangedAt, events.Current.Time);
        Assert.IsFalse(await platform.SetBotPresenceAsync("100", false, "Duplicate", timeout.Token));
        Assert.IsTrue(await platform.SetBotPresenceAsync("100", true, cancellationToken: timeout.Token));
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsTrue(((BotPresenceChangedEvent)events.Current.Payload).Presence.IsOnline);
        Assert.AreEqual(string.Empty, ((BotPresenceChangedEvent)events.Current.Payload).Presence.Reason);
        Assert.HasCount(2, observed);
        Assert.IsTrue(platform.IsBotOnline("100"));
    }

    [TestMethod]
    public async Task OfflineRecipientKeepsNativeMessagesWithoutEventsAndOtherAccountsRemainUsable()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        await platform.SetBotPresenceAsync("100", false, "Local logout");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        var retained = await platform.SendMessageAsync(ChatScene.Friend, "300", "300", "100",
            [new TextSegment("Received while offline")], timeout.Token);
        // The offline bot's persona may still be used as a native simulated peer
        // when the recipient is another online bot account.
        var delivered = await platform.SendMessageAsync(ChatScene.Friend, "100", "100", "200",
            [new TextSegment("Another account is still online")], timeout.Token);
        Assert.IsTrue(await next);
        Assert.AreEqual("200", events.Current.SelfId);
        Assert.AreEqual(delivered.Id, ((MessageEvent)events.Current.Payload).Message.Id);
        Assert.IsNotNull(await store.GetMessageAsync(retained.Id, timeout.Token));
        Assert.IsNotNull(await store.GetUserAsync("100", timeout.Token));
        await platform.SetBotPresenceAsync("100", true, cancellationToken: timeout.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<BotPresenceChangedEvent>(events.Current.Payload);
        var restored = await platform.SendMessageAsync(ChatScene.Friend, "300", "300", "100",
            [new TextSegment("Live again")], timeout.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(restored.Id, ((MessageEvent)events.Current.Payload).Message.Id);
    }

    [TestMethod]
    public async Task OfflineBotCannotSendAsItselfAndRestorationDoesNotDeleteHistory()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        var existing = await platform.SendMessageAsync(ChatScene.Friend, "300", "100", "100",
            [new TextSegment("Before logout")]);
        await platform.SetBotPresenceAsync("100", false);
        var failure = await Assert.ThrowsAsync<PlatformException>(() => platform.SendMessageAsync(
            ChatScene.Friend, "300", "100", "100", [new TextSegment("Rejected")]));
        Assert.AreEqual(PlatformError.NotPermitted, failure.Error);
        Assert.HasCount(1, await store.GetMessagesAsync(new Chat(ChatScene.Friend, "300", "100")));
        await platform.SetBotPresenceAsync("100", true);
        await platform.SendMessageAsync(ChatScene.Friend, "300", "100", "100", [new TextSegment("After login")]);
        Assert.HasCount(2, await store.GetMessagesAsync(new Chat(ChatScene.Friend, "300", "100")));
        Assert.IsNotNull(await store.GetMessageAsync(existing.Id));
    }

    [TestMethod]
    public async Task InvalidAndCanceledChangesDoNotChangeRuntimeStateOrRaiseNotifications()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        var count = 0;
        platform.BotPresenceChanged += (_, _) => count++;
        await Assert.ThrowsAsync<PlatformException>(() => platform.SetBotPresenceAsync("300", false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            platform.SetBotPresenceAsync("100", false, cancellationToken: cancellation.Token));
        Assert.IsTrue(platform.IsBotOnline("100"));
        Assert.AreEqual(0, count);
    }

    private static async Task SeedAsync(PlatformService platform)
    {
        foreach (var id in new[] { "100", "200", "300" })
            await platform.SaveUserAsync(new User("User " + id, id));
        platform.RegisterBot("100");
        platform.RegisterBot("200");
    }
}
