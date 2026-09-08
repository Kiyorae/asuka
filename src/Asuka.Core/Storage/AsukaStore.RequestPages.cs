namespace Asuka.Core;

public sealed partial class AsukaStore
{
    public Task<IReadOnlyList<PendingRequest>> GetFriendRequestHistoryAsync(
        string selfId, bool isFiltered, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {RequestSelect} WHERE self_id=$self AND kind=$kind AND is_filtered=$filtered
                ORDER BY time DESC, notification_seq DESC LIMIT $limit;
                """;
            Add(command, "$self", selfId);
            Add(command, "$kind", RequestKind.Friend.ToStorageValue());
            Add(command, "$filtered", isFiltered ? 1 : 0);
            Add(command, "$limit", limit);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var requests = new List<PendingRequest>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) requests.Add(ReadRequest(reader));
            return (IReadOnlyList<PendingRequest>)requests;
        }, cancellationToken);
    }

    public Task<(IReadOnlyList<PendingRequest> Requests, long? NextSequence)> GetGroupRequestNotificationPageAsync(
        string selfId, bool isFiltered, long? startSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {RequestSelect} WHERE self_id=$self AND kind IN ($join,$invited) AND is_filtered=$filtered
                AND ($start IS NULL OR notification_seq <= $start)
                ORDER BY notification_seq DESC LIMIT $limit;
                """;
            Add(command, "$self", selfId);
            Add(command, "$join", RequestKind.GroupJoin.ToStorageValue());
            Add(command, "$invited", RequestKind.GroupInvitedJoin.ToStorageValue());
            Add(command, "$filtered", isFiltered ? 1 : 0);
            Add(command, "$start", startSequence);
            Add(command, "$limit", (long)limit + 1);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var requests = new List<PendingRequest>();
            long? next = null;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var request = ReadRequest(reader);
                if (requests.Count == limit) { next = request.NotificationSequence; break; }
                requests.Add(request);
            }
            return (Requests: (IReadOnlyList<PendingRequest>)requests, NextSequence: next);
        }, cancellationToken);
    }
}
