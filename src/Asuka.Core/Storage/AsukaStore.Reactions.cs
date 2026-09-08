namespace Asuka.Core;

public sealed partial class AsukaStore
{
    public Task<IReadOnlyList<MessageReactionState>> GetMessageReactionsAsync(
        string messageId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            token => ReadReactionsAsync("WHERE r.message_id=$message_id", messageId, null, token),
            cancellationToken);

    public Task<IReadOnlyList<MessageReactionState>> GetChatReactionsAsync(
        Chat chat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return WithGateAsync(
            token => ReadReactionsAsync(
                "WHERE m.scene=$scene AND m.peer_id=$peer_id AND m.self_id=$self_id",
                null,
                chat,
                token),
            cancellationToken);
    }

    public Task<bool> SetMessageReactionAsync(
        string messageId,
        string userId,
        string reaction,
        bool added,
        CancellationToken cancellationToken = default) =>
        SetMessageReactionAsync(messageId, userId, reaction, added, "face", cancellationToken);

    public async Task<bool> SetMessageReactionAsync(
        string messageId,
        string userId,
        string reaction,
        bool added,
        string reactionType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(reactionType);
        var changed = await WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                // The active-message condition also prevents a concurrent recall
                // through another store connection from resurrecting reactions.
                command.CommandText = added
                    ? """
                        INSERT INTO message_reactions(message_id, user_id, reaction, reaction_type)
                        SELECT id, $user_id, $reaction, $reaction_type FROM messages
                        WHERE id=$message_id AND recalled_at IS NULL
                        ON CONFLICT(message_id, user_id, reaction_type, reaction) DO NOTHING;
                        """
                    : """
                        DELETE FROM message_reactions
                        WHERE message_id=$message_id AND user_id=$user_id
                            AND reaction=$reaction AND reaction_type=$reaction_type;
                        """;
                Add(command, "$message_id", messageId);
                Add(command, "$user_id", userId);
                Add(command, "$reaction", reaction);
                Add(command, "$reaction_type", reactionType);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0;
            },
            cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            Publish(StoreChangeKind.Messages);
        }

        return changed;
    }

    private async Task<IReadOnlyList<MessageReactionState>> ReadReactionsAsync(
        string predicate,
        string? messageId,
        Chat? chat,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.message_id, r.user_id, r.reaction, r.reaction_type
            FROM message_reactions r JOIN messages m ON m.id=r.message_id
            {predicate} AND m.recalled_at IS NULL
            ORDER BY r.message_id, r.reaction_type, r.reaction, r.user_id;
            """;
        if (chat is not null)
        {
            Add(command, "$scene", chat.Scene.ToStorageValue());
            Add(command, "$peer_id", chat.PeerId);
            Add(command, "$self_id", chat.SelfId);
        }
        else
        {
            Add(command, "$message_id", messageId);
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var reactions = new List<MessageReactionState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reactions.Add(new MessageReactionState(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return reactions;
    }
}
