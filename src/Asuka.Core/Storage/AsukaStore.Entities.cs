using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    internal Task CreateGroupWithOwnerAsync(
        Group group,
        string ownerId,
        CancellationToken cancellationToken) =>
        WriteAsync(
            async token =>
            {
                using var transaction = _connection.BeginTransaction(deferred: false);
                if (!await ExistsAsync("users", ownerId, token, transaction).ConfigureAwait(false))
                {
                    throw new PlatformException(PlatformError.UserNotFound, $"User not found: {ownerId}")
                    {
                        UserId = ownerId,
                        ResourceId = ownerId,
                    };
                }

                using (var groupCommand = _connection.CreateCommand())
                {
                    groupCommand.Transaction = transaction;
                    groupCommand.CommandText = """
                        INSERT INTO groups(id, name, avatar, intro, level, max_member_count, whole_muted, created_at)
                        VALUES($id, $name, $avatar, $intro, $level, $max_count, $whole_muted, $created_at);
                        """;
                    Add(groupCommand, "$id", group.Id);
                    Add(groupCommand, "$name", group.Name);
                    Add(groupCommand, "$avatar", group.Avatar);
                    Add(groupCommand, "$intro", group.Intro);
                    Add(groupCommand, "$level", group.Level);
                    Add(groupCommand, "$max_count", group.MaxMemberCount);
                    Add(groupCommand, "$whole_muted", group.WholeMuted);
                    Add(groupCommand, "$created_at", ToTimestamp(group.CreatedAt));
                    _ = await groupCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (var memberCommand = _connection.CreateCommand())
                {
                    memberCommand.Transaction = transaction;
                    memberCommand.CommandText = """
                        INSERT INTO group_members(
                            group_id, user_id, card, role, title, joined_at, last_sent_at, muted_until)
                        VALUES($group_id, $user_id, '', 'owner', '', $joined_at, NULL, NULL);
                        """;
                    Add(memberCommand, "$group_id", group.Id);
                    Add(memberCommand, "$user_id", ownerId);
                    Add(memberCommand, "$joined_at", ToTimestamp(DateTimeOffset.UtcNow));
                    _ = await memberCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Groups | StoreChangeKind.Members | StoreChangeKind.Conversations,
            cancellationToken);

    public Task SaveAsync(User user, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users(id, name, nickname, avatar, sex, age, sign, created_at)
                    VALUES($id, $name, $nickname, $avatar, $sex, $age, $sign, $created_at)
                    ON CONFLICT(id) DO UPDATE SET
                        name=excluded.name, nickname=excluded.nickname, avatar=excluded.avatar,
                        sex=excluded.sex, age=excluded.age, sign=excluded.sign, created_at=excluded.created_at;
                    """;
                Add(command, "$id", user.Id);
                Add(command, "$name", user.Name);
                Add(command, "$nickname", user.Nickname);
                Add(command, "$avatar", user.Avatar);
                Add(command, "$sex", user.Sex.ToStorageValue());
                Add(command, "$age", user.Age);
                Add(command, "$sign", user.Sign);
                Add(command, "$created_at", ToTimestamp(user.CreatedAt));
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Users | StoreChangeKind.Members | StoreChangeKind.Conversations,
            cancellationToken);

    public Task<User?> GetUserAsync(string id, CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, nickname, avatar, sex, age, sign, created_at
                    FROM users WHERE id=$id LIMIT 1;
                    """;
                Add(command, "$id", id);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadUser(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, nickname, avatar, sex, age, sign, created_at
                    FROM users ORDER BY created_at, id;
                    """;
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var users = new List<User>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    users.Add(ReadUser(reader));
                }

                return (IReadOnlyList<User>)users;
            },
            cancellationToken);

    public Task DeleteUserAsync(string id, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM users WHERE id=$id;";
                Add(command, "$id", id);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Users | StoreChangeKind.Members | StoreChangeKind.Friendships | StoreChangeKind.Conversations,
            cancellationToken);

    public Task SaveAsync(Group group, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO groups(id, name, avatar, intro, level, max_member_count, whole_muted, created_at)
                    VALUES($id, $name, $avatar, $intro, $level, $max_count, $whole_muted, $created_at)
                    ON CONFLICT(id) DO UPDATE SET
                        name=excluded.name, avatar=excluded.avatar, intro=excluded.intro,
                        level=excluded.level, max_member_count=excluded.max_member_count,
                        whole_muted=excluded.whole_muted, created_at=excluded.created_at;
                    """;
                Add(command, "$id", group.Id);
                Add(command, "$name", group.Name);
                Add(command, "$avatar", group.Avatar);
                Add(command, "$intro", group.Intro);
                Add(command, "$level", group.Level);
                Add(command, "$max_count", group.MaxMemberCount);
                Add(command, "$whole_muted", group.WholeMuted);
                Add(command, "$created_at", ToTimestamp(group.CreatedAt));
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Groups | StoreChangeKind.Conversations | StoreChangeKind.Friendships,
            cancellationToken);

    public Task<Group?> GetGroupAsync(string id, CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, avatar, intro, level, max_member_count, whole_muted, created_at
                    FROM groups WHERE id=$id LIMIT 1;
                    """;
                Add(command, "$id", id);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadGroup(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<Group>> GetAllGroupsAsync(CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, avatar, intro, level, max_member_count, whole_muted, created_at
                    FROM groups ORDER BY created_at, id;
                    """;
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var groups = new List<Group>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    groups.Add(ReadGroup(reader));
                }

                return (IReadOnlyList<Group>)groups;
            },
            cancellationToken);

    public Task DeleteGroupAsync(string id, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                await using var transaction = await _connection.BeginTransactionAsync(token).ConfigureAwait(false);
                using var command = _connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    DELETE FROM messages WHERE scene='group' AND peer_id=$id;
                    DELETE FROM pending_requests WHERE group_id=$id;
                    DELETE FROM groups WHERE id=$id;
                    """;
                Add(command, "$id", id);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Groups | StoreChangeKind.Members | StoreChangeKind.Messages
                | StoreChangeKind.Requests | StoreChangeKind.Conversations,
            cancellationToken);

    public Task SaveAsync(GroupMember member, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                if (!await ExistsAsync("groups", member.GroupId, token).ConfigureAwait(false))
                {
                    throw new StorePersistenceException(
                        $"Cannot write a record that references a missing group: {member.GroupId}");
                }

                if (!await ExistsAsync("users", member.UserId, token).ConfigureAwait(false))
                {
                    throw new StorePersistenceException(
                        $"Cannot write a record that references a missing user: {member.UserId}");
                }

                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO group_members(
                        group_id, user_id, card, role, title, joined_at, last_sent_at, muted_until)
                    VALUES($group_id, $user_id, $card, $role, $title, $joined_at, $last_sent_at, $muted_until)
                    ON CONFLICT(group_id, user_id) DO UPDATE SET
                        card=excluded.card, role=excluded.role, title=excluded.title,
                        joined_at=excluded.joined_at, last_sent_at=excluded.last_sent_at,
                        muted_until=excluded.muted_until;
                    """;
                Add(command, "$group_id", member.GroupId);
                Add(command, "$user_id", member.UserId);
                Add(command, "$card", member.Card);
                Add(command, "$role", member.Role.ToStorageValue());
                Add(command, "$title", member.Title);
                Add(command, "$joined_at", ToTimestamp(member.JoinedAt));
                Add(command, "$last_sent_at", ToTimestamp(member.LastSentAt));
                Add(command, "$muted_until", ToTimestamp(member.MutedUntil));
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Members,
            cancellationToken);

    public Task<GroupMember?> GetMemberAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT group_id, user_id, card, role, title, joined_at, last_sent_at, muted_until
                    FROM group_members WHERE group_id=$group_id AND user_id=$user_id LIMIT 1;
                    """;
                Add(command, "$group_id", groupId);
                Add(command, "$user_id", userId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadMember(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<GroupMember>> GetMembersAsync(
        string groupId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT group_id, user_id, card, role, title, joined_at, last_sent_at, muted_until
                    FROM group_members WHERE group_id=$group_id ORDER BY joined_at, user_id;
                    """;
                Add(command, "$group_id", groupId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var members = new List<GroupMember>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    members.Add(ReadMember(reader));
                }

                return (IReadOnlyList<GroupMember>)members;
            },
            cancellationToken);

    public Task<IReadOnlyList<Group>> GetGroupsContainingAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT g.id, g.name, g.avatar, g.intro, g.level, g.max_member_count, g.whole_muted, g.created_at
                    FROM groups g
                    INNER JOIN group_members m ON m.group_id=g.id
                    WHERE m.user_id=$user_id
                    ORDER BY g.created_at, g.id;
                    """;
                Add(command, "$user_id", userId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var groups = new List<Group>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    groups.Add(ReadGroup(reader));
                }

                return (IReadOnlyList<Group>)groups;
            },
            cancellationToken);

    public Task<int> GetMemberCountAsync(string groupId, CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM group_members WHERE group_id=$group_id;";
                Add(command, "$group_id", groupId);
                return Convert.ToInt32(
                    await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken);

    public Task RemoveMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM group_members WHERE group_id=$group_id AND user_id=$user_id;";
                Add(command, "$group_id", groupId);
                Add(command, "$user_id", userId);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Members,
            cancellationToken);

    public Task SaveAsync(Friendship friendship, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                if (!await ExistsAsync("users", friendship.UserId, token).ConfigureAwait(false))
                {
                    throw new StorePersistenceException(
                        $"Cannot write a record that references a missing user: {friendship.UserId}");
                }

                if (!await ExistsAsync("users", friendship.FriendId, token).ConfigureAwait(false))
                {
                    throw new StorePersistenceException(
                        $"Cannot write a record that references a missing user: {friendship.FriendId}");
                }

                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO friendships(user_id, friend_id, remark, created_at)
                    VALUES($user_id, $friend_id, $remark, $created_at)
                    ON CONFLICT(user_id, friend_id) DO UPDATE SET
                        remark=excluded.remark, created_at=excluded.created_at;
                    """;
                Add(command, "$user_id", friendship.UserId);
                Add(command, "$friend_id", friendship.FriendId);
                Add(command, "$remark", friendship.Remark);
                Add(command, "$created_at", ToTimestamp(friendship.CreatedAt));
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Friendships,
            cancellationToken);

    public Task<Friendship?> GetFriendshipAsync(
        string userId,
        string friendId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT user_id, friend_id, remark, created_at FROM friendships
                    WHERE user_id=$user_id AND friend_id=$friend_id LIMIT 1;
                    """;
                Add(command, "$user_id", userId);
                Add(command, "$friend_id", friendId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadFriendship(reader) : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<Friendship>> GetFriendshipsAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT user_id, friend_id, remark, created_at FROM friendships
                    WHERE user_id=$user_id ORDER BY created_at, friend_id;
                    """;
                Add(command, "$user_id", userId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var friendships = new List<Friendship>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    friendships.Add(ReadFriendship(reader));
                }

                return (IReadOnlyList<Friendship>)friendships;
            },
            cancellationToken);

    public Task<IReadOnlyList<FriendInfo>> GetFriendsAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT f.user_id, f.friend_id, f.remark, f.created_at,
                           u.id, u.name, u.nickname, u.avatar, u.sex, u.age, u.sign, u.created_at
                    FROM friendships f
                    INNER JOIN users u ON u.id=f.friend_id
                    WHERE f.user_id=$user_id
                    ORDER BY f.created_at, f.friend_id;
                    """;
                Add(command, "$user_id", userId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var friends = new List<FriendInfo>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    friends.Add(new FriendInfo(ReadFriendship(reader), ReadUser(reader, 4)));
                }

                return (IReadOnlyList<FriendInfo>)friends;
            },
            cancellationToken);

    public Task RemoveFriendshipAsync(
        string userId,
        string friendId,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM friendships
                    WHERE (user_id=$user_id AND friend_id=$friend_id)
                       OR (user_id=$friend_id AND friend_id=$user_id);
                    """;
                Add(command, "$user_id", userId);
                Add(command, "$friend_id", friendId);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Friendships,
            cancellationToken);

    private async Task<bool> ExistsAsync(
        string table,
        string id,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=$id);";
        Add(command, "$id", id);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static User ReadUser(SqliteDataReader reader, int offset = 0) => new(
        name: reader.GetString(offset + 1),
        id: reader.GetString(offset),
        nickname: reader.GetString(offset + 2),
        avatar: reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        sex: EnumStorage.ParseStorageValue<Sex>(reader.GetString(offset + 4)),
        age: reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5),
        sign: reader.GetString(offset + 6),
        createdAt: FromTimestamp(reader.GetInt64(offset + 7)));

    private static Group ReadGroup(SqliteDataReader reader) => new(
        name: reader.GetString(1),
        id: reader.GetString(0),
        avatar: reader.IsDBNull(2) ? null : reader.GetString(2),
        intro: reader.GetString(3),
        level: reader.GetInt32(4),
        maxMemberCount: reader.GetInt32(5),
        wholeMuted: reader.GetBoolean(6),
        createdAt: FromTimestamp(reader.GetInt64(7)));

    private static GroupMember ReadMember(SqliteDataReader reader) => new(
        groupId: reader.GetString(0),
        userId: reader.GetString(1),
        card: reader.GetString(2),
        role: EnumStorage.ParseStorageValue<GroupRole>(reader.GetString(3)),
        title: reader.GetString(4),
        joinedAt: FromTimestamp(reader.GetInt64(5)),
        lastSentAt: GetOptionalTimestamp(reader, 6),
        mutedUntil: GetOptionalTimestamp(reader, 7));

    private static Friendship ReadFriendship(SqliteDataReader reader) => new(
        userId: reader.GetString(0),
        friendId: reader.GetString(1),
        remark: reader.GetString(2),
        createdAt: FromTimestamp(reader.GetInt64(3)));
}
