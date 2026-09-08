using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class PeerStateTests
{
    private const string SelfId = "100";
    private const string FriendId = "200";
    private const string GroupId = "300";

    [TestMethod]
    public async Task PeerStateAndProfileLikesSurviveDatabaseReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-peer-state-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "state.db");
        try
        {
            long sequence;
            var chat = new Chat(ChatScene.Friend, FriendId, SelfId);
            await using (var store = new AsukaStore(database))
            await using (var platform = new PlatformService(store))
            {
                await SeedAsync(platform);
                var message = await platform.SendMessageAsync(chat.Scene, chat.PeerId, FriendId, SelfId, [new TextSegment("read")]);
                sequence = message.Seq;
                await platform.SetPeerPinAsync(chat, true);
                await platform.MarkMessageAsReadAsync(chat, sequence);
                await platform.SendProfileLikeAsync(FriendId, SelfId, 7);
                await platform.SetNicknameAsync(SelfId, "Saved nickname");
                await platform.SetBioAsync(SelfId, "Saved bio");
            }
            await using (var reopened = new AsukaStore(database))
            {
                Assert.AreEqual(new PeerState(chat, true, sequence), await reopened.GetPeerStateAsync(chat));
                var likes = await reopened.GetProfileLikesAsync(FriendId);
                Assert.HasCount(1, likes);
                Assert.AreEqual(7L, likes[0].Count);
                Assert.AreEqual(SelfId, likes[0].SenderId);
                var user = (await reopened.GetUserAsync(SelfId))!;
                Assert.AreEqual("Saved nickname", user.Nickname);
                Assert.AreEqual("Saved bio", user.Sign);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClearingHistoryResetsReadPositionButPreservesPinAndNewMessagesBecomeUnread()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        var chat = new Chat(ChatScene.Group, GroupId, SelfId);
        var first = await platform.SendMessageAsync(chat.Scene, GroupId, FriendId, SelfId, [new TextSegment("old")]);
        await platform.SetPeerPinAsync(chat, true);
        await platform.MarkMessageAsReadAsync(chat, first.Seq);
        await platform.ClearMessageHistoryAsync(chat);
        Assert.AreEqual(new PeerState(chat, true, 0), await store.GetPeerStateAsync(chat));
        var next = await platform.SendMessageAsync(chat.Scene, GroupId, FriendId, SelfId, [new TextSegment("new")]);
        Assert.IsGreaterThan((await store.GetPeerStateAsync(chat)).LastReadSequence, next.Seq);
    }

    [TestMethod]
    public async Task RemovedRelationshipsAndEntitiesCannotLeavePinsBehind()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        var friend = new Chat(ChatScene.Friend, FriendId, SelfId);
        var group = new Chat(ChatScene.Group, GroupId, SelfId);
        await platform.SetPeerPinAsync(friend, true);
        await platform.SetPeerPinAsync(group, true);
        await platform.RemoveFriendAsync(SelfId, FriendId);
        Assert.IsFalse((await store.GetPeerStateAsync(friend)).IsPinned);
        await platform.DeleteGroupAsync(GroupId, SelfId);
        Assert.HasCount(0, await store.GetPeerStatesAsync(SelfId));
        await platform.AddFriendshipAsync(SelfId, FriendId);
        await platform.SetPeerPinAsync(friend, true);
        await platform.SendProfileLikeAsync(FriendId, SelfId);
        await platform.DeleteUserAsync(FriendId);
        Assert.HasCount(0, await store.GetPeerStatesAsync(SelfId));
        Assert.HasCount(0, await store.GetProfileLikesAsync(FriendId));
    }

    [TestMethod]
    public async Task PeerMutationsRequireFriendshipMembershipAndAnExistingTemporaryConversation()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        await platform.SaveUserAsync(new User("Stranger", id: "400"));
        foreach (var chat in new[]
        {
            new Chat(ChatScene.Friend, "400", SelfId),
            new Chat(ChatScene.Temp, "400", SelfId),
            new Chat(ChatScene.Group, GroupId, "400"),
            new Chat(ChatScene.Friend, FriendId, "999"),
        })
        {
            await Assert.ThrowsAsync<PlatformException>(() => platform.SetPeerPinAsync(chat, true));
        }
        await Assert.ThrowsAsync<PlatformException>(() => platform.SendProfileLikeAsync("400", SelfId));
        Assert.HasCount(0, await store.GetPeerStatesAsync(SelfId));
        Assert.HasCount(0, await store.GetPeerStatesAsync("400"));
    }

    [TestMethod]
    public async Task ATemporaryConversationCanStillBeUnpinnedAfterItsHistoryWasCleared()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(platform);
        var chat = new Chat(ChatScene.Temp, FriendId, SelfId);
        await platform.SendMessageAsync(chat.Scene, FriendId, FriendId, SelfId, [new TextSegment("temporary")]);
        await platform.SetPeerPinAsync(chat, true);
        await platform.ClearMessageHistoryAsync(chat);
        await platform.SetPeerPinAsync(chat, false);
        Assert.IsFalse((await store.GetPeerStateAsync(chat)).IsPinned);
    }

    private static async Task SeedAsync(PlatformService platform)
    {
        await platform.SaveUserAsync(new User("Self", id: SelfId));
        await platform.SaveUserAsync(new User("Friend", id: FriendId));
        await platform.AddFriendshipAsync(SelfId, FriendId);
        await platform.CreateGroupAsync(new Group("Group", id: GroupId), SelfId);
        await platform.AddMemberAsync(GroupId, FriendId, SelfId);
    }
}
