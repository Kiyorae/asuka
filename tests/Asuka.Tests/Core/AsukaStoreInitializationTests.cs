using Asuka.Core;
using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AsukaStoreInitializationTests
{
    [TestMethod]
    public void UnsupportedSchemaFailureImmediatelyReleasesTheDatabaseFile()
    {
        WithDatabase(database =>
        {
            using (var connection = OpenConnection(database))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=2147483647;";
                command.ExecuteNonQuery();
            }

            var failure = Assert.ThrowsExactly<StorePersistenceException>(() =>
            {
                using var store = new AsukaStore(database);
            });
            StringAssert.Contains(failure.Message, "newer than this build supports");
            AssertFileReleased(database);
        });
    }

    [TestMethod]
    public void FailedMigrationImmediatelyReleasesTheDatabaseAndRollsBack()
    {
        WithDatabase(database =>
        {
            using (var connection = OpenConnection(database))
            {
                VersionSevenStoreSchema.Create(connection);
                using var command = connection.CreateCommand();
                // Force migration 8 to fail after opening its transaction.
                command.CommandText = "CREATE TABLE profile_like_days (sentinel INTEGER);";
                command.ExecuteNonQuery();
            }

            var failure = Assert.ThrowsExactly<SqliteException>(() =>
            {
                using var store = new AsukaStore(database);
            });
            Assert.AreEqual(1, failure.SqliteErrorCode);
            StringAssert.Contains(failure.Message, "profile_like_days");
            AssertFileReleased(database);

            using (var connection = OpenConnection(database))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                Assert.AreEqual(7L, command.ExecuteScalar());
                command.CommandText = "DROP TABLE profile_like_days;";
                command.ExecuteNonQuery();
            }
            using var recovered = new AsukaStore(database);
            Assert.AreEqual(database, recovered.DatabasePath);
        });
    }

    private static SqliteConnection OpenConnection(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void AssertFileReleased(string database)
    {
        // In particular, no GC or pool clearing may conceal a leaked native handle.
        using (var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsGreaterThan(0L, exclusive.Length);
        File.Move(database, database + ".released");
        File.Move(database + ".released", database);
    }

    private static void WithDatabase(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-initialization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            test(Path.Combine(directory, "state.db"));
        }
        finally
        {
            // Preserve the assertion/initialization error if a regression leaks a
            // handle; cleanup must not replace it with Directory.Delete's error.
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
