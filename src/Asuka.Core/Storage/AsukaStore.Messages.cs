using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    public Task<Message> AppendMessageAsync(Message message, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var transaction = _connection.BeginTransaction(deferred: false);
                var sqliteTransaction = transaction;
                var stored = message;
                if (stored.Seq == 0)
                {
                    using var sequenceCommand = _connection.CreateCommand();
                    sequenceCommand.Transaction = sqliteTransaction;
                    sequenceCommand.CommandText = """
                        SELECT COALESCE(MAX(seq), 0) + 1 FROM messages
                        WHERE scene=$scene AND peer_id=$peer_id AND self_id=$self_id;
                        """;
                    Add(sequenceCommand, "$scene", stored.Scene.ToStorageValue());
                    Add(sequenceCommand, "$peer_id", stored.PeerId);
                    Add(sequenceCommand, "$self_id", stored.SelfId);
                    var next = Convert.ToInt64(
                        await sequenceCommand.ExecuteScalarAsync(token).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                    stored = stored with { Seq = next };
                }

                await UpsertMessageAsync(stored, sqliteTransaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return stored;
            },
            StoreChangeKind.Messages | StoreChangeKind.Conversations,
            cancellationToken);

    public Task SaveAsync(Message message, CancellationToken cancellationToken = default) =>
        WriteAsync(
            token => UpsertMessageAsync(message, null, token),
            StoreChangeKind.Messages | StoreChangeKind.Conversations,
            cancellationToken);

    public Task<Message?> GetMessageAsync(string id, CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"{MessageSelect} WHERE id=$id LIMIT 1;";
                Add(command, "$id", id);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadMessage(reader) : null;
            },
            cancellationToken);

    public Task<Message?> GetMessageAsync(
        ChatScene scene,
        string peerId,
        long seq,
        string selfId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"""
                    {MessageSelect}
                    WHERE scene=$scene AND peer_id=$peer_id AND seq=$seq AND self_id=$self_id
                    LIMIT 1;
                    """;
                Add(command, "$scene", scene.ToStorageValue());
                Add(command, "$peer_id", peerId);
                Add(command, "$seq", seq);
                Add(command, "$self_id", selfId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadMessage(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(
        Chat chat,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<Message>>([]);
        }

        return WithGateAsync(token => ReadMessagesAsync(chat, limit, token), cancellationToken);
    }

    public Task<IReadOnlyList<Message>> GetHistoryAsync(
        ChatScene scene,
        string peerId,
        string selfId,
        long? startSeq = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 100);
        return WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"""
                    SELECT * FROM (
                        {MessageSelect}
                        WHERE scene=$scene AND peer_id=$peer_id AND self_id=$self_id AND seq <= $start_seq
                        ORDER BY seq DESC LIMIT $limit
                    ) ORDER BY seq ASC;
                    """;
                Add(command, "$scene", scene.ToStorageValue());
                Add(command, "$peer_id", peerId);
                Add(command, "$self_id", selfId);
                Add(command, "$start_seq", startSeq ?? long.MaxValue);
                Add(command, "$limit", bounded);
                return await ReadMessageListAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ForwardNode>?> GetForwardNodesAsync(
        string id,
        string selfId,
        CancellationToken cancellationToken = default)
    {
        var messages = await WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"{MessageSelect} WHERE self_id=$self_id;";
                Add(command, "$self_id", selfId);
                return await ReadMessageListAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var message in messages)
        {
            if (FindForwardNodes(message.Content, id) is { } nodes)
            {
                return nodes;
            }
        }

        return null;
    }

    public Task<Message?> RecallMessageAsync(
        string id,
        string operatorId,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                var recalledAt = DateTimeOffset.UtcNow;
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = """
                        UPDATE messages SET recalled_at=$recalled_at, recalled_by=$recalled_by WHERE id=$id;
                        """;
                    Add(command, "$recalled_at", ToTimestamp(recalledAt));
                    Add(command, "$recalled_by", operatorId);
                    Add(command, "$id", id);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                    {
                        return null;
                    }
                }

                using var read = _connection.CreateCommand();
                read.CommandText = $"{MessageSelect} WHERE id=$id LIMIT 1;";
                Add(read, "$id", id);
                using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadMessage(reader) : null;
            },
            StoreChangeKind.Messages | StoreChangeKind.Conversations,
            cancellationToken);

    public Task<IReadOnlyList<ActiveChat>> GetActiveChatsAsync(
        string selfId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ReadActiveChatsAsync(selfId, token), cancellationToken);

    public Task DeleteMessagesAsync(Chat chat, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM messages WHERE scene=$scene AND peer_id=$peer_id AND self_id=$self_id;
                    """;
                Add(command, "$scene", chat.Scene.ToStorageValue());
                Add(command, "$peer_id", chat.PeerId);
                Add(command, "$self_id", chat.SelfId);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Messages | StoreChangeKind.Conversations,
            cancellationToken);

    private const string MessageSelect = """
        SELECT id, seq, scene, peer_id, sender_id, self_id, content, time, direction, recalled_at, recalled_by
        FROM messages
        """;

    private async Task UpsertMessageAsync(
        Message message,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(
                id, seq, scene, peer_id, sender_id, self_id, content, time, direction, recalled_at, recalled_by)
            VALUES($id, $seq, $scene, $peer_id, $sender_id, $self_id, $content, $time, $direction, $recalled_at, $recalled_by)
            ON CONFLICT(id) DO UPDATE SET
                seq=excluded.seq, scene=excluded.scene, peer_id=excluded.peer_id,
                sender_id=excluded.sender_id, self_id=excluded.self_id, content=excluded.content,
                time=excluded.time, direction=excluded.direction,
                recalled_at=excluded.recalled_at, recalled_by=excluded.recalled_by;
            """;
        Add(command, "$id", message.Id);
        Add(command, "$seq", message.Seq);
        Add(command, "$scene", message.Scene.ToStorageValue());
        Add(command, "$peer_id", message.PeerId);
        Add(command, "$sender_id", message.SenderId);
        Add(command, "$self_id", message.SelfId);
        Add(command, "$content", SerializeContent(message.Content));
        Add(command, "$time", ToTimestamp(message.Time));
        Add(command, "$direction", message.Direction.ToStorageValue());
        Add(command, "$recalled_at", ToTimestamp(message.RecalledAt));
        Add(command, "$recalled_by", message.RecalledBy);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Message>> ReadMessagesAsync(
        Chat chat,
        int limit,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT * FROM (
                {MessageSelect}
                WHERE scene=$scene AND peer_id=$peer_id AND self_id=$self_id
                ORDER BY seq DESC LIMIT $limit
            ) ORDER BY seq ASC;
            """;
        Add(command, "$scene", chat.Scene.ToStorageValue());
        Add(command, "$peer_id", chat.PeerId);
        Add(command, "$self_id", chat.SelfId);
        Add(command, "$limit", limit);
        return await ReadMessageListAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Message>> ReadMessageListAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<Message>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    private async Task<IReadOnlyList<ActiveChat>> ReadActiveChatsAsync(
        string selfId,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {MessageSelect} WHERE self_id=$self_id ORDER BY seq ASC;
            """;
        Add(command, "$self_id", selfId);
        var messages = await ReadMessageListAsync(command, cancellationToken).ConfigureAwait(false);
        return messages
            .GroupBy(message => message.Chat)
            .Select(group => new ActiveChat(group.Key, group.MaxBy(message => message.Seq)!))
            .OrderByDescending(item => item.LastMessage.Time)
            .ThenBy(item => item.Chat.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Message ReadMessage(SqliteDataReader reader) => new(
        scene: EnumStorage.ParseStorageValue<ChatScene>(reader.GetString(2)),
        peerId: reader.GetString(3),
        senderId: reader.GetString(4),
        selfId: reader.GetString(5),
        content: DeserializeContent(reader.GetString(6)),
        direction: EnumStorage.ParseStorageValue<MessageDirection>(reader.GetString(8)),
        id: reader.GetString(0),
        seq: reader.GetInt64(1),
        time: FromTimestamp(reader.GetInt64(7)),
        recalledAt: GetOptionalTimestamp(reader, 9),
        recalledBy: reader.IsDBNull(10) ? null : reader.GetString(10));

    private static IReadOnlyList<ForwardNode>? FindForwardNodes(IEnumerable<MessageSegment> content, string id)
    {
        foreach (var forward in content.OfType<ForwardSegment>())
        {
            if (forward.Id == id)
            {
                return forward.Nodes;
            }

            foreach (var node in forward.Nodes)
            {
                if (FindForwardNodes(node.Content, id) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
