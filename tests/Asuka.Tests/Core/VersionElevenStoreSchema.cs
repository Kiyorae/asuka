using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

// Frozen schema 11 additions. Migration tests must not construct historical
// databases by calling the production migration being tested.
internal static class VersionElevenStoreSchema
{
    internal static void Create(SqliteConnection connection)
    {
        VersionTenStoreSchema.Create(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE group_honors (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                type INTEGER NOT NULL CHECK(type BETWEEN 0 AND 4),
                description TEXT NOT NULL CHECK(length(description) <= 1024),
                UNIQUE(group_id,type,user_id),
                FOREIGN KEY(group_id,user_id) REFERENCES group_members(group_id,user_id) ON DELETE CASCADE
            );
            CREATE TABLE group_current_talkative (
                group_id TEXT PRIMARY KEY NOT NULL,
                user_id TEXT NOT NULL,
                day_count INTEGER NOT NULL CHECK(day_count BETWEEN 1 AND 2147483647),
                FOREIGN KEY(group_id,user_id) REFERENCES group_members(group_id,user_id) ON DELETE CASCADE
            );
            PRAGMA user_version=11;
            """;
        command.ExecuteNonQuery();
    }
}
