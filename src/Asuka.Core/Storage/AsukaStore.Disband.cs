namespace Asuka.Core;

public sealed partial class AsukaStore
{
    internal Task<IReadOnlyList<string>> DisbandGroupAsync(
        string groupId, string operatorId, IReadOnlyCollection<string> registeredBots, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            if (!await ExistsAsync("groups", groupId, token, transaction).ConfigureAwait(false))
                throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}") { GroupId = groupId };
            var recipients = new List<string>();
            var candidates = registeredBots.ToHashSet(StringComparer.Ordinal);
            var authorized = false;
            using (var members = _connection.CreateCommand())
            {
                members.Transaction = transaction;
                members.CommandText = "SELECT user_id, role FROM group_members WHERE group_id=$group;";
                Add(members, "$group", groupId);
                using var reader = await members.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var userId = reader.GetString(0);
                    if (userId == operatorId && reader.GetString(1) == "owner") authorized = true;
                    if (candidates.Contains(userId)) recipients.Add(userId);
                }
            }
            if (!authorized) throw new PlatformException(PlatformError.NotPermitted, "Only the group owner can disband the group");
            using (var deletion = _connection.CreateCommand())
            {
                deletion.Transaction = transaction;
                deletion.CommandText = """
                    DELETE FROM messages WHERE scene='group' AND peer_id=$group;
                    UPDATE pending_requests SET resolution_state='ignored', resolution_reason='Group disbanded', resolved_by=$operator
                        WHERE group_id=$group AND resolution_state IS NULL;
                    DELETE FROM groups WHERE id=$group;
                    """;
                Add(deletion, "$group", groupId);
                Add(deletion, "$operator", operatorId);
                _ = await deletion.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return (IReadOnlyList<string>)recipients;
        }, StoreChangeKind.Groups | StoreChangeKind.Members | StoreChangeKind.Messages | StoreChangeKind.Requests
            | StoreChangeKind.Conversations | StoreChangeKind.Files | StoreChangeKind.Notifications, cancellationToken);
}
