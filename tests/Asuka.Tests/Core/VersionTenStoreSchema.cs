using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

// Frozen schema 8-10 additions. Production migrations must not define their own input.
internal static class VersionTenStoreSchema
{
    internal static void Create(SqliteConnection connection)
    {
        VersionSevenStoreSchema.Create(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE profile_like_days (
                user_id TEXT NOT NULL, sender_id TEXT NOT NULL, day_number INTEGER NOT NULL,
                count INTEGER NOT NULL CHECK(typeof(count)='integer' AND count > 0),
                PRIMARY KEY(user_id,sender_id,day_number),
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
                FOREIGN KEY(sender_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE TABLE group_notification_history (
                notification_seq INTEGER PRIMARY KEY CHECK(notification_seq > 0),
                self_id TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('admin_change','kick','quit')),
                group_id TEXT NOT NULL, target_user_id TEXT NOT NULL, operator_id TEXT NULL,
                is_set INTEGER NULL CHECK(is_set IS NULL OR is_set IN (0,1)), time INTEGER NOT NULL,
                CHECK((kind='admin_change' AND operator_id IS NOT NULL AND is_set IS NOT NULL)
                    OR (kind='kick' AND operator_id IS NOT NULL AND is_set IS NULL)
                    OR (kind='quit' AND operator_id IS NULL AND is_set IS NULL))
            );
            CREATE INDEX ix_group_notification_history_self_seq ON group_notification_history(self_id,notification_seq DESC);
            CREATE INDEX ix_requests_group_notification_page ON pending_requests(self_id,is_filtered,notification_seq DESC)
                WHERE kind IN ('groupJoin','groupInvitedJoin') AND group_id IS NOT NULL;
            CREATE TRIGGER reject_request_group_notification_collision BEFORE INSERT ON pending_requests
                WHEN EXISTS(SELECT 1 FROM group_notification_history WHERE notification_seq=NEW.notification_seq)
            BEGIN
                SELECT RAISE(ABORT, 'The notification sequence is already used by group history');
            END;
            CREATE TABLE custom_faces (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                self_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                asset_id TEXT NOT NULL REFERENCES assets(id), added_at INTEGER NOT NULL,
                UNIQUE(self_id,asset_id)
            );
            CREATE INDEX custom_faces_account_order ON custom_faces(self_id,sequence);
            PRAGMA user_version=10;
            """;
        command.ExecuteNonQuery();
    }
}
