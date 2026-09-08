using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class GroupNotificationTests
{
    [TestMethod]
    public async Task AdminChangesAreAtomicIdempotentAndOnlyPersistForMemberRecipients()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        Assert.IsTrue(await store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3", "4", "3", "6"]));
        Assert.AreEqual(GroupRole.Admin, (await store.GetMemberAsync("500", "5"))!.Role);
        var first = (await store.GetGroupNotificationPageAsync("3")).Notifications.Single();
        Assert.AreEqual(GroupNotificationKind.AdminChange, first.Kind);
        Assert.AreEqual("5", first.TargetUserId);
        Assert.AreEqual("1", first.OperatorId);
        Assert.IsNotNull(first.IsSet);
        Assert.IsTrue(first.IsSet.Value);
        Assert.IsNull(first.Request);
        Assert.IsFalse(first.IsFiltered);
        Assert.HasCount(1, (await store.GetGroupNotificationPageAsync("4")).Notifications);
        Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("6")).Notifications);
        Assert.IsFalse(await store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3", "4"]));
        Assert.HasCount(1, (await store.GetGroupNotificationPageAsync("3")).Notifications);
        Assert.IsTrue(await store.SetAdminWithNotificationsAsync("500", "5", "1", false, ["3"]));
        var last = (await store.GetGroupNotificationPageAsync("3")).Notifications[0];
        Assert.IsNotNull(last.IsSet);
        Assert.IsFalse(last.IsSet.Value);
        Assert.IsGreaterThan(first.NotificationSequence, last.NotificationSequence);
    }

    [TestMethod]
    public async Task RequestsAndMemberChangesShareOneInclusiveDescendingPage()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        var join = await SaveRequestAsync(store, RequestKind.GroupJoin, "3");
        await store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3"]);
        var invited = await SaveRequestAsync(store, RequestKind.GroupInvitedJoin, "3");
        var filtered = await SaveRequestAsync(store, RequestKind.GroupJoin, "3", true);
        await SaveRequestAsync(store, RequestKind.Friend, "3");
        await SaveRequestAsync(store, RequestKind.GroupInvite, "3");
        await SaveRequestAsync(store, RequestKind.GroupJoin, "4");
        var first = await store.GetGroupNotificationPageAsync("3", limit: 2);
        Assert.HasCount(2, first.Notifications);
        Assert.AreEqual(invited.NotificationSequence, first.Notifications[0].NotificationSequence);
        Assert.AreEqual(GroupNotificationKind.AdminChange, first.Notifications[1].Kind);
        Assert.AreEqual(join.NotificationSequence, first.NextSequence);
        var last = await store.GetGroupNotificationPageAsync("3", startSequence: first.NextSequence, limit: 2);
        Assert.AreEqual(join.Id, last.Notifications.Single().Request!.Id);
        Assert.IsNull(last.NextSequence);
        var onlyFiltered = await store.GetGroupNotificationPageAsync("3", isFiltered: true);
        Assert.AreEqual(filtered.Id, onlyFiltered.Notifications.Single().Request!.Id);
        Assert.IsTrue(onlyFiltered.Notifications[0].IsFiltered);
        await store.ResolveRequestAsync(join.Id, RequestResolution.Accepted, "1");
        var resolved = (await store.GetGroupNotificationPageAsync("3", startSequence: join.NotificationSequence)).Notifications.Single();
        Assert.AreEqual(RequestResolutionStatus.Accepted, resolved.Request!.Resolution!.Status);
        Assert.AreEqual("1", resolved.OperatorId);
        Assert.IsGreaterThan(join.NotificationSequence, first.Notifications[1].NotificationSequence);
        Assert.IsGreaterThan(first.Notifications[1].NotificationSequence, invited.NotificationSequence);
    }

    [TestMethod]
    public async Task KickIncludesDepartingBotAndSelfLeaveIsQuitRegardlessOfLegacyReason()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        Assert.IsTrue(await store.RemoveMemberWithNotificationsAsync("500", "3", "2", GroupMemberChangeReason.Voluntary, ["3", "4", "6"]));
        Assert.IsNull(await store.GetMemberAsync("500", "3"));
        var kick = (await store.GetGroupNotificationPageAsync("3")).Notifications.Single();
        Assert.AreEqual(GroupNotificationKind.Kick, kick.Kind);
        Assert.AreEqual("3", kick.TargetUserId);
        Assert.AreEqual("2", kick.OperatorId);
        Assert.IsNull(kick.IsSet);
        Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("6")).Notifications);
        Assert.IsFalse(await store.RemoveMemberWithNotificationsAsync("500", "3", "2", GroupMemberChangeReason.Administrative, ["3", "4"]));
        Assert.HasCount(1, (await store.GetGroupNotificationPageAsync("4")).Notifications);
        Assert.IsTrue(await store.RemoveMemberWithNotificationsAsync("500", "4", "4", GroupMemberChangeReason.Voluntary, ["4"]));
        var quit = (await store.GetGroupNotificationPageAsync("4")).Notifications[0];
        Assert.AreEqual(GroupNotificationKind.Quit, quit.Kind);
        Assert.IsNull(quit.OperatorId);
        Assert.IsNull(await store.GetMemberAsync("500", "4"));
    }

    [TestMethod]
    public async Task RoleFailuresCannotChangeMembershipOrWriteHistory()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await Assert.ThrowsExactlyAsync<PlatformException>(() => store.SetAdminWithNotificationsAsync("500", "5", "2", true, ["3"]));
        await Assert.ThrowsExactlyAsync<PlatformException>(() => store.SetAdminWithNotificationsAsync("500", "1", "1", false, ["3"]));
        await Assert.ThrowsExactlyAsync<PlatformException>(() => store.RemoveMemberWithNotificationsAsync("500", "5", "3", GroupMemberChangeReason.Administrative, ["3"]));
        await Assert.ThrowsExactlyAsync<PlatformException>(() => store.RemoveMemberWithNotificationsAsync("500", "2", "3", GroupMemberChangeReason.Administrative, ["3"]));
        await Assert.ThrowsExactlyAsync<PlatformException>(() => store.RemoveMemberWithNotificationsAsync("500", "1", "1", GroupMemberChangeReason.Voluntary, ["3"]));
        Assert.AreEqual(GroupRole.Member, (await store.GetMemberAsync("500", "5"))!.Role);
        Assert.AreEqual(GroupRole.Owner, (await store.GetMemberAsync("500", "1"))!.Role);
        Assert.HasCount(5, await store.GetMembersAsync("500"));
        Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("3")).Notifications);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateChangesOnlyAllocateOneHistoryEntry()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3"])));
        Assert.AreEqual(1, results.Count(changed => changed));
        Assert.HasCount(1, (await store.GetGroupNotificationPageAsync("3")).Notifications);
        var request = await SaveRequestAsync(store, RequestKind.GroupJoin, "3");
        Assert.AreEqual(2L, request.NotificationSequence);
    }

    [TestMethod]
    public async Task HistorySurvivesGroupAndActorDeletionAndDatabaseReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "asuka-notifications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.sqlite3");
        try
        {
            await using (var store = new AsukaStore(path))
            {
                await SeedAsync(store);
                await store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3"]);
                await store.DeleteGroupAsync("500");
                await store.DeleteUserAsync("1");
                await store.DeleteUserAsync("5");
            }
            await using var reopened = new AsukaStore(path);
            await using var platform = new PlatformService(reopened);
            var entry = (await platform.GetGroupNotificationsAsync("3")).Notifications.Single();
            Assert.AreEqual("500", entry.GroupId);
            Assert.AreEqual("5", entry.TargetUserId);
            Assert.AreEqual("1", entry.OperatorId);
            Assert.AreEqual(GroupNotificationKind.AdminChange, entry.Kind);
            Assert.IsGreaterThan(DateTimeOffset.UnixEpoch, entry.Time);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task AHistoryWriteFailureRollsBackTheAlreadyAttemptedRoleChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "asuka-notification-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.sqlite3");
        try
        {
            await using var store = new AsukaStore(path);
            await SeedAsync(store);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE request_notification_sequence SET next_seq=9223372036854775807 WHERE id=1;";
                await command.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsExactlyAsync<StorePersistenceException>(() => store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3"]));
            Assert.AreEqual(GroupRole.Member, (await store.GetMemberAsync("500", "5"))!.Role);
            Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("3")).Notifications);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CancellationAndInvalidPageArgumentsLeaveStateUnchanged()
    {
        await using var store = new AsukaStore();
        await using var platform = new PlatformService(store);
        await SeedAsync(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => store.SetAdminWithNotificationsAsync("500", "5", "1", true, ["3"], cancellation.Token));
        Assert.AreEqual(GroupRole.Member, (await store.GetMemberAsync("500", "5"))!.Role);
        Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("3")).Notifications);
        await Assert.ThrowsExactlyAsync<PlatformException>(() => platform.GetGroupNotificationsAsync("3", limit: 0));
        await Assert.ThrowsExactlyAsync<PlatformException>(() => platform.GetGroupNotificationsAsync("missing"));
        Assert.HasCount(0, (await store.GetGroupNotificationPageAsync("3", startSequence: 0)).Notifications);
    }

    private static async Task<PendingRequest> SaveRequestAsync(AsukaStore store, RequestKind kind, string selfId, bool filtered = false)
    {
        var request = new PendingRequest(kind, "6", selfId, kind == RequestKind.Friend ? null : "500",
            targetUserId: kind == RequestKind.GroupInvitedJoin ? "7" : null, isFiltered: filtered);
        await store.SaveAsync(request);
        return (await store.GetRequestAsync(request.Id))!;
    }

    private static async Task SeedAsync(AsukaStore store)
    {
        foreach (var id in Enumerable.Range(1, 7).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            await store.SaveAsync(new User("User " + id, id));
        await store.SaveAsync(new Group("Notifications", "500"));
        foreach (var id in new[] { "1", "2", "3", "4", "5" })
            await store.SaveAsync(new GroupMember("500", id, role: id == "1" ? GroupRole.Owner : id == "2" ? GroupRole.Admin : GroupRole.Member));
    }
}
