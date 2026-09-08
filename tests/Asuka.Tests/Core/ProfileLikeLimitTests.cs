using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class ProfileLikeLimitTests
{
    [TestMethod]
    public async Task DailyLimitResetsAtChinaMidnightWhileCumulativeLikesRemain()
    {
        await using var store = new AsukaStore();
        await store.SaveAsync(new User("sender", id: "100"));
        await store.SaveAsync(new User("friend", id: "200"));
        var beforeMidnight = new DateTimeOffset(2026, 9, 8, 15, 59, 59, TimeSpan.Zero);
        await store.AddProfileLikesAsync("200", "100", 10, 10, beforeMidnight, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<PlatformException>(() =>
            store.AddProfileLikesAsync("200", "100", 1, 10, beforeMidnight, CancellationToken.None));
        Assert.AreEqual(PlatformError.NotPermitted, failure.Error);
        await store.AddProfileLikesAsync("200", "100", 3, 10, beforeMidnight.AddSeconds(2), CancellationToken.None);
        Assert.AreEqual(13L, (await store.GetProfileLikesAsync("200"))[0].Count);
        // Returning to a previous date cannot erase that date's already used allowance.
        await Assert.ThrowsAsync<PlatformException>(() =>
            store.AddProfileLikesAsync("200", "100", 1, 10, beforeMidnight, CancellationToken.None));
    }

    [TestMethod]
    public async Task DailyCountsPersistAcrossDatabaseReopenAndAreIsolatedPerSender()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-like-limits-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "state.db");
        var now = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await using (var store = new AsukaStore(database))
            {
                await store.SaveAsync(new User("sender", id: "100"));
                await store.SaveAsync(new User("friend", id: "200"));
                await store.SaveAsync(new User("other sender", id: "300"));
                await store.AddProfileLikesAsync("200", "100", 10, 10, now, CancellationToken.None);
            }
            await using (var store = new AsukaStore(database))
            {
                await Assert.ThrowsAsync<PlatformException>(() =>
                    store.AddProfileLikesAsync("200", "100", 1, 10, now.AddMinutes(1), CancellationToken.None));
                await store.AddProfileLikesAsync("200", "300", 10, 10, now, CancellationToken.None);
                Assert.HasCount(2, await store.GetProfileLikesAsync("200"));
                Assert.IsTrue((await store.GetProfileLikesAsync("200")).All(like => like.Count == 10));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnrestrictedProtocolWritesAlsoCountTowardAnOptionalDailyLimit()
    {
        await using var store = new AsukaStore();
        await store.SaveAsync(new User("sender", id: "100"));
        await store.SaveAsync(new User("friend", id: "200"));
        var now = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        await store.AddProfileLikesAsync("200", "100", 9, null, now, CancellationToken.None);
        await Assert.ThrowsAsync<PlatformException>(() =>
            store.AddProfileLikesAsync("200", "100", 2, 10, now, CancellationToken.None));
        Assert.AreEqual(9L, (await store.GetProfileLikesAsync("200"))[0].Count);
        await store.AddProfileLikesAsync("200", "100", 1, 10, now, CancellationToken.None);
        Assert.AreEqual(10L, (await store.GetProfileLikesAsync("200"))[0].Count);
    }

    [TestMethod]
    public async Task MigrationPreservesTheExistingLastSendDaysAllowance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-like-migration-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "state.db");
        var now = new DateTimeOffset(2026, 9, 8, 18, 0, 0, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(directory);
            // Schema 7 stored cumulative likes without a daily breakdown. Build the
            // historical input directly so future migrations cannot contaminate it.
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                VersionSevenStoreSchema.Create(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users(id,name,nickname,sex,sign,created_at)
                    VALUES('100','sender','','unknown','',$time),
                        ('200','friend','','unknown','',$time);
                    INSERT INTO profile_likes(user_id,sender_id,count,last_sent_at)
                    VALUES('200','100',9,$time);
                    """;
                command.Parameters.AddWithValue("$time", now.UtcTicks);
                _ = await command.ExecuteNonQueryAsync();
            }
            await using (var store = new AsukaStore(database))
            {
                Assert.AreEqual(9L, (await store.GetProfileLikesAsync("200"))[0].Count);
                await Assert.ThrowsAsync<PlatformException>(() =>
                    store.AddProfileLikesAsync("200", "100", 2, 10, now, CancellationToken.None));
                await store.AddProfileLikesAsync("200", "100", 1, 10, now, CancellationToken.None);
                Assert.AreEqual(10L, (await store.GetProfileLikesAsync("200"))[0].Count);
                await store.AddProfileLikesAsync("200", "100", 10, 10, now.AddDays(1), CancellationToken.None);
                Assert.AreEqual(20L, (await store.GetProfileLikesAsync("200"))[0].Count);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
