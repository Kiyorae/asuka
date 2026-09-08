using System.Text.Json;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion6()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            PRAGMA user_version=6;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<IReadOnlyList<GroupAnnouncement>> GetGroupAnnouncementsAsync(
        string groupId, CancellationToken cancellationToken = default) =>
        WithGateAsync<IReadOnlyList<GroupAnnouncement>>(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, group_id, user_id, time, content, image_asset FROM group_announcements
                WHERE group_id=$group ORDER BY time DESC, id;
                """;
            Add(command, "$group", groupId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var result = new List<GroupAnnouncement>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                result.Add(new GroupAnnouncement(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    FromTimestamp(reader.GetInt64(3)), reader.GetString(4),
                    reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<Asset>(reader.GetString(5), PersistenceJsonOptions)));
            return result;
        }, cancellationToken);

    internal Task SaveGroupAnnouncementAsync(GroupAnnouncement announcement, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO group_announcements(id,group_id,user_id,time,content,image_asset)
                VALUES($id,$group,$user,$time,$content,$image);
                """;
            Add(command, "$id", announcement.Id);
            Add(command, "$group", announcement.GroupId);
            Add(command, "$user", announcement.UserId);
            Add(command, "$time", ToTimestamp(announcement.Time));
            Add(command, "$content", announcement.Content);
            Add(command, "$image", announcement.Image is null ? null : JsonSerializer.Serialize(announcement.Image, PersistenceJsonOptions));
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Groups, cancellationToken);

    internal Task<bool> DeleteGroupAnnouncementAsync(string groupId, string announcementId, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM group_announcements WHERE group_id=$group AND id=$id;";
            Add(command, "$group", groupId);
            Add(command, "$id", announcementId);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, StoreChangeKind.Groups, cancellationToken);

    public Task<GroupEssencePage> GetGroupEssenceMessagesAsync(
        string groupId, string selfId, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT m.id,m.seq,m.scene,m.peer_id,m.sender_id,m.self_id,m.content,m.time,m.direction,m.recalled_at,m.recalled_by,
                    m.anonymous_id,m.anonymous_name,m.anonymous_flag,
                    e.sender_name,e.operator_id,e.operator_name,e.operation_time
                FROM group_essence_messages e INNER JOIN messages m ON m.id=e.message_id
                WHERE m.scene='group' AND m.peer_id=$group AND m.self_id=$self AND m.recalled_at IS NULL
                ORDER BY e.operation_time DESC,m.seq DESC,m.id LIMIT $limit OFFSET $offset;
                """;
            Add(command, "$group", groupId);
            Add(command, "$self", selfId);
            Add(command, "$limit", (long)pageSize + 1);
            Add(command, "$offset", (long)pageIndex * pageSize);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var messages = new List<GroupEssenceMessage>();
            var isEnd = true;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (messages.Count == pageSize) { isEnd = false; break; }
                messages.Add(new GroupEssenceMessage(ReadMessage(reader), reader.GetString(14), reader.GetString(15),
                    reader.GetString(16), FromTimestamp(reader.GetInt64(17))));
            }
            return new GroupEssencePage(messages, isEnd);
        }, cancellationToken);
    }

    public Task<bool> IsGroupEssenceMessageAsync(string messageId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM group_essence_messages e INNER JOIN messages m ON m.id=e.message_id
                    WHERE e.message_id=$id AND m.scene='group' AND m.recalled_at IS NULL);
                """;
            Add(command, "$id", messageId);
            return Convert.ToBoolean(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlySet<string>> GetChatEssenceMessageIdsAsync(Chat chat, CancellationToken cancellationToken = default) =>
        WithGateAsync<IReadOnlySet<string>>(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT m.id FROM group_essence_messages e INNER JOIN messages m ON m.id=e.message_id
                WHERE m.scene=$scene AND m.peer_id=$peer AND m.self_id=$self AND m.recalled_at IS NULL;
                """;
            Add(command, "$scene", chat.Scene.ToStorageValue());
            Add(command, "$peer", chat.PeerId);
            Add(command, "$self", chat.SelfId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(reader.GetString(0));
            return ids;
        }, cancellationToken);

    internal async Task<bool> SetGroupEssenceMessageAsync(GroupEssenceMessage essence, bool isSet, CancellationToken cancellationToken)
    {
        var changed = await WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            using (var validate = _connection.CreateCommand())
            {
                validate.Transaction = transaction;
                validate.CommandText = """
                    SELECT COUNT(*) FROM messages WHERE id=$id AND scene='group' AND peer_id=$group
                        AND self_id=$self AND seq=$seq AND recalled_at IS NULL;
                    """;
                Add(validate, "$id", essence.Message.Id);
                Add(validate, "$group", essence.Message.PeerId);
                Add(validate, "$self", essence.Message.SelfId);
                Add(validate, "$seq", essence.Message.Seq);
                if (Convert.ToInt32(await validate.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
                    throw new PlatformException(PlatformError.MessageNotFound, "The active group message no longer exists");
            }
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = isSet ? """
                INSERT INTO group_essence_messages(message_id,sender_name,operator_id,operator_name,operation_time)
                VALUES($id,$sender,$operator,$operator_name,$time) ON CONFLICT(message_id) DO NOTHING;
                """ : "DELETE FROM group_essence_messages WHERE message_id=$id;";
            Add(command, "$id", essence.Message.Id);
            if (isSet)
            {
                Add(command, "$sender", essence.SenderName);
                Add(command, "$operator", essence.OperatorId);
                Add(command, "$operator_name", essence.OperatorName);
                Add(command, "$time", ToTimestamp(essence.OperationTime));
            }
            var result = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (changed) Publish(StoreChangeKind.Messages | StoreChangeKind.Groups);
        return changed;
    }
}
