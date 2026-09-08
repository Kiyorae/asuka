using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class RequestLifecycleStoreTests
{
    [TestMethod]
    public async Task RemovingAGroupDoesNotReuseAnOldNotificationSequence()
    {
        await using var store = new AsukaStore();
        const string groupId = "50001";
        await store.SaveAsync(new Group("Temporary", id: groupId));
        var request = new PendingRequest(RequestKind.GroupJoin, "10001", "10002", groupId);
        await store.SaveAsync(request);
        var originalSequence = (await store.GetRequestAsync(request.Id))!.NotificationSequence;
        await store.DeleteGroupAsync(groupId);
        Assert.IsNull(await store.GetRequestAsync(request.Id));
        var later = new PendingRequest(RequestKind.Friend, "10003", "10002");
        await store.SaveAsync(later);
        Assert.IsGreaterThan(originalSequence, (await store.GetRequestAsync(later.Id))!.NotificationSequence);
    }

    [TestMethod]
    public async Task LegacyRequestsMigrateWithDefaultMetadataAndKeepResolvedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-request-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.sqlite");
        try
        {
            // Requests used this schema through v6. Start with an authentic historical database.
            using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                legacy.Open();
                LegacyStoreSchema.Create(legacy, 2);
                using var command = legacy.CreateCommand();
                command.CommandText = """
                    INSERT INTO pending_requests VALUES
                        ('request-old', 'old-flag', 'groupJoin', '10001', '50001', '10002',
                            'old comment', 638000000000000000, 'rejected', 'old reason'),
                        ('request-pending', 'pending-flag', 'friend', '10003', NULL, '10002',
                            '', 638000000000000001, NULL, NULL);
                    """;
                command.ExecuteNonQuery();
            }

            await using var store = new AsukaStore(path);
            var old = (await store.GetRequestByFlagAsync("old-flag"))!;
            Assert.AreEqual(RequestKind.GroupJoin, old.Kind);
            Assert.AreEqual(RequestResolution.Rejected("old reason"), old.Resolution);
            Assert.IsFalse(old.IsFiltered);
            Assert.AreEqual("asuka", old.Via);
            Assert.IsNull(old.TargetUserId);
            Assert.IsNull(old.SourceGroupId);
            Assert.IsNull(old.ResolvedBy);
            Assert.IsGreaterThan(0L, old.NotificationSequence);
            var pending = (await store.GetRequestByFlagAsync("pending-flag"))!;
            Assert.IsGreaterThan(old.NotificationSequence, pending.NotificationSequence);
            Assert.HasCount(2, await store.GetRequestsAsync("10002"));
            Assert.HasCount(1, await store.GetPendingRequestsAsync("10002"));
            var added = new PendingRequest(RequestKind.Friend, "10004", "10002");
            await store.SaveAsync(added);
            Assert.IsGreaterThan(pending.NotificationSequence,
                (await store.GetRequestAsync(added.Id))!.NotificationSequence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RequestMetadataAndResolutionSurviveReopeningAndSequencesAreStable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-request-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.sqlite");
        try
        {
            PendingRequest first;
            PendingRequest second;
            await using (var store = new AsukaStore(path))
            {
                var request = new PendingRequest(RequestKind.GroupInvitedJoin, "10001", "10002", "50001",
                    targetUserId: "10003", sourceGroupId: "50002", isFiltered: true, via: "group");
                await store.SaveAsync(request);
                first = (await store.GetRequestAsync(request.Id))!;
                Assert.IsGreaterThan(0L, first.NotificationSequence);
                await store.SaveAsync(first with { Comment = "updated" });
                var another = new PendingRequest(RequestKind.Friend, "10004", "10002");
                await store.SaveAsync(another);
                second = (await store.GetRequestAsync(another.Id))!;
                Assert.IsGreaterThan(first.NotificationSequence, second.NotificationSequence);
                await store.ResolveRequestAsync(first.Id, RequestResolution.Rejected("declined"),
                    resolvedBy: "10002");
                Assert.IsNull(await store.ResolveRequestAsync(first.Id, RequestResolution.Accepted,
                    resolvedBy: "10003"));
            }

            await using (var reopened = new AsukaStore(path))
            {
                var request = (await reopened.GetRequestAsync(first.Id))!;
                Assert.AreEqual(first.NotificationSequence, request.NotificationSequence);
                Assert.AreEqual("updated", request.Comment);
                Assert.AreEqual("10003", request.TargetUserId);
                Assert.AreEqual("50002", request.SourceGroupId);
                Assert.IsTrue(request.IsFiltered);
                Assert.AreEqual("group", request.Via);
                Assert.AreEqual("10002", request.ResolvedBy);
                Assert.AreEqual(RequestResolutionStatus.Rejected, request.Resolution!.Status);
                Assert.AreEqual("declined", request.Resolution.Reason);
                Assert.HasCount(2, await reopened.GetRequestsAsync("10002"));
                Assert.HasCount(1, await reopened.GetPendingRequestsAsync("10002"));
                Assert.AreEqual(second.NotificationSequence,
                    (await reopened.GetRequestByFlagAsync(second.Flag))!.NotificationSequence);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
