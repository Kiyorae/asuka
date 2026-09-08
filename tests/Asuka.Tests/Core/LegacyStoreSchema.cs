using Microsoft.Data.Sqlite;

namespace Asuka.Tests.Core;

// Frozen historical schema. Future migrations must not redefine their own input fixture.
internal static class LegacyStoreSchema
{
    private const string VersionOne = """
        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            nickname TEXT NOT NULL,
            avatar TEXT NULL,
            sex TEXT NOT NULL,
            age INTEGER NULL,
            sign TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_users_created_at ON users(created_at);

        CREATE TABLE IF NOT EXISTS groups (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            avatar TEXT NULL,
            intro TEXT NOT NULL,
            level INTEGER NOT NULL,
            max_member_count INTEGER NOT NULL,
            whole_muted INTEGER NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_groups_created_at ON groups(created_at);

        CREATE TABLE IF NOT EXISTS group_members (
            group_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            card TEXT NOT NULL,
            role TEXT NOT NULL,
            title TEXT NOT NULL,
            joined_at INTEGER NOT NULL,
            last_sent_at INTEGER NULL,
            muted_until INTEGER NULL,
            PRIMARY KEY(group_id, user_id),
            FOREIGN KEY(group_id) REFERENCES groups(id) ON DELETE CASCADE,
            FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_members_group_joined ON group_members(group_id, joined_at);
        CREATE INDEX IF NOT EXISTS ix_members_user ON group_members(user_id);

        CREATE TABLE IF NOT EXISTS friendships (
            user_id TEXT NOT NULL,
            friend_id TEXT NOT NULL,
            remark TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            PRIMARY KEY(user_id, friend_id),
            FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
            FOREIGN KEY(friend_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_friendships_user_created ON friendships(user_id, created_at);
        CREATE INDEX IF NOT EXISTS ix_friendships_friend ON friendships(friend_id);

        CREATE TABLE IF NOT EXISTS messages (
            id TEXT PRIMARY KEY NOT NULL,
            seq INTEGER NOT NULL,
            scene TEXT NOT NULL,
            peer_id TEXT NOT NULL,
            sender_id TEXT NOT NULL,
            self_id TEXT NOT NULL,
            content TEXT NOT NULL,
            time INTEGER NOT NULL,
            direction TEXT NOT NULL,
            recalled_at INTEGER NULL,
            recalled_by TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_messages_chat_seq ON messages(scene, peer_id, self_id, seq);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_chat_seq
            ON messages(scene, peer_id, self_id, seq) WHERE seq > 0;
        CREATE INDEX IF NOT EXISTS ix_messages_self_time ON messages(self_id, time);
        CREATE INDEX IF NOT EXISTS ix_messages_time ON messages(time);

        CREATE TABLE IF NOT EXISTS pending_requests (
            id TEXT PRIMARY KEY NOT NULL,
            flag TEXT NOT NULL,
            kind TEXT NOT NULL,
            requester_id TEXT NOT NULL,
            group_id TEXT NULL,
            self_id TEXT NOT NULL,
            comment TEXT NOT NULL,
            time INTEGER NOT NULL,
            resolution_state TEXT NULL,
            resolution_reason TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_requests_flag ON pending_requests(flag);
        CREATE INDEX IF NOT EXISTS ix_requests_self_time ON pending_requests(self_id, time);

        CREATE TABLE IF NOT EXISTS assets (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL,
            mime_type TEXT NULL,
            byte_count INTEGER NOT NULL,
            source_kind TEXT NOT NULL,
            source_value TEXT NULL
        );

        PRAGMA user_version=1;
        """;

    internal static void Create(SqliteConnection connection, int version)
    {
        if (version is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(version));
        using var command = connection.CreateCommand();
        command.CommandText = version == 1 ? VersionOne : VersionOne.Replace("PRAGMA user_version=1;", "PRAGMA user_version=2;", StringComparison.Ordinal);
        command.ExecuteNonQuery();
    }
}