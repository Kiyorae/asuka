using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

// Frozen schema 7 additions to the historical schema 2 fixture. This fixture must
// not invoke production migrations: later schemas are the output under test.
internal static class VersionSevenStoreSchema
{
    internal static void Create(SqliteConnection connection)
    {
        LegacyStoreSchema.Create(connection, 2);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE message_reactions (
                message_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                reaction TEXT NOT NULL,
                reaction_type TEXT NOT NULL,
                PRIMARY KEY(message_id, user_id, reaction_type, reaction),
                FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE INDEX ix_message_reactions_user ON message_reactions(user_id);
            CREATE TRIGGER clear_recalled_message_reactions
                AFTER UPDATE OF recalled_at ON messages WHEN NEW.recalled_at IS NOT NULL
            BEGIN
                DELETE FROM message_reactions WHERE message_id=NEW.id;
            END;
            CREATE TABLE shared_folders (
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                parent_folder_id TEXT NOT NULL DEFAULT '/',
                name TEXT NOT NULL,
                creator_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                last_modified_at INTEGER NOT NULL,
                UNIQUE(group_id, parent_folder_id, name)
            );
            CREATE INDEX ix_shared_folders_group ON shared_folders(group_id, parent_folder_id);
            CREATE TABLE shared_files (
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NULL REFERENCES groups(id) ON DELETE CASCADE,
                recipient_id TEXT NULL REFERENCES users(id) ON DELETE CASCADE,
                uploader_id TEXT NOT NULL,
                asset_id TEXT NOT NULL REFERENCES assets(id),
                name TEXT NOT NULL,
                parent_folder_id TEXT NOT NULL DEFAULT '/',
                uploaded_at INTEGER NOT NULL,
                expires_at INTEGER NULL,
                downloaded_times INTEGER NOT NULL DEFAULT 0 CHECK(downloaded_times >= 0),
                file_hash TEXT NOT NULL DEFAULT '',
                CHECK((group_id IS NOT NULL AND recipient_id IS NULL) OR
                    (group_id IS NULL AND recipient_id IS NOT NULL))
            );
            CREATE INDEX ix_shared_files_group ON shared_files(group_id, parent_folder_id, uploaded_at);
            CREATE INDEX ix_shared_files_private ON shared_files(recipient_id, uploader_id, uploaded_at);
            CREATE TABLE peer_states (
                self_id TEXT NOT NULL,
                scene TEXT NOT NULL CHECK(scene IN ('friend','group','temp')),
                peer_id TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0 CHECK(is_pinned IN (0,1)),
                last_read_seq INTEGER NOT NULL DEFAULT 0 CHECK(last_read_seq >= 0),
                PRIMARY KEY(self_id, scene, peer_id),
                FOREIGN KEY(self_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE TABLE profile_likes (
                user_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                count INTEGER NOT NULL CHECK(typeof(count)='integer' AND count > 0),
                last_sent_at INTEGER NOT NULL,
                PRIMARY KEY(user_id, sender_id),
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
                FOREIGN KEY(sender_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE TRIGGER clear_removed_peer_user AFTER DELETE ON users
            BEGIN
                DELETE FROM peer_states WHERE scene IN ('friend','temp') AND peer_id=OLD.id;
            END;
            CREATE TRIGGER clear_removed_peer_group AFTER DELETE ON groups
            BEGIN
                DELETE FROM peer_states WHERE scene='group' AND peer_id=OLD.id;
            END;
            CREATE TRIGGER clear_departed_peer_group AFTER DELETE ON group_members
            BEGIN
                DELETE FROM peer_states WHERE scene='group' AND peer_id=OLD.group_id AND self_id=OLD.user_id;
            END;
            CREATE TRIGGER clear_removed_peer_friend AFTER DELETE ON friendships
            BEGIN
                DELETE FROM peer_states WHERE scene='friend' AND peer_id=OLD.friend_id AND self_id=OLD.user_id;
            END;
            CREATE TRIGGER reset_cleared_peer_read_position AFTER DELETE ON messages
                WHEN NOT EXISTS (SELECT 1 FROM messages
                    WHERE self_id=OLD.self_id AND scene=OLD.scene AND peer_id=OLD.peer_id)
            BEGIN
                UPDATE peer_states SET last_read_seq=0
                    WHERE self_id=OLD.self_id AND scene=OLD.scene AND peer_id=OLD.peer_id;
            END;
            CREATE TABLE group_announcements (
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                user_id TEXT NOT NULL,
                time INTEGER NOT NULL,
                content TEXT NOT NULL,
                image_asset TEXT NULL
            );
            CREATE INDEX ix_group_announcements_group ON group_announcements(group_id, time DESC, id);
            CREATE TABLE group_essence_messages (
                message_id TEXT PRIMARY KEY NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                sender_name TEXT NOT NULL,
                operator_id TEXT NOT NULL,
                operator_name TEXT NOT NULL,
                operation_time INTEGER NOT NULL
            );
            CREATE TRIGGER clear_recalled_group_essence AFTER UPDATE ON messages
                WHEN NEW.recalled_at IS NOT NULL OR NEW.scene != 'group'
                    OR NEW.peer_id != OLD.peer_id OR NEW.self_id != OLD.self_id OR NEW.seq != OLD.seq
            BEGIN
                DELETE FROM group_essence_messages WHERE message_id=NEW.id;
            END;
            ALTER TABLE pending_requests ADD COLUMN target_user_id TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN source_group_id TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN is_filtered INTEGER NOT NULL DEFAULT 0 CHECK(is_filtered IN (0,1));
            ALTER TABLE pending_requests ADD COLUMN via TEXT NOT NULL DEFAULT 'asuka';
            ALTER TABLE pending_requests ADD COLUMN resolved_by TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN notification_seq INTEGER NOT NULL DEFAULT 0;
            UPDATE pending_requests SET notification_seq=rowid;
            CREATE TABLE request_notification_sequence (
                id INTEGER PRIMARY KEY CHECK(id=1),
                next_seq INTEGER NOT NULL CHECK(next_seq > 0));
            INSERT INTO request_notification_sequence(id, next_seq)
                SELECT 1, COALESCE(MAX(notification_seq), 0) + 1 FROM pending_requests;
            CREATE UNIQUE INDEX ux_requests_notification_seq ON pending_requests(notification_seq)
                WHERE notification_seq > 0;
            CREATE INDEX ix_requests_self_filter_time ON pending_requests(self_id, is_filtered, time DESC);
            PRAGMA user_version=7;
            """;
        command.ExecuteNonQuery();
    }
}
