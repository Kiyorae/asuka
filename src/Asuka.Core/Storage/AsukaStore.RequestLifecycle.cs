namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion7()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE pending_requests ADD COLUMN target_user_id TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN source_group_id TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN is_filtered INTEGER NOT NULL DEFAULT 0 CHECK(is_filtered IN (0,1));
            ALTER TABLE pending_requests ADD COLUMN via TEXT NOT NULL DEFAULT 'asuka';
            ALTER TABLE pending_requests ADD COLUMN resolved_by TEXT NULL;
            ALTER TABLE pending_requests ADD COLUMN notification_seq INTEGER NOT NULL DEFAULT 0;
            UPDATE pending_requests SET notification_seq=rowid;
            CREATE TABLE request_notification_sequence (
                id INTEGER PRIMARY KEY CHECK(id=1),
                next_seq INTEGER NOT NULL CHECK(next_seq > 0));
            INSERT INTO request_notification_sequence(id, next_seq)
                SELECT 1, COALESCE(MAX(notification_seq), 0) + 1 FROM pending_requests;
            CREATE UNIQUE INDEX ux_requests_notification_seq ON pending_requests(notification_seq)
                WHERE notification_seq > 0;
            CREATE INDEX ix_requests_self_filter_time ON pending_requests(self_id, is_filtered, time DESC);
            PRAGMA user_version=7;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Returns request history, including accepted, rejected, and ignored requests.</summary>
    public Task<IReadOnlyList<PendingRequest>> GetRequestsAsync(
        string selfId,
        RequestKind? kind = null,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = kind is null
                ? $"{RequestSelect} WHERE self_id=$self_id ORDER BY time DESC, notification_seq DESC;"
                : $"{RequestSelect} WHERE self_id=$self_id AND kind=$kind ORDER BY time DESC, notification_seq DESC;";
            Add(command, "$self_id", selfId);
            if (kind is { } requestKind) Add(command, "$kind", requestKind.ToStorageValue());
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var requests = new List<PendingRequest>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) requests.Add(ReadRequest(reader));
            return (IReadOnlyList<PendingRequest>)requests;
        }, cancellationToken);
}
