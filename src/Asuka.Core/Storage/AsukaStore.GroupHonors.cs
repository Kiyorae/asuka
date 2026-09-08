using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion11()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
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
        transaction.Commit();
    }

    public Task<GroupHonorInfo> GetGroupHonorInfoAsync(string groupId, string accountId,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction();
            await RequireHonorMemberAsync(groupId, accountId, transaction, token).ConfigureAwait(false);
            GroupTalkative? current = null;
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT h.user_id,u.nickname,u.avatar,h.day_count FROM group_current_talkative h
                    INNER JOIN users u ON u.id=h.user_id WHERE h.group_id=$group;
                    """;
                Add(command, "$group", groupId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    current = new GroupTalkative(reader.GetString(0), reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetInt32(3));
            }
            var lists = Enumerable.Range(0, 5).Select(_ => new List<GroupHonorEntry>()).ToArray();
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT h.type,h.user_id,u.nickname,u.avatar,h.description FROM group_honors h
                    INNER JOIN users u ON u.id=h.user_id WHERE h.group_id=$group ORDER BY h.sequence;
                    """;
                Add(command, "$group", groupId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    lists[reader.GetInt32(0)].Add(new GroupHonorEntry(reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3), reader.GetString(4)));
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new GroupHonorInfo(groupId, current, lists[0], lists[1], lists[2], lists[3], lists[4]);
        }, cancellationToken);

    internal async Task<GroupHonorMutation> SetGroupHonorAsync(string groupId, string userId, GroupHonorType type,
        string operatorId, string description, int dayCount, IReadOnlyCollection<string> botIds, CancellationToken cancellationToken)
    {
        var result = await WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            await RequireHonorAdministratorAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            await RequireHonorMemberAsync(groupId, userId, transaction, token).ConfigureAwait(false);
            var recipients = await ReadHonorRecipientsAsync(groupId, botIds, transaction, token).ConfigureAwait(false);
            string? previousDescription;
            using (var previous = HonorCommand(transaction, groupId, userId, type))
            {
                previous.CommandText = "SELECT description FROM group_honors WHERE group_id=$group AND user_id=$user AND type=$type;";
                previousDescription = await previous.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            }
            var changed = previousDescription != description;
            var granted = previousDescription is null;
            if (type == GroupHonorType.Talkative)
            {
                using var previous = _connection.CreateCommand();
                previous.Transaction = transaction;
                previous.CommandText = "SELECT user_id,day_count FROM group_current_talkative WHERE group_id=$group;";
                Add(previous, "$group", groupId);
                using (var reader = await previous.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    var exists = await reader.ReadAsync(token).ConfigureAwait(false);
                    granted = !exists || reader.GetString(0) != userId;
                    changed |= granted || reader.GetInt32(1) != dayCount;
                }
                using var current = _connection.CreateCommand();
                current.Transaction = transaction;
                current.CommandText = """
                    INSERT INTO group_current_talkative(group_id,user_id,day_count) VALUES($group,$user,$days)
                    ON CONFLICT(group_id) DO UPDATE SET user_id=excluded.user_id,day_count=excluded.day_count
                    WHERE user_id!=excluded.user_id OR day_count!=excluded.day_count;
                    """;
                Add(current, "$group", groupId);
                Add(current, "$user", userId);
                Add(current, "$days", dayCount);
                await current.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            using (var command = HonorCommand(transaction, groupId, userId, type))
            {
                command.CommandText = """
                    INSERT INTO group_honors(group_id,user_id,type,description) VALUES($group,$user,$type,$description)
                    ON CONFLICT(group_id,type,user_id) DO UPDATE SET description=excluded.description
                    WHERE description!=excluded.description;
                    """;
                Add(command, "$description", description);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new GroupHonorMutation(changed, granted, recipients);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Changed) Publish(StoreChangeKind.Groups);
        return result;
    }

    internal async Task<bool> RemoveGroupHonorAsync(string groupId, string userId, GroupHonorType type,
        string operatorId, CancellationToken cancellationToken)
    {
        var changed = await WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            await RequireHonorAdministratorAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            await RequireHonorMemberAsync(groupId, userId, transaction, token).ConfigureAwait(false);
            using var command = HonorCommand(transaction, groupId, userId, type);
            command.CommandText = "DELETE FROM group_honors WHERE group_id=$group AND user_id=$user AND type=$type;";
            var result = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            if (type == GroupHonorType.Talkative)
            {
                command.CommandText = "DELETE FROM group_current_talkative WHERE group_id=$group AND user_id=$user;";
                result |= await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
        if (changed) Publish(StoreChangeKind.Groups);
        return changed;
    }

    internal Task<IReadOnlyList<string>> GetGroupLuckyKingRecipientsAsync(string groupId, string senderId, string targetId,
        string operatorId, IReadOnlyCollection<string> botIds, CancellationToken cancellationToken) =>
        WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction();
            await RequireHonorAdministratorAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            await RequireHonorMemberAsync(groupId, senderId, transaction, token).ConfigureAwait(false);
            await RequireHonorMemberAsync(groupId, targetId, transaction, token).ConfigureAwait(false);
            var recipients = await ReadHonorRecipientsAsync(groupId, botIds, transaction, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return recipients;
        }, cancellationToken);

    private SqliteCommand HonorCommand(SqliteTransaction transaction, string groupId, string userId, GroupHonorType type)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        Add(command, "$group", groupId);
        Add(command, "$user", userId);
        Add(command, "$type", (int)type);
        return command;
    }

    private async Task<GroupRole> RequireHonorMemberAsync(string groupId, string userId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync("groups", groupId, cancellationToken, transaction).ConfigureAwait(false))
            throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}") { GroupId = groupId };
        return await ReadNotificationMemberRoleAsync(groupId, userId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformException(PlatformError.NotAMember, $"User {userId} is not a member of group {groupId}")
            { GroupId = groupId, UserId = userId };
    }

    private async Task RequireHonorAdministratorAsync(string groupId, string operatorId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (await RequireHonorMemberAsync(groupId, operatorId, transaction, cancellationToken).ConfigureAwait(false) <= GroupRole.Member)
            throw new PlatformException(PlatformError.NotPermitted, "Administrator privileges are required to manage group honors");
    }

    private async Task<IReadOnlyList<string>> ReadHonorRecipientsAsync(string groupId, IReadOnlyCollection<string> botIds,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var selfId in botIds.Distinct(StringComparer.Ordinal))
            if (await ReadNotificationMemberRoleAsync(groupId, selfId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                result.Add(selfId);
        return result;
    }

    internal sealed record GroupHonorMutation(bool Changed, bool Granted, IReadOnlyList<string> Recipients);
}
