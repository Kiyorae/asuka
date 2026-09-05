using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class PlatformServiceTests
{
    [TestMethod]
    public async Task ClearMessageHistoryRemovesOnlyTheSelectedConversation()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);
        var groupChat = new Asuka.Core.Chat(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Bot.Id);
        await service.SendMessageAsync(
            groupChat.Scene,
            groupChat.PeerId,
            fixture.Member.Id,
            groupChat.SelfId,
            [new Asuka.Core.TextSegment("remove me")]);
        await service.SendMessageAsync(
            Asuka.Core.ChatScene.Friend,
            fixture.Member.Id,
            fixture.Member.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("keep me")]);

        await service.ClearMessageHistoryAsync(groupChat);

        Assert.IsEmpty(await store.GetMessagesAsync(groupChat));
        Assert.HasCount(
            1,
            await store.GetMessagesAsync(new Asuka.Core.Chat(
                Asuka.Core.ChatScene.Friend,
                fixture.Member.Id,
                fixture.Bot.Id)));
    }

    [TestMethod]
    public async Task SaveAssetUsesThePlatformMutationBoundary()
    {
        await using var store = new Asuka.Core.AsukaStore();
        await using var service = new Asuka.Core.PlatformService(store);
        var asset = new Asuka.Core.Asset(
            new string('a', 64),
            "attachment.txt",
            "text/plain",
            ByteCount: 12);

        await service.SaveAssetAsync(asset);

        Assert.AreEqual(asset, await store.GetAssetAsync(asset.Id));
    }

    [TestMethod]
    public async Task CreateGroupValidatesAndCreatesOwnerMembership()
    {
        await using var store = new Asuka.Core.AsukaStore();
        await using var service = new Asuka.Core.PlatformService(store);
        var owner = new Asuka.Core.User("Owner", id: "10001");
        await service.SaveUserAsync(owner);
        var group = new Asuka.Core.Group("Created", id: "50001");

        var created = await service.CreateGroupAsync(group, owner.Id);

        Assert.AreEqual(group, created);
        Assert.AreEqual(
            Asuka.Core.GroupRole.Owner,
            (await store.GetMemberAsync(group.Id, owner.Id))?.Role);

        var missingOwner = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.CreateGroupAsync(new Asuka.Core.Group("Invalid", id: "50002"), "missing"));
        Assert.AreEqual(Asuka.Core.PlatformError.UserNotFound, missingOwner.Error);
        Assert.IsNull(await store.GetGroupAsync("50002"));
    }

    [TestMethod]
    public async Task DeleteGroupRequiresTheOwner()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);

        var denied = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.DeleteGroupAsync(fixture.Group.Id, fixture.Member.Id));

        Assert.AreEqual(Asuka.Core.PlatformError.NotPermitted, denied.Error);
        Assert.IsNotNull(await store.GetGroupAsync(fixture.Group.Id));

        await service.DeleteGroupAsync(fixture.Group.Id, fixture.Owner.Id);

        Assert.IsNull(await store.GetGroupAsync(fixture.Group.Id));
    }

    [TestMethod]
    public async Task WholeMuteBlocksMembersButNotAdmins()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);

        await service.SetWholeMuteAsync(fixture.Group.Id, fixture.Owner.Id, true);
        var exception = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(() => service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Member.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("blocked")]));
        Assert.AreEqual(Asuka.Core.PlatformError.WholeGroupMuted, exception.Error);

        await service.SetAdminAsync(fixture.Group.Id, fixture.Member.Id, fixture.Owner.Id, true);
        var sent = await service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Member.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("allowed")]);
        Assert.AreEqual(1L, sent.Seq);
    }

    [TestMethod]
    public async Task AdminCannotRecallEqualOrHigherRoleMessage()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);
        await service.SetAdminAsync(fixture.Group.Id, fixture.Member.Id, fixture.Owner.Id, true);
        var ownerMessage = await service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Owner.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("owner")]);

        var denied = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.RecallMessageAsync(ownerMessage.Id, fixture.Member.Id));
        Assert.AreEqual(Asuka.Core.PlatformError.NotPermitted, denied.Error);

        var recalled = await service.RecallMessageAsync(ownerMessage.Id, fixture.Owner.Id);
        Assert.IsTrue(recalled.IsRecalled);
        Assert.AreEqual(fixture.Owner.Id, recalled.RecalledBy);
    }

    [TestMethod]
    public async Task SelfEchoIsSuppressedByDefaultAndCanBeEnabled()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);
        service.RegisterBot(fixture.Bot.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = service.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);

        var firstEvent = events.MoveNextAsync().AsTask();
        await service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Member.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("human")],
            timeout.Token);
        Assert.IsTrue(await firstEvent);
        Assert.IsInstanceOfType<Asuka.Core.MessageEvent>(events.Current.Payload);

        service.SetEchoesSelfEvents(true);
        var echoedEvent = events.MoveNextAsync().AsTask();
        await service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Bot.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("bot")],
            timeout.Token);
        Assert.IsTrue(await echoedEvent);
        Assert.AreEqual(fixture.Bot.Id, events.Current.SelfId);
    }

    [TestMethod]
    public async Task CancellationAfterMessageCommitStillCompletesFollowUpAndEvent()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);
        using var sendCancellation = new CancellationTokenSource();
        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = service.Events(eventTimeout.Token).GetAsyncEnumerator(eventTimeout.Token);
        var received = events.MoveNextAsync().AsTask();
        EventHandler<Asuka.Core.StoreChangedEventArgs> cancelAfterCommit = (_, args) =>
        {
            if ((args.Changes & Asuka.Core.StoreChangeKind.Messages) != 0)
            {
                sendCancellation.Cancel();
            }
        };
        store.Changed += cancelAfterCommit;
        try
        {
            var sent = await service.SendMessageAsync(
                Asuka.Core.ChatScene.Group,
                fixture.Group.Id,
                fixture.Member.Id,
                fixture.Bot.Id,
                [new Asuka.Core.TextSegment("committed")],
                sendCancellation.Token);

            Assert.IsTrue(await received);
            Assert.AreEqual(sent.Id, ((Asuka.Core.MessageEvent)events.Current.Payload).Message.Id);
            Assert.AreEqual(sent.Time, (await store.GetMemberAsync(fixture.Group.Id, fixture.Member.Id))?.LastSentAt);
        }
        finally
        {
            store.Changed -= cancelAfterCommit;
        }
    }

    [TestMethod]
    public async Task OutsidersCannotPokeOrReactInAGroup()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        var outsider = new Asuka.Core.User("Outsider", id: "10004");
        await store.SaveAsync(outsider);
        await using var service = new Asuka.Core.PlatformService(store);
        var message = await service.SendMessageAsync(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Member.Id,
            fixture.Bot.Id,
            [new Asuka.Core.TextSegment("message")]);

        var poke = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.PokeAsync(Asuka.Core.ChatScene.Group, fixture.Group.Id, outsider.Id, fixture.Member.Id));
        Assert.AreEqual(Asuka.Core.PlatformError.NotAMember, poke.Error);

        var reaction = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.ReactAsync(message.Id, outsider.Id, "1", true));
        Assert.AreEqual(Asuka.Core.PlatformError.NotAMember, reaction.Error);
    }

    [TestMethod]
    public async Task PrivatePokeAllowsSelfTargetButRejectsThirdPartyTarget()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        var outsider = new Asuka.Core.User("Outsider", id: "10004");
        await store.SaveAsync(outsider);
        await using var service = new Asuka.Core.PlatformService(store);
        await service.AddFriendshipAsync(fixture.Bot.Id, fixture.Member.Id);

        await service.PokeAsync(
            Asuka.Core.ChatScene.Friend,
            fixture.Member.Id,
            fixture.Bot.Id,
            fixture.Bot.Id);

        var denied = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(() => service.PokeAsync(
            Asuka.Core.ChatScene.Friend,
            fixture.Member.Id,
            fixture.Bot.Id,
            outsider.Id));
        Assert.AreEqual(Asuka.Core.PlatformError.NotPermitted, denied.Error);
    }

    [TestMethod]
    public async Task FriendRequestApprovalCreatesBothDirectionsWithRemark()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        await using var service = new Asuka.Core.PlatformService(store);

        var request = await service.RequestFriendAsync(fixture.Member.Id, fixture.Bot.Id, "hello");
        await service.ResolveRequestAsync(request.Flag, true, remark: "Member remark");

        Assert.AreEqual("Member remark", (await store.GetFriendshipAsync(fixture.Bot.Id, fixture.Member.Id))?.Remark);
        Assert.IsNotNull(await store.GetFriendshipAsync(fixture.Member.Id, fixture.Bot.Id));
        Assert.IsNotNull((await store.GetRequestAsync(request.Id))?.Resolution);

        var duplicate = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.ResolveRequestAsync(request.Flag, true));
        Assert.AreEqual(Asuka.Core.PlatformError.NotPermitted, duplicate.Error);
    }

    [TestMethod]
    public async Task FailedGroupApprovalRemainsPendingAndCanBeRetried()
    {
        await using var store = new Asuka.Core.AsukaStore();
        await using var service = new Asuka.Core.PlatformService(store);
        var owner = new Asuka.Core.User("Owner", id: "10001");
        var requester = new Asuka.Core.User("Requester", id: "10002");
        await service.SaveUserAsync(owner);
        await service.SaveUserAsync(requester);
        var group = new Asuka.Core.Group("Full", id: "50001", maxMemberCount: 1);
        await service.CreateGroupAsync(group, owner.Id);
        var request = await service.RequestJoinGroupAsync(group.Id, requester.Id, owner.Id);

        var full = await Assert.ThrowsAsync<Asuka.Core.PlatformException>(
            () => service.ResolveRequestAsync(request.Flag, true));
        Assert.AreEqual(Asuka.Core.PlatformError.NotPermitted, full.Error);
        Assert.IsNull((await store.GetRequestAsync(request.Id))?.Resolution);
        Assert.IsNull(await store.GetMemberAsync(group.Id, requester.Id));

        await service.SaveGroupAsync(group with { MaxMemberCount = 2 });
        await service.ResolveRequestAsync(request.Flag, true);
        Assert.AreEqual(
            Asuka.Core.RequestResolutionStatus.Accepted,
            (await store.GetRequestAsync(request.Id))?.Resolution?.Status);
        Assert.IsNotNull(await store.GetMemberAsync(group.Id, requester.Id));
    }

    private static async Task<(Asuka.Core.User Owner, Asuka.Core.User Member, Asuka.Core.User Bot, Asuka.Core.Group Group)> SeedAsync(
        Asuka.Core.AsukaStore store)
    {
        var owner = new Asuka.Core.User("Owner", id: "10001");
        var member = new Asuka.Core.User("Member", id: "10002");
        var bot = new Asuka.Core.User("Bot", id: "10003");
        var group = new Asuka.Core.Group("Group", id: "50001");
        await store.SaveAsync(owner);
        await store.SaveAsync(member);
        await store.SaveAsync(bot);
        await store.SaveAsync(group);
        await store.SaveAsync(new Asuka.Core.GroupMember(group.Id, owner.Id, role: Asuka.Core.GroupRole.Owner));
        await store.SaveAsync(new Asuka.Core.GroupMember(group.Id, member.Id));
        await store.SaveAsync(new Asuka.Core.GroupMember(group.Id, bot.Id));
        return (owner, member, bot, group);
    }
}
