using Asuka.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class MessageInteractionTests
{
    [TestMethod]
    public async Task LiveReplyAppendChecksTargetInTheStorageTransaction()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        var target = await store.AppendMessageAsync(new Message(ChatScene.Group, "50001", "10002", "10003",
            [new TextSegment("target")], MessageDirection.Outgoing));
        var prepared = new Message(ChatScene.Group, "50001", "10002", "10003",
            [new ReplySegment(target.Id), new TextSegment("prepared before recall")], MessageDirection.Outgoing);
        await store.RecallMessageAsync(target.Id, "10002");
        var error = await Assert.ThrowsAsync<PlatformException>(() => store.AppendLiveMessageAsync(prepared));
        Assert.AreEqual(PlatformError.InvalidParameter, error.Error);
        Assert.IsNull(await store.GetMessageAsync(prepared.Id));
        var next = await store.AppendLiveMessageAsync(new Message(ChatScene.Group, "50001", "10002", "10003",
            [new TextSegment("next")], MessageDirection.Outgoing));
        Assert.AreEqual(target.Seq + 1, next.Seq);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("recalled")]
    [DataRow("other_chat")]
    [DataRow("other_account")]
    public async Task ReplyValidationAppliesInsidePlatformMutation(string mode)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var target = await store.AppendMessageAsync(new Message(ChatScene.Group,
            mode == "other_chat" ? "50002" : "50001", "10002",
            mode == "other_account" ? "10004" : "10003", [new TextSegment("target")], MessageDirection.Outgoing));
        if (mode == "recalled") await platform.RecallMessageAsync(target.Id, "10002");
        var chat = new Chat(ChatScene.Group, "50001", "10003");
        var before = await store.GetMessagesAsync(chat);
        var error = await Assert.ThrowsAsync<PlatformException>(() => platform.SendMessageAsync(
            chat.Scene, chat.PeerId, "10002", chat.SelfId,
            [new ReplySegment(mode == "missing" ? "missing" : target.Id), new TextSegment("must not send")]));
        Assert.AreEqual(PlatformError.InvalidParameter, error.Error);
        Assert.HasCount(before.Count, await store.GetMessagesAsync(chat));
    }

    [TestMethod]
    public async Task ReactionsArePerUserIdempotentAndPublishOnlyCommittedChanges()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        var messageChanges = 0;
        store.Changed += (_, args) =>
        {
            if ((args.Changes & StoreChangeKind.Messages) != 0)
            {
                messageChanges++;
            }
        };

        await platform.ReactAsync(message.Id, "10001", "128077", true, "emoji", timeout.Token);
        await platform.ReactAsync(message.Id, "10001", "128077", true, "emoji", timeout.Token);
        await platform.ReactAsync(message.Id, "10002", "128077", true, "emoji", timeout.Token);
        await platform.ReactAsync(message.Id, "10001", "128077", false, "emoji", timeout.Token);
        await platform.ReactAsync(message.Id, "10001", "128077", false, "emoji", timeout.Token);

        Assert.AreEqual(3, messageChanges);
        CollectionAssert.AreEqual(
            new[] { new MessageReactionState(message.Id, "10002", "128077", "emoji") },
            (await store.GetMessageReactionsAsync(message.Id)).ToArray());
        await SendAsync(platform);

        Assert.IsTrue(await next);
        Assert.AreEqual(
            new MessageReaction(message.Id, ChatScene.Group, "50001", "10001", "128077", true, "emoji"),
            ((MessageReactionEvent)events.Current.Payload).Reaction);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual("10002", ((MessageReactionEvent)events.Current.Payload).Reaction.UserId);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsFalse(((MessageReactionEvent)events.Current.Payload).Reaction.Added);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<MessageEvent>(events.Current.Payload);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateReactionsProduceOneStoredEntry()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => platform.ReactAsync(message.Id, "10001", "66", true)));

        Assert.HasCount(1, await store.GetMessageReactionsAsync(message.Id));
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => platform.ReactAsync(message.Id, "10001", "66", false)));
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
    }

    [TestMethod]
    public async Task ReactionTypesHaveIndependentIdentity()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);

        await platform.ReactAsync(message.Id, "10001", "66", true);
        await platform.ReactAsync(message.Id, "10001", "66", true, "emoji");
        Assert.HasCount(2, await store.GetMessageReactionsAsync(message.Id));
        await platform.ReactAsync(message.Id, "10001", "66", false);

        Assert.AreEqual("emoji", (await store.GetMessageReactionsAsync(message.Id)).Single().ReactionType);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    public async Task BlankReactionIdsAreRejectedWithoutMutation(string reaction)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);

        var error = await Assert.ThrowsAsync<PlatformException>(
            () => platform.ReactAsync(message.Id, "10001", reaction, true));

        Assert.AreEqual(PlatformError.InvalidParameter, error.Error);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
    }

    [TestMethod]
    public async Task ReactionsRequireConversationParticipantsAndActiveGroupMembership()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var groupMessage = await SendAsync(platform);
        var privateMessage = await platform.SendMessageAsync(
            ChatScene.Friend, "10002", "10002", "10003", [new TextSegment("private")]);

        var groupError = await Assert.ThrowsAsync<PlatformException>(
            () => platform.ReactAsync(groupMessage.Id, "10004", "66", true));
        Assert.AreEqual(PlatformError.NotAMember, groupError.Error);
        var privateError = await Assert.ThrowsAsync<PlatformException>(
            () => platform.ReactAsync(privateMessage.Id, "10001", "66", true));
        Assert.AreEqual(PlatformError.NotPermitted, privateError.Error);

        await platform.ReactAsync(privateMessage.Id, "10002", "66", true);
        await platform.ReactAsync(privateMessage.Id, "10003", "66", true);
        Assert.HasCount(2, await store.GetMessageReactionsAsync(privateMessage.Id));
        Assert.IsEmpty(await store.GetMessageReactionsAsync(groupMessage.Id));
    }

    [TestMethod]
    public async Task RecallIsIdempotentClearsReactionsAndPublishesOneNotice()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);
        await platform.ReactAsync(message.Id, "10001", "66", true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        var changes = 0;
        store.Changed += (_, args) =>
        {
            if ((args.Changes & StoreChangeKind.Messages) != 0)
            {
                changes++;
            }
        };

        var first = await platform.RecallMessageAsync(message.Id, "10002");
        var second = await platform.RecallMessageAsync(message.Id, "10001");

        Assert.AreEqual(first.RecalledAt, second.RecalledAt);
        Assert.AreEqual("10002", second.RecalledBy);
        Assert.AreEqual(1, changes);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
        await SendAsync(platform);
        Assert.IsTrue(await next);
        Assert.IsInstanceOfType<MessageRecalledEvent>(events.Current.Payload);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.IsInstanceOfType<MessageEvent>(events.Current.Payload);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RecalledMessagesRejectReactions(bool added)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);
        await platform.RecallMessageAsync(message.Id, "10002");

        var error = await Assert.ThrowsAsync<PlatformException>(
            () => platform.ReactAsync(message.Id, "10001", "66", added));

        Assert.AreEqual(PlatformError.NotPermitted, error.Error);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
    }

    [TestMethod]
    public async Task FormerMembersCannotRecallTheirOwnGroupMessages()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);
        await platform.RemoveMemberAsync("50001", "10002", "10002");

        var error = await Assert.ThrowsAsync<PlatformException>(
            () => platform.RecallMessageAsync(message.Id, "10002"));

        Assert.AreEqual(PlatformError.NotAMember, error.Error);
        Assert.IsFalse((await store.GetMessageAsync(message.Id))!.IsRecalled);
        Assert.IsTrue((await platform.RecallMessageAsync(message.Id, "10001")).IsRecalled);
    }

    [TestMethod]
    public async Task PrivateRecallRequiresTheSenderToBeAConversationParticipant()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await platform.SendMessageAsync(
            ChatScene.Friend, "10002", "10002", "10003", [new TextSegment("private")]);

        var recipientError = await Assert.ThrowsAsync<PlatformException>(
            () => platform.RecallMessageAsync(message.Id, "10003"));
        Assert.AreEqual(PlatformError.NotPermitted, recipientError.Error);
        var outsiderError = await Assert.ThrowsAsync<PlatformException>(
            () => platform.RecallMessageAsync(message.Id, "10001"));
        Assert.AreEqual(PlatformError.NotPermitted, outsiderError.Error);
        Assert.IsTrue((await platform.RecallMessageAsync(message.Id, "10002")).IsRecalled);

        var imported = await store.AppendMessageAsync(new Message(
            ChatScene.Friend, "10002", "10001", "10003", [new TextSegment("invalid sender")],
            MessageDirection.Incoming));
        var importedError = await Assert.ThrowsAsync<PlatformException>(
            () => platform.RecallMessageAsync(imported.Id, "10001"));
        Assert.AreEqual(PlatformError.NotPermitted, importedError.Error);
    }

    [TestMethod]
    public async Task CancellationAfterReactionCommitStillPublishesTheEvent()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        var message = await SendAsync(platform);
        using var mutationCancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        store.Changed += (_, args) =>
        {
            if ((args.Changes & StoreChangeKind.Messages) != 0)
            {
                mutationCancellation.Cancel();
            }
        };

        await platform.ReactAsync(message.Id, "10001", "66", true, mutationCancellation.Token);

        Assert.IsTrue(await next);
        Assert.IsInstanceOfType<MessageReactionEvent>(events.Current.Payload);
        Assert.HasCount(1, await store.GetMessageReactionsAsync(message.Id));
    }

    private static Task<Message> SendAsync(PlatformService platform) =>
        platform.SendMessageAsync(ChatScene.Group, "50001", "10002", "10003", [new TextSegment("message")]);

    internal static async Task SeedAsync(AsukaStore store)
    {
        await store.SaveAsync(new User("Owner", id: "10001"));
        await store.SaveAsync(new User("Member", id: "10002"));
        await store.SaveAsync(new User("Bot", id: "10003"));
        await store.SaveAsync(new User("Outsider", id: "10004"));
        await store.SaveAsync(new Group("Group", id: "50001"));
        await store.SaveAsync(new GroupMember("50001", "10001", role: GroupRole.Owner));
        await store.SaveAsync(new GroupMember("50001", "10002"));
        await store.SaveAsync(new GroupMember("50001", "10003"));
    }
}
