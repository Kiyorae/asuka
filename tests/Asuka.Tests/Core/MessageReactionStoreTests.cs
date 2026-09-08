using Asuka.Core;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class MessageReactionStoreTests
{
    [TestMethod]
    public async Task ReactionsSurviveReopenAndAreScopedToTheSelectedChat()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "asuka.sqlite3");
        try
        {
            string groupMessageId;
            string privateMessageId;
            var groupChat = new Chat(ChatScene.Group, "50001", "10003");
            await using (var store = new AsukaStore(path))
            {
                await MessageInteractionTests.SeedAsync(store);
                var groupMessage = await AppendAsync(store, groupChat);
                var privateMessage = await AppendAsync(store, new Chat(ChatScene.Friend, "10002", "10003"));
                groupMessageId = groupMessage.Id;
                privateMessageId = privateMessage.Id;
                Assert.IsTrue(await store.SetMessageReactionAsync(groupMessageId, "10001", "66", true));
                Assert.IsFalse(await store.SetMessageReactionAsync(groupMessageId, "10001", "66", true));
                await store.SetMessageReactionAsync(groupMessageId, "10002", "128077", true, "emoji");
                await store.SetMessageReactionAsync(privateMessageId, "10002", "66", true);
            }

            await using var reopened = new AsukaStore(path);
            var expected = new[]
            {
                new MessageReactionState(groupMessageId, "10002", "128077", "emoji"),
                new MessageReactionState(groupMessageId, "10001", "66"),
            };
            CollectionAssert.AreEqual(expected, (await reopened.GetMessageReactionsAsync(groupMessageId)).ToArray());
            CollectionAssert.AreEqual(expected, (await reopened.GetChatReactionsAsync(groupChat)).ToArray());
            Assert.HasCount(1, await reopened.GetMessageReactionsAsync(privateMessageId));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [TestMethod]
    public async Task VersionTwoMigrationPreservesMessagesAndAddsReactionPersistence()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "asuka.sqlite3");
        try
        {
            var message = new Message(ChatScene.Group, "50001", "10002", "10003",
                [new TextSegment("retained")], MessageDirection.Outgoing, id: "20001", seq: 1,
                time: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                LegacyStoreSchema.Create(connection, 2);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users VALUES('10001', 'Owner', 'Owner', NULL, 'unknown', NULL, '', $time);
                    INSERT INTO users VALUES('10002', 'Sender', 'Sender', NULL, 'unknown', NULL, '', $time);
                    INSERT INTO users VALUES('10003', 'Bot', 'Bot', NULL, 'unknown', NULL, '', $time);
                    INSERT INTO groups VALUES('50001','Group',NULL,'',1,200,0,$time);
                    INSERT INTO group_members VALUES('50001','10001','','owner','',$time,NULL,NULL);
                    INSERT INTO group_members VALUES('50001','10002','','member','',$time,NULL,NULL);
                    INSERT INTO group_members VALUES('50001','10003','','member','',$time,NULL,NULL);
                    INSERT INTO messages VALUES('20001',1,'group','50001','10002','10003',
                        '[{"type":"text","text":"retained"}]',$time,'outgoing',NULL,NULL);
                    """;
                command.Parameters.AddWithValue("$time", message.Time.UtcTicks);
                command.ExecuteNonQuery();
            }

            await using var migrated = new AsukaStore(path);
            var preserved = await migrated.GetMessageAsync(message.Id);
            Assert.IsNotNull(preserved);
            Assert.AreEqual(message.Time, preserved.Time);
            Assert.AreEqual(message.Seq, preserved.Seq);
            Assert.AreEqual(message.Content.PlainText(), preserved.Content.PlainText());
            Assert.IsTrue(await migrated.SetMessageReactionAsync(message.Id, "10001", "66", true));
            Assert.HasCount(1, await migrated.GetMessageReactionsAsync(message.Id));
            using var verification = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            verification.Open();
            using var version = verification.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual((long)AsukaStore.CurrentSchemaVersion, version.ExecuteScalar());
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [TestMethod]
    public async Task RecallDeletesStoredReactionsAndKeepsOriginalMetadataOnRetry()
    {
        await using var store = new AsukaStore();
        await MessageInteractionTests.SeedAsync(store);
        var message = await AppendAsync(store, new Chat(ChatScene.Group, "50001", "10003"));
        await store.SetMessageReactionAsync(message.Id, "10001", "66", true);

        var first = await store.RecallMessageAsync(message.Id, "10002");
        var second = await store.RecallMessageAsync(message.Id, "10001");

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.RecalledAt, second.RecalledAt);
        Assert.AreEqual("10002", second.RecalledBy);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
        Assert.IsFalse(await store.SetMessageReactionAsync(message.Id, "10001", "66", true));
        // Restoring the message proves reactions were removed, rather than just
        // filtered out of the read result while the message was recalled.
        await store.SaveAsync(message);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
    }

    [TestMethod]
    public async Task ConcurrentRecallsFromSeparateConnectionsCommitOnlyOnce()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "asuka.sqlite3");
        try
        {
            await using var firstStore = new AsukaStore(path);
            await MessageInteractionTests.SeedAsync(firstStore);
            var message = await AppendAsync(firstStore, new Chat(ChatScene.Group, "50001", "10003"));
            await firstStore.SetMessageReactionAsync(message.Id, "10001", "66", true);
            await using var secondStore = new AsukaStore(path);
            var changes = 0;
            firstStore.Changed += (_, args) =>
            {
                if ((args.Changes & StoreChangeKind.Messages) != 0)
                {
                    Interlocked.Increment(ref changes);
                }
            };
            secondStore.Changed += (_, args) =>
            {
                if ((args.Changes & StoreChangeKind.Messages) != 0)
                {
                    Interlocked.Increment(ref changes);
                }
            };

            var results = await Task.WhenAll(
                Task.Run(() => firstStore.RecallMessageAsync(message.Id, "10001")),
                Task.Run(() => secondStore.RecallMessageAsync(message.Id, "10002")));

            var first = results[0];
            var second = results[1];
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual(first.RecalledAt, second.RecalledAt);
            Assert.AreEqual(first.RecalledBy, second.RecalledBy);
            Assert.AreEqual(1, changes);
            Assert.IsEmpty(await firstStore.GetMessageReactionsAsync(message.Id));
            Assert.IsEmpty(await secondStore.GetMessageReactionsAsync(message.Id));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SavingRecalledMessagesAlsoClearsTheirReactions()
    {
        await using var store = new AsukaStore();
        await MessageInteractionTests.SeedAsync(store);
        var message = await AppendAsync(store, new Chat(ChatScene.Group, "50001", "10003"));
        await store.SetMessageReactionAsync(message.Id, "10001", "66", true);

        await store.SaveAsync(message with { RecalledAt = DateTimeOffset.UtcNow, RecalledBy = "10002" });
        await store.SaveAsync(message);

        Assert.IsEmpty(await store.GetMessageReactionsAsync(message.Id));
    }

    [TestMethod]
    public async Task DeletingAConversationCascadesOnlyItsReactions()
    {
        await using var store = new AsukaStore();
        await MessageInteractionTests.SeedAsync(store);
        var groupChat = new Chat(ChatScene.Group, "50001", "10003");
        var deleted = await AppendAsync(store, groupChat);
        var retained = await AppendAsync(store, new Chat(ChatScene.Friend, "10002", "10003"));
        await store.SetMessageReactionAsync(deleted.Id, "10001", "66", true);
        await store.SetMessageReactionAsync(retained.Id, "10002", "66", true);

        await store.DeleteMessagesAsync(groupChat);
        await store.SaveAsync(deleted);

        Assert.IsEmpty(await store.GetMessageReactionsAsync(deleted.Id));
        Assert.HasCount(1, await store.GetMessageReactionsAsync(retained.Id));
    }

    [TestMethod]
    public async Task DeletingGroupsAndUsersCascadesTheirReactionState()
    {
        await using var store = new AsukaStore();
        await MessageInteractionTests.SeedAsync(store);
        var groupMessage = await AppendAsync(store, new Chat(ChatScene.Group, "50001", "10003"));
        var privateMessage = await AppendAsync(store, new Chat(ChatScene.Friend, "10002", "10003"));
        await store.SetMessageReactionAsync(groupMessage.Id, "10001", "66", true);
        await store.SetMessageReactionAsync(privateMessage.Id, "10001", "66", true);
        await store.SetMessageReactionAsync(privateMessage.Id, "10002", "66", true);

        await store.DeleteGroupAsync("50001");
        await store.SaveAsync(groupMessage);
        Assert.IsEmpty(await store.GetMessageReactionsAsync(groupMessage.Id));
        Assert.HasCount(2, await store.GetMessageReactionsAsync(privateMessage.Id));
        var messageChanges = 0;
        store.Changed += (_, args) =>
        {
            if ((args.Changes & StoreChangeKind.Messages) != 0)
            {
                messageChanges++;
            }
        };
        await store.DeleteUserAsync("10001");
        Assert.AreEqual(1, messageChanges);
        Assert.AreEqual("10002", (await store.GetMessageReactionsAsync(privateMessage.Id)).Single().UserId);
    }

    private static Task<Message> AppendAsync(AsukaStore store, Chat chat) =>
        store.AppendMessageAsync(new Message(
            chat.Scene, chat.PeerId, "10002", chat.SelfId, [new TextSegment("persisted")], MessageDirection.Incoming));

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-reactions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("asuka-reactions-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside this test's temporary scope.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
