namespace Matcha.Core;

public sealed partial class MatchaStore
{
    public IAsyncEnumerable<IReadOnlyList<User>> ObserveUsersAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync(StoreChangeKind.Users, GetAllUsersAsync, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<Group>> ObserveGroupsAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync(StoreChangeKind.Groups, GetAllGroupsAsync, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<Message>> ObserveMessagesAsync(
        Chat chat,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            StoreChangeKind.Messages,
            token => GetMessagesAsync(chat, limit, token),
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<MemberRosterItem>> ObserveMembersAsync(
        string groupId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            StoreChangeKind.Members | StoreChangeKind.Users,
            token => GetMemberRosterAsync(groupId, token),
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<Friendship>> ObserveFriendshipsAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            StoreChangeKind.Friendships,
            token => GetFriendshipsAsync(userId, token),
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<PendingRequest>> ObservePendingRequestsAsync(
        string selfId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            StoreChangeKind.Requests,
            token => GetPendingRequestsAsync(selfId, cancellationToken: token),
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<ConversationSummary>> ObserveConversationsAsync(
        string selfId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            StoreChangeKind.Conversations,
            token => GetConversationSummariesAsync(selfId, token),
            cancellationToken);

    public Task<IReadOnlyList<MemberRosterItem>> GetMemberRosterAsync(
        string groupId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT m.group_id, m.user_id, m.card, m.role, m.title, m.joined_at, m.last_sent_at, m.muted_until,
                           u.id, u.name, u.nickname, u.avatar, u.sex, u.age, u.sign, u.created_at
                    FROM group_members m
                    INNER JOIN users u ON u.id=m.user_id
                    WHERE m.group_id=$group_id
                    ORDER BY m.joined_at, m.user_id;
                    """;
                Add(command, "$group_id", groupId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var roster = new List<MemberRosterItem>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    roster.Add(new MemberRosterItem(ReadMember(reader), ReadUser(reader, 8)));
                }

                return (IReadOnlyList<MemberRosterItem>)roster;
            },
            cancellationToken);

    public Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(
        string selfId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                var activeChats = await ReadActiveChatsAsync(selfId, token).ConfigureAwait(false);
                var summaries = new List<ConversationSummary>(activeChats.Count);
                foreach (var item in activeChats)
                {
                    using var command = _connection.CreateCommand();
                    if (item.Chat.Scene == ChatScene.Group)
                    {
                        command.CommandText = "SELECT name, avatar FROM groups WHERE id=$id LIMIT 1;";
                    }
                    else
                    {
                        command.CommandText = "SELECT nickname, avatar FROM users WHERE id=$id LIMIT 1;";
                    }

                    Add(command, "$id", item.Chat.PeerId);
                    using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    var title = item.Chat.PeerId;
                    string? avatar = null;
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        title = reader.GetString(0);
                        avatar = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }

                    summaries.Add(new ConversationSummary(item.Chat, title, avatar, item.LastMessage));
                }

                return (IReadOnlyList<ConversationSummary>)summaries;
            },
            cancellationToken);
}
