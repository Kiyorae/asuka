namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion5()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            PRAGMA user_version=5;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<PeerState> GetPeerStateAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT is_pinned, last_read_seq FROM peer_states
                WHERE self_id=$self_id AND scene=$scene AND peer_id=$peer_id;
                """;
            AddPeerChatParameters(command, chat);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? new PeerState(chat, reader.GetBoolean(0), reader.GetInt64(1))
                : new PeerState(chat);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PeerState>> GetPeerStatesAsync(string selfId, CancellationToken cancellationToken = default) =>
        WithGateAsync<IReadOnlyList<PeerState>>(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT scene, peer_id, is_pinned, last_read_seq FROM peer_states
                WHERE self_id=$self_id ORDER BY scene, peer_id;
                """;
            Add(command, "$self_id", selfId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var states = new List<PeerState>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                states.Add(new PeerState(new Chat(EnumStorage.ParseStorageValue<ChatScene>(reader.GetString(0)),
                    reader.GetString(1), selfId), reader.GetBoolean(2), reader.GetInt64(3)));
            }
            return states;
        }, cancellationToken);

    internal async Task<bool> SetPeerPinAsync(Chat chat, bool isPinned, CancellationToken cancellationToken)
    {
        var changed = await WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO peer_states(self_id, scene, peer_id, is_pinned)
                    SELECT $self_id, $scene, $peer_id, $is_pinned
                    WHERE $is_pinned=1 OR EXISTS (SELECT 1 FROM peer_states
                        WHERE self_id=$self_id AND scene=$scene AND peer_id=$peer_id)
                ON CONFLICT(self_id, scene, peer_id) DO UPDATE SET is_pinned=excluded.is_pinned
                    WHERE peer_states.is_pinned != excluded.is_pinned;
                """;
            AddPeerChatParameters(command, chat);
            Add(command, "$is_pinned", isPinned);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0;
        }, cancellationToken).ConfigureAwait(false);
        if (changed) Publish(StoreChangeKind.Conversations);
        return changed;
    }

    internal Task MarkMessageAsReadAsync(Chat chat, long messageSequence, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO peer_states(self_id, scene, peer_id, last_read_seq)
                VALUES($self_id, $scene, $peer_id, $seq)
                ON CONFLICT(self_id, scene, peer_id) DO UPDATE SET last_read_seq=excluded.last_read_seq
                    WHERE peer_states.last_read_seq < excluded.last_read_seq;
                """;
            AddPeerChatParameters(command, chat);
            Add(command, "$seq", messageSequence);
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Conversations, cancellationToken);

    public Task<IReadOnlyList<ProfileLikeState>> GetProfileLikesAsync(string userId, CancellationToken cancellationToken = default) =>
        WithGateAsync<IReadOnlyList<ProfileLikeState>>(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT sender_id, count, last_sent_at FROM profile_likes WHERE user_id=$user_id ORDER BY sender_id;";
            Add(command, "$user_id", userId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var likes = new List<ProfileLikeState>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                likes.Add(new ProfileLikeState(userId, reader.GetString(0), reader.GetInt64(1), FromTimestamp(reader.GetInt64(2))));
            return likes;
        }, cancellationToken);

    internal Task AddProfileLikesAsync(string userId, string senderId, int count, CancellationToken cancellationToken) =>
        AddProfileLikesAsync(userId, senderId, count, null, DateTimeOffset.UtcNow, cancellationToken);

    private static void AddPeerChatParameters(Microsoft.Data.Sqlite.SqliteCommand command, Chat chat)
    {
        Add(command, "$self_id", chat.SelfId);
        Add(command, "$scene", chat.Scene.ToStorageValue());
        Add(command, "$peer_id", chat.PeerId);
    }
}
