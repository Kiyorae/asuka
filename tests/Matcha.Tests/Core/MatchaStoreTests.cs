using Microsoft.VisualStudio.TestTools.UnitTesting;
using Matcha.Core;
using Microsoft.Data.Sqlite;

namespace Matcha.Tests.Core;

[TestClass]
public sealed class MatchaStoreTests
{
    [TestMethod]
    public async Task ConcurrentDisposeIsIdempotent()
    {
        var store = new MatchaStore();
        await Task.WhenAll(store.DisposeAsync().AsTask(), store.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task MessageContentAndHistoryRoundTripWithDensePerChatSequences()
    {
        await using var store = new Matcha.Core.MatchaStore();
        var fixture = await SeedAsync(store);
        var chat = new Matcha.Core.Chat(Matcha.Core.ChatScene.Group, fixture.Group.Id, fixture.Bob.Id);
        var content = new Matcha.Core.MessageSegment[]
        {
            new Matcha.Core.ReplySegment("70001"),
            new Matcha.Core.MentionSegment(fixture.Bob.Id),
            new Matcha.Core.TextSegment("hello 🍵"),
            new Matcha.Core.UnsupportedSegment("market_face", Matcha.Core.JsonValue.Parse("{\"key\":\"value\"}")),
        };

        for (var index = 0; index < 10; index++)
        {
            _ = await store.AppendMessageAsync(new Matcha.Core.Message(
                chat.Scene,
                chat.PeerId,
                fixture.Alice.Id,
                chat.SelfId,
                index == 0 ? content : [new Matcha.Core.TextSegment($"message {index + 1}")],
                Matcha.Core.MessageDirection.Outgoing));
        }

        var messages = await store.GetMessagesAsync(chat);
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(value => (long)value).ToArray(), messages.Select(message => message.Seq).ToArray());
        CollectionAssert.AreEqual(content, messages[0].Content.ToArray());
        Assert.AreEqual("hello 🍵", messages[0].Content.PlainText());
        Assert.AreEqual("70001", messages[0].Content.ReplyTarget());

        var latest = await store.GetHistoryAsync(chat.Scene, chat.PeerId, chat.SelfId, limit: 4);
        CollectionAssert.AreEqual(new long[] { 7, 8, 9, 10 }, latest.Select(message => message.Seq).ToArray());
        var previous = await store.GetHistoryAsync(chat.Scene, chat.PeerId, chat.SelfId, startSeq: 6, limit: 4);
        CollectionAssert.AreEqual(new long[] { 3, 4, 5, 6 }, previous.Select(message => message.Seq).ToArray());
    }

    [TestMethod]
    public async Task ConcurrentAppendsAllocateUniqueDenseSequences()
    {
        await using var store = new Matcha.Core.MatchaStore();
        var fixture = await SeedAsync(store);
        var messages = Enumerable.Range(0, 40).Select(index => new Matcha.Core.Message(
            Matcha.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Alice.Id,
            fixture.Bob.Id,
            [new Matcha.Core.TextSegment(index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            Matcha.Core.MessageDirection.Outgoing));

        var stored = await Task.WhenAll(messages.Select(message => store.AppendMessageAsync(message)));
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 40).Select(value => (long)value).ToArray(),
            stored.Select(message => message.Seq).Order().ToArray());
    }

    [TestMethod]
    public async Task ObservationsEmitInitialAndRelevantCommittedUpdates()
    {
        await using var store = new Matcha.Core.MatchaStore();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = store.ObserveUsersAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.IsTrue(await iterator.MoveNextAsync());
        Assert.IsEmpty(iterator.Current);

        await store.SaveAsync(new Matcha.Core.User("Alice", id: "10001"), timeout.Token);
        await store.SaveAsync(new Matcha.Core.Asset("a", "a.bin"), timeout.Token);

        Assert.IsTrue(await iterator.MoveNextAsync());
        Assert.HasCount(1, iterator.Current);
        Assert.AreEqual("10001", iterator.Current[0].Id);
    }

    [TestMethod]
    public async Task GroupDeleteCascadesOnlyGroupScopedData()
    {
        await using var store = new Matcha.Core.MatchaStore();
        var fixture = await SeedAsync(store);
        var other = new Matcha.Core.Group("Other", id: "50002");
        await store.SaveAsync(other);
        await store.SaveAsync(new Matcha.Core.GroupMember(other.Id, fixture.Bob.Id));

        var target = await store.AppendMessageAsync(new Matcha.Core.Message(
            Matcha.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Alice.Id,
            fixture.Bob.Id,
            [new Matcha.Core.TextSegment("delete")],
            Matcha.Core.MessageDirection.Outgoing));
        var retained = await store.AppendMessageAsync(new Matcha.Core.Message(
            Matcha.Core.ChatScene.Group,
            other.Id,
            fixture.Bob.Id,
            fixture.Bob.Id,
            [new Matcha.Core.TextSegment("retain")],
            Matcha.Core.MessageDirection.Incoming));
        var request = new Matcha.Core.PendingRequest(
            Matcha.Core.RequestKind.GroupJoin,
            fixture.Alice.Id,
            fixture.Bob.Id,
            fixture.Group.Id);
        await store.SaveAsync(request);

        await store.DeleteGroupAsync(fixture.Group.Id);

        Assert.IsNull(await store.GetGroupAsync(fixture.Group.Id));
        Assert.IsNull(await store.GetMessageAsync(target.Id));
        Assert.IsNull(await store.GetRequestAsync(request.Id));
        Assert.IsNotNull(await store.GetMessageAsync(retained.Id));
    }

    [TestMethod]
    public async Task PersistentStoreEnablesWalAndSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"matcha-db-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "matcha.sqlite3");
        try
        {
            await using (var store = new Matcha.Core.MatchaStore(path))
            {
                await store.SaveAsync(new Matcha.Core.User("Alice", id: "10001"));
            }

            await using var reopened = new Matcha.Core.MatchaStore(path);
            Assert.AreEqual("Alice", (await reopened.GetUserAsync("10001"))?.Name);
            using var verification = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            verification.Open();
            using var journalMode = verification.CreateCommand();
            journalMode.CommandText = "PRAGMA journal_mode;";
            Assert.AreEqual("wal", journalMode.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task VersionOneMillisecondTimestampsAreMigratedToTicks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"matcha-v1-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "matcha.sqlite3");
        Directory.CreateDirectory(directory);
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE users(
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, nickname TEXT NOT NULL,
                        avatar TEXT, sex TEXT NOT NULL, age INTEGER, sign TEXT NOT NULL,
                        created_at INTEGER NOT NULL);
                    CREATE TABLE groups(created_at INTEGER NOT NULL);
                    CREATE TABLE group_members(
                        joined_at INTEGER NOT NULL, last_sent_at INTEGER, muted_until INTEGER);
                    CREATE TABLE friendships(created_at INTEGER NOT NULL);
                    CREATE TABLE messages(time INTEGER NOT NULL, recalled_at INTEGER);
                    CREATE TABLE pending_requests(time INTEGER NOT NULL);
                    INSERT INTO users VALUES('10001', 'Legacy', 'Legacy', NULL, 'unknown', NULL, '', 1000);
                    PRAGMA user_version=1;
                    """;
                _ = command.ExecuteNonQuery();
            }

            await using var store = new MatchaStore(path);
            var user = await store.GetUserAsync("10001");
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1000), user?.CreatedAt);

            using var verification = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            verification.Open();
            using var version = verification.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(2L, version.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<(Matcha.Core.User Alice, Matcha.Core.User Bob, Matcha.Core.Group Group)> SeedAsync(
        Matcha.Core.MatchaStore store)
    {
        var alice = new Matcha.Core.User("Alice", id: "10001");
        var bob = new Matcha.Core.User("Bob", id: "10002");
        var group = new Matcha.Core.Group("Test", id: "50001");
        await store.SaveAsync(alice);
        await store.SaveAsync(bob);
        await store.SaveAsync(group);
        await store.SaveAsync(new Matcha.Core.GroupMember(group.Id, alice.Id, role: Matcha.Core.GroupRole.Owner));
        await store.SaveAsync(new Matcha.Core.GroupMember(group.Id, bob.Id));
        return (alice, bob, group);
    }
}
