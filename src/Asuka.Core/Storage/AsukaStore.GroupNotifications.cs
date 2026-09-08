using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion9()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE group_notification_history (
                notification_seq INTEGER PRIMARY KEY CHECK(notification_seq > 0),
                self_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('admin_change','kick','quit')),
                group_id TEXT NOT NULL,
                target_user_id TEXT NOT NULL,
                operator_id TEXT NULL,
                is_set INTEGER NULL CHECK(is_set IS NULL OR is_set IN (0,1)),
                time INTEGER NOT NULL,
                CHECK((kind='admin_change' AND operator_id IS NOT NULL AND is_set IS NOT NULL)
                    OR (kind='kick' AND operator_id IS NOT NULL AND is_set IS NULL)
                    OR (kind='quit' AND operator_id IS NULL AND is_set IS NULL))
            );
            CREATE INDEX ix_group_notification_history_self_seq ON group_notification_history(self_id, notification_seq DESC);
            CREATE INDEX ix_requests_group_notification_page ON pending_requests(self_id,is_filtered,notification_seq DESC)
                WHERE kind IN ('groupJoin','groupInvitedJoin') AND group_id IS NOT NULL;
            CREATE TRIGGER reject_request_group_notification_collision BEFORE INSERT ON pending_requests
                WHEN EXISTS(SELECT 1 FROM group_notification_history WHERE notification_seq=NEW.notification_seq)
            BEGIN
                SELECT RAISE(ABORT, 'The notification sequence is already used by group history');
            END;
            PRAGMA user_version=9;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Combines request and membership history in SQL, reading at most limit + 1 records.</summary>
    public Task<GroupNotificationPage> GetGroupNotificationPageAsync(string selfId, bool isFiltered = false,
        long? startSequence = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (startSequence < 0) throw new ArgumentOutOfRangeException(nameof(startSequence));
        return WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            var startClause = startSequence is null ? string.Empty : "AND notification_seq <= $start";
            command.CommandText = $"""
                SELECT * FROM (
                    SELECT id,flag,kind,requester_id,group_id,self_id,comment,time,
                        resolution_state,resolution_reason,target_user_id,source_group_id,
                        is_filtered,via,resolved_by,notification_seq,
                        CASE kind WHEN 'groupJoin' THEN 'join_request' ELSE 'invited_join_request' END AS notification_kind,
                        CASE kind WHEN 'groupJoin' THEN requester_id ELSE COALESCE(target_user_id,requester_id) END AS history_target,
                        resolved_by AS history_operator,NULL AS is_set
                    FROM pending_requests
                    WHERE self_id=$self AND kind IN ('groupJoin','groupInvitedJoin') AND group_id IS NOT NULL
                        AND is_filtered=$filtered {startClause}
                    UNION ALL
                    SELECT NULL,NULL,NULL,NULL,group_id,self_id,NULL,time,
                        NULL,NULL,NULL,NULL,0,NULL,NULL,notification_seq,kind,target_user_id,operator_id,is_set
                    FROM group_notification_history WHERE self_id=$self AND $filtered=0 {startClause}
                ) ORDER BY notification_seq DESC LIMIT $limit;
                """;
            Add(command, "$self", selfId);
            Add(command, "$filtered", isFiltered);
            Add(command, "$limit", (long)limit + 1);
            if (startSequence is not null) Add(command, "$start", startSequence);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var notifications = new List<GroupNotificationEntry>();
            long? next = null;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (notifications.Count == limit) { next = reader.GetInt64(15); break; }
                notifications.Add(new GroupNotificationEntry(reader.GetInt64(15), reader.GetString(5),
                    ParseGroupNotificationKind(reader.GetString(16)), reader.GetString(4), reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetBoolean(19),
                    FromTimestamp(reader.GetInt64(7)), reader.IsDBNull(0) ? null : ReadRequest(reader)));
            }
            return new GroupNotificationPage(notifications, next);
        }, cancellationToken);
    }

    public Task<bool> SetAdminWithNotificationsAsync(string groupId, string userId, string operatorId, bool granted,
        IReadOnlyCollection<string> selfIds, CancellationToken cancellationToken = default) =>
        ChangeMemberWithNotificationsAsync(groupId, userId, operatorId, granted, selfIds, cancellationToken);

    public Task<bool> RemoveMemberWithNotificationsAsync(string groupId, string userId, string operatorId,
        GroupMemberChangeReason reason, IReadOnlyCollection<string> recipients, CancellationToken cancellationToken = default)
    {
        if (reason is not (GroupMemberChangeReason.Voluntary or GroupMemberChangeReason.Administrative))
            throw new PlatformException(PlatformError.InvalidParameter, "A member removal must be voluntary or administrative");
        return ChangeMemberWithNotificationsAsync(groupId, userId, operatorId, null, recipients, cancellationToken);
    }

    private async Task<bool> ChangeMemberWithNotificationsAsync(string groupId, string userId, string operatorId,
        bool? granted, IReadOnlyCollection<string> selfIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selfIds);
        var changed = await WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            if (!await ExistsAsync("groups", groupId, token, transaction).ConfigureAwait(false))
                throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}") { GroupId = groupId };
            var target = await ReadNotificationMemberRoleAsync(groupId, userId, transaction, token).ConfigureAwait(false);
            if (target is null)
            {
                if (granted is null) return false;
                throw new PlatformException(PlatformError.NotAMember, $"User {userId} is not a member of group {groupId}")
                { GroupId = groupId, UserId = userId };
            }
            if (target == GroupRole.Owner)
                throw new PlatformException(PlatformError.NotPermitted, granted is null
                    ? "The group owner must explicitly disband the group instead of leaving it"
                    : "The group owner's role cannot be changed");
            var actor = await ReadNotificationMemberRoleAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            if (granted is not null)
            {
                if (actor != GroupRole.Owner)
                    throw new PlatformException(PlatformError.NotPermitted, "Only the group owner can manage administrators");
                if (target == (granted.Value ? GroupRole.Admin : GroupRole.Member)) return false;
            }
            else if (operatorId != userId && (actor is null || actor <= GroupRole.Member || actor <= target))
                throw new PlatformException(PlatformError.NotPermitted, "Removing another member requires a strictly higher administrator role");

            var recipients = new List<string>();
            foreach (var selfId in selfIds.Distinct(StringComparer.Ordinal))
            {
                if (await ReadNotificationMemberRoleAsync(groupId, selfId, transaction, token).ConfigureAwait(false) is not null)
                    recipients.Add(selfId);
            }
            using (var mutation = _connection.CreateCommand())
            {
                mutation.Transaction = transaction;
                mutation.CommandText = granted is null
                    ? "DELETE FROM group_members WHERE group_id=$group AND user_id=$user;"
                    : "UPDATE group_members SET role=$role WHERE group_id=$group AND user_id=$user;";
                Add(mutation, "$group", groupId);
                Add(mutation, "$user", userId);
                if (granted is not null) Add(mutation, "$role", (granted.Value ? GroupRole.Admin : GroupRole.Member).ToStorageValue());
                _ = await mutation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var kind = granted is not null ? GroupNotificationKind.AdminChange
                : operatorId == userId ? GroupNotificationKind.Quit : GroupNotificationKind.Kick;
            var time = DateTimeOffset.UtcNow;
            foreach (var recipient in recipients)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE request_notification_sequence SET next_seq=next_seq+1
                    WHERE id=1 AND typeof(next_seq)='integer' AND next_seq < 9223372036854775807
                    RETURNING next_seq-1;
                    """;
                var allocated = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (allocated is null || allocated is DBNull)
                    throw new StorePersistenceException("The notification sequence counter is exhausted or unavailable");
                var sequence = Convert.ToInt64(allocated, CultureInfo.InvariantCulture);
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO group_notification_history(notification_seq,self_id,kind,group_id,target_user_id,operator_id,is_set,time)
                    VALUES($seq,$self,$kind,$group,$target,$operator,$is_set,$time);
                    """;
                Add(command, "$seq", sequence);
                Add(command, "$self", recipient);
                Add(command, "$kind", kind switch
                {
                    GroupNotificationKind.AdminChange => "admin_change",
                    GroupNotificationKind.Kick => "kick",
                    _ => "quit",
                });
                Add(command, "$group", groupId);
                Add(command, "$target", userId);
                Add(command, "$operator", kind == GroupNotificationKind.Quit ? null : operatorId);
                Add(command, "$is_set", granted);
                Add(command, "$time", ToTimestamp(time));
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (changed) Publish(StoreChangeKind.Members | StoreChangeKind.Conversations | StoreChangeKind.Notifications);
        return changed;
    }

    private async Task<GroupRole?> ReadNotificationMemberRoleAsync(string groupId, string userId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT role FROM group_members WHERE group_id=$group AND user_id=$user;";
        Add(command, "$group", groupId);
        Add(command, "$user", userId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string role
            ? EnumStorage.ParseStorageValue<GroupRole>(role) : null;
    }

    private static GroupNotificationKind ParseGroupNotificationKind(string value) => value switch
    {
        "join_request" => GroupNotificationKind.JoinRequest,
        "invited_join_request" => GroupNotificationKind.InvitedJoinRequest,
        "admin_change" => GroupNotificationKind.AdminChange,
        "kick" => GroupNotificationKind.Kick,
        "quit" => GroupNotificationKind.Quit,
        _ => throw new StorePersistenceException($"Invalid group notification type: {value}"),
    };
}
