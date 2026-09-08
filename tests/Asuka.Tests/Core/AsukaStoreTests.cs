using Microsoft.VisualStudio.TestTools.UnitTesting;
using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AsukaStoreTests
{
    [TestMethod]
    public async Task ConcurrentDisposeIsIdempotent()
    {
        var store = new AsukaStore();
        await Task.WhenAll(store.DisposeAsync().AsTask(), store.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task MessageContentAndHistoryRoundTripWithDensePerChatSequences()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        var chat = new Asuka.Core.Chat(Asuka.Core.ChatScene.Group, fixture.Group.Id, fixture.Bob.Id);
        var content = new Asuka.Core.MessageSegment[]
        {
            new Asuka.Core.ReplySegment("70001"),
            new Asuka.Core.MentionSegment(fixture.Bob.Id),
            new Asuka.Core.TextSegment("hello 🍵"),
            new Asuka.Core.UnsupportedSegment("market_face", Asuka.Core.JsonValue.Parse("{\"key\":\"value\"}")),
        };

        for (var index = 0; index < 10; index++)
        {
            _ = await store.AppendMessageAsync(new Asuka.Core.Message(
                chat.Scene,
                chat.PeerId,
                fixture.Alice.Id,
                chat.SelfId,
                index == 0 ? content : [new Asuka.Core.TextSegment($"message {index + 1}")],
                Asuka.Core.MessageDirection.Outgoing));
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
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        var messages = Enumerable.Range(0, 40).Select(index => new Asuka.Core.Message(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Alice.Id,
            fixture.Bob.Id,
            [new Asuka.Core.TextSegment(index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            Asuka.Core.MessageDirection.Outgoing));

        var stored = await Task.WhenAll(messages.Select(message => store.AppendMessageAsync(message)));
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 40).Select(value => (long)value).ToArray(),
            stored.Select(message => message.Seq).Order().ToArray());
    }

    [TestMethod]
    public async Task ObservationsEmitInitialAndRelevantCommittedUpdates()
    {
        await using var store = new Asuka.Core.AsukaStore();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = store.ObserveUsersAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.IsTrue(await iterator.MoveNextAsync());
        Assert.IsEmpty(iterator.Current);

        await store.SaveAsync(new Asuka.Core.User("Alice", id: "10001"), timeout.Token);
        await store.SaveAsync(new Asuka.Core.Asset("a", "a.bin"), timeout.Token);

        Assert.IsTrue(await iterator.MoveNextAsync());
        Assert.HasCount(1, iterator.Current);
        Assert.AreEqual("10001", iterator.Current[0].Id);
    }

    [TestMethod]
    public async Task GroupDeleteCascadesOnlyGroupScopedData()
    {
        await using var store = new Asuka.Core.AsukaStore();
        var fixture = await SeedAsync(store);
        var other = new Asuka.Core.Group("Other", id: "50002");
        await store.SaveAsync(other);
        await store.SaveAsync(new Asuka.Core.GroupMember(other.Id, fixture.Bob.Id));

        var target = await store.AppendMessageAsync(new Asuka.Core.Message(
            Asuka.Core.ChatScene.Group,
            fixture.Group.Id,
            fixture.Alice.Id,
            fixture.Bob.Id,
            [new Asuka.Core.TextSegment("delete")],
            Asuka.Core.MessageDirection.Outgoing));
        var retained = await store.AppendMessageAsync(new Asuka.Core.Message(
            Asuka.Core.ChatScene.Group,
            other.Id,
            fixture.Bob.Id,
            fixture.Bob.Id,
            [new Asuka.Core.TextSegment("retain")],
            Asuka.Core.MessageDirection.Incoming));
        var request = new Asuka.Core.PendingRequest(
            Asuka.Core.RequestKind.GroupJoin,
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
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-db-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "asuka.sqlite3");
        try
        {
            await using (var store = new Asuka.Core.AsukaStore(path))
            {
                await store.SaveAsync(new Asuka.Core.User("Alice", id: "10001"));
            }

            await using var reopened = new Asuka.Core.AsukaStore(path);
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
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-v1-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "asuka.sqlite3");
        Directory.CreateDirectory(directory);
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                LegacyStoreSchema.Create(connection, 1);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users VALUES('10001', 'Legacy', 'Legacy', NULL, 'unknown', NULL, '', 1000);
                    """;
                _ = command.ExecuteNonQuery();
            }

            await using var store = new AsukaStore(path);
            var user = await store.GetUserAsync("10001");
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1000), user?.CreatedAt);

            using var verification = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            verification.Open();
            using var version = verification.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual((long)AsukaStore.CurrentSchemaVersion, version.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<(Asuka.Core.User Alice, Asuka.Core.User Bob, Asuka.Core.Group Group)> SeedAsync(
        Asuka.Core.AsukaStore store)
    {
        var alice = new Asuka.Core.User("Alice", id: "10001");
        var bob = new Asuka.Core.User("Bob", id: "10002");
        var group = new Asuka.Core.Group("Test", id: "50001");
        await store.SaveAsync(alice);
        await store.SaveAsync(bob);
        await store.SaveAsync(group);
        await store.SaveAsync(new Asuka.Core.GroupMember(group.Id, alice.Id, role: Asuka.Core.GroupRole.Owner));
        await store.SaveAsync(new Asuka.Core.GroupMember(group.Id, bob.Id));
        return (alice, bob, group);
    }
}
