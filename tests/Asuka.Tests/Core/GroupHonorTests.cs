using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class GroupHonorTests
{
    private static readonly string[] ExpectedDragonOrder = ["102", "103"];

    [TestMethod]
    public async Task HonorsPersistCurrentDragonHistoryAndStableGrantOrderAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asuka-honor-{Guid.NewGuid():N}.sqlite3");
        try
        {
            await using (var store = new AsukaStore(path))
            await using (var platform = new PlatformService(store))
            {
                await SeedAsync(store);
                foreach (var type in Enum.GetValues<GroupHonorType>())
                    Assert.IsTrue(await platform.SetGroupHonorAsync("500", "102", type, "101", $"{type} description", 7));
                Assert.IsTrue(await platform.SetGroupHonorAsync("500", "103", GroupHonorType.Talkative, "101", "Second dragon", 3));
                Assert.IsTrue(await platform.SetGroupHonorAsync("500", "102", GroupHonorType.Talkative, "101", "Updated dragon", 8));
                Assert.IsFalse(await platform.SetGroupHonorAsync("500", "102", GroupHonorType.Talkative, "101", "Updated dragon", 8));
            }
            await using var reopened = new AsukaStore(path);
            await using var restored = new PlatformService(reopened);
            var info = await restored.GetGroupHonorInfoAsync("500", "103");
            Assert.AreEqual("500", info.GroupId);
            Assert.AreEqual(new GroupTalkative("102", "Alice", "https://example.com/alice.png", 8), info.CurrentTalkative);
            CollectionAssert.AreEqual(ExpectedDragonOrder, info.TalkativeList.Select(entry => entry.UserId).ToArray());
            Assert.AreEqual("Updated dragon", info.TalkativeList[0].Description);
            Assert.AreEqual("Performer description", info.PerformerList.Single().Description);
            Assert.AreEqual("Legend description", info.LegendList.Single().Description);
            Assert.AreEqual("StrongNewbie description", info.StrongNewbieList.Single().Description);
            Assert.AreEqual("Emotion description", info.EmotionList.Single().Description);
            await reopened.SaveAsync((await reopened.GetUserAsync("102"))! with { Nickname = "Updated Alice" });
            Assert.AreEqual("Updated Alice", (await restored.GetGroupHonorInfoAsync("500", "101")).CurrentTalkative!.Nickname);
        }
        finally { DeleteDatabase(path); }
    }

    [TestMethod]
    public async Task ReadsAndWritesValidateMembershipAuthorityAndTypedValuesWithoutPartialChanges()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        await AssertErrorAsync(PlatformError.NotAMember, () => platform.GetGroupHonorInfoAsync("500", "999"));
        await AssertErrorAsync(PlatformError.GroupNotFound, () => platform.GetGroupHonorInfoAsync("missing", "101"));
        await AssertErrorAsync(PlatformError.NotPermitted, () => platform.SetGroupHonorAsync("500", "102", GroupHonorType.Emotion, "103"));
        await AssertErrorAsync(PlatformError.NotAMember, () => platform.SetGroupHonorAsync("500", "999", GroupHonorType.Emotion, "101"));
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SetGroupHonorAsync("500", "102", (GroupHonorType)99, "101"));
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SetGroupHonorAsync("500", "102", GroupHonorType.Talkative, "101", dayCount: 0));
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SetGroupHonorAsync("500", "102", GroupHonorType.Emotion, "101", new string('x', 1025)));
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SetGroupHonorAsync("500", "102", GroupHonorType.Emotion, "101", null!));
        await AssertErrorAsync(PlatformError.NotPermitted, () => platform.RemoveGroupHonorAsync("500", "102", GroupHonorType.Emotion, "103"));
        await AssertErrorAsync(PlatformError.NotPermitted, () => platform.PublishGroupLuckyKingAsync("500", "102", "103", "103"));
        await AssertErrorAsync(PlatformError.NotAMember, () => platform.PublishGroupLuckyKingAsync("500", "999", "103", "101"));
        await AssertErrorAsync(PlatformError.NotAMember, () => platform.PublishGroupLuckyKingAsync("500", "102", "999", "101"));
        var info = await platform.GetGroupHonorInfoAsync("500", "101");
        Assert.IsNull(info.CurrentTalkative);
        Assert.IsEmpty(info.TalkativeList.Concat(info.PerformerList).Concat(info.LegendList).Concat(info.StrongNewbieList).Concat(info.EmotionList));
    }

    [TestMethod]
    public async Task ConcurrentDuplicateGrantsEmitOnceToOnlineMemberBotsAndLuckyKingIsAQueueBarrier()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        platform.RegisterBot("101");
        platform.RegisterBot("102");
        platform.RegisterBot("999");
        await platform.SetBotPresenceAsync("102", false, "offline test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        var changes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            platform.SetGroupHonorAsync("500", "101", GroupHonorType.Performer, "101", cancellationToken: timeout.Token)));
        Assert.AreEqual(1, changes.Count(changed => changed));
        Assert.IsTrue(await platform.SetGroupHonorAsync("500", "101", GroupHonorType.Performer, "101", "New description", cancellationToken: timeout.Token));
        await platform.SetGroupHonorAsync("500", "101", GroupHonorType.Legend, "101", cancellationToken: timeout.Token);
        await platform.SetGroupHonorAsync("500", "101", GroupHonorType.StrongNewbie, "101", cancellationToken: timeout.Token);
        Assert.IsTrue(await platform.RemoveGroupHonorAsync("500", "101", GroupHonorType.Performer, "101", timeout.Token));
        Assert.IsFalse(await platform.RemoveGroupHonorAsync("500", "101", GroupHonorType.Performer, "101", timeout.Token));
        await platform.PublishGroupLuckyKingAsync("500", "102", "103", "101", timeout.Token);
        Assert.IsTrue(await next);
        Assert.AreEqual("101", events.Current.SelfId);
        Assert.AreEqual(new GroupHonorChangedEvent(new GroupHonorChange("500", "101", GroupHonorType.Performer)), events.Current.Payload);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual("101", events.Current.SelfId);
        Assert.AreEqual(new GroupLuckyKingEvent(new GroupLuckyKing("500", "102", "103")), events.Current.Payload);
    }

    [TestMethod]
    public async Task RemovingCurrentMemberOrGroupClearsHonorsWithoutChoosingAFakeSuccessor()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        await platform.SetGroupHonorAsync("500", "102", GroupHonorType.Talkative, "101", "Former dragon", 2);
        await platform.SetGroupHonorAsync("500", "103", GroupHonorType.Talkative, "101", "Current dragon", 1);
        await platform.SetGroupHonorAsync("500", "103", GroupHonorType.Emotion, "101");
        await platform.RemoveMemberAsync("500", "103", "101", GroupMemberChangeReason.Administrative);
        var info = await platform.GetGroupHonorInfoAsync("500", "101");
        Assert.IsNull(info.CurrentTalkative);
        Assert.AreEqual("102", info.TalkativeList.Single().UserId);
        Assert.IsEmpty(info.EmotionList);
        await AssertErrorAsync(PlatformError.NotAMember, () => platform.GetGroupHonorInfoAsync("500", "103"));
        await platform.DeleteGroupAsync("500", "101");
        await store.SaveAsync(new Group("Replacement", id: "500"));
        await store.SaveAsync(new GroupMember("500", "101", role: GroupRole.Owner));
        info = await platform.GetGroupHonorInfoAsync("500", "101");
        Assert.IsNull(info.CurrentTalkative);
        Assert.IsEmpty(info.TalkativeList);
    }

    [TestMethod]
    public async Task VersionTenDatabaseMigratesWithoutInventingHonorsOrLosingMembers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asuka-honor-migration-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                VersionTenStoreSchema.Create(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users(id,name,nickname,sex,sign,created_at)
                        VALUES('101','Owner','Owner','unknown','',0),('102','Alice','Alice','unknown','',0),('103','Bob','Bob','unknown','',0);
                    INSERT INTO groups(id,name,intro,level,max_member_count,whole_muted,created_at)
                        VALUES('500','Group','',1,200,0,0);
                    INSERT INTO group_members(group_id,user_id,card,role,title,joined_at)
                        VALUES('500','101','','owner','',0),('500','102','','admin','',0),('500','103','','member','',0);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using var migrated = new AsukaStore(path);
            Assert.HasCount(3, await migrated.GetMembersAsync("500"));
            var info = await migrated.GetGroupHonorInfoAsync("500", "101");
            Assert.IsNull(info.CurrentTalkative);
            Assert.IsEmpty(info.TalkativeList);
        }
        finally { DeleteDatabase(path); }
    }

    private static async Task AssertErrorAsync(PlatformError expected, Func<Task> operation) =>
        Assert.AreEqual(expected, (await Assert.ThrowsAsync<PlatformException>(operation)).Error);

    private static async Task SeedAsync(AsukaStore store)
    {
        await store.SaveAsync(new User("Owner", id: "101"));
        await store.SaveAsync(new User("Alice", id: "102", avatar: "https://example.com/alice.png"));
        await store.SaveAsync(new User("Bob", id: "103"));
        await store.SaveAsync(new User("Outside", id: "999"));
        await store.SaveAsync(new Group("Group", id: "500"));
        await store.SaveAsync(new GroupMember("500", "101", role: GroupRole.Owner));
        await store.SaveAsync(new GroupMember("500", "102", role: GroupRole.Admin));
        await store.SaveAsync(new GroupMember("500", "103"));
    }

    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }
}
