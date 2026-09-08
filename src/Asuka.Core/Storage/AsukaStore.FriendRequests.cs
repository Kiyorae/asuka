using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    internal Task<PendingRequest> CreateFriendRequestAsync(PendingRequest request, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            foreach (var userId in new[] { request.RequesterId, request.SelfId })
            {
                if (!await ExistsAsync("users", userId, token, transaction).ConfigureAwait(false))
                    throw new PlatformException(PlatformError.UserNotFound, $"User not found: {userId}")
                    { UserId = userId, ResourceId = userId };
            }
            if (await HasFriendshipForRequestAsync(request, transaction, token).ConfigureAwait(false))
                throw new PlatformException(PlatformError.AlreadyExists, "The users are already friends");

            using (var pending = _connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM pending_requests WHERE kind='friend' AND resolution_state IS NULL
                        AND ((self_id=$self AND requester_id=$requester) OR (self_id=$requester AND requester_id=$self)));
                    """;
                Add(pending, "$self", request.SelfId);
                Add(pending, "$requester", request.RequesterId);
                if (Convert.ToInt64(await pending.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                    throw new PlatformException(PlatformError.AlreadyExists, "A friend request between these users is already pending");
            }

            long sequence;
            using (var counter = _connection.CreateCommand())
            {
                counter.Transaction = transaction;
                counter.CommandText = "SELECT next_seq FROM request_notification_sequence WHERE id=1;";
                sequence = Convert.ToInt64(await counter.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            var stored = request with { NotificationSequence = sequence };
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO pending_requests(id, flag, kind, requester_id, self_id, comment, time,
                        is_filtered, via, notification_seq)
                    VALUES($id, $flag, 'friend', $requester_id, $self_id, $comment, $time,
                        $is_filtered, $via, $notification_seq);
                    UPDATE request_notification_sequence SET next_seq=next_seq + 1 WHERE id=1;
                    """;
                BindRequest(insert, stored);
                _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return stored;
        }, StoreChangeKind.Requests, cancellationToken);

    private async Task<bool> HasFriendshipForRequestAsync(
        PendingRequest request, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM friendships
                WHERE (user_id=$self AND friend_id=$requester) OR (user_id=$requester AND friend_id=$self));
            """;
        Add(command, "$self", request.SelfId);
        Add(command, "$requester", request.RequesterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private async Task IgnoreOtherFriendRequestsAsync(
        PendingRequest accepted, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE pending_requests SET resolution_state='ignored',
                resolution_reason='Another request for this friendship was accepted', resolved_by=$self
            WHERE id<>$id AND kind='friend' AND resolution_state IS NULL
                AND ((self_id=$self AND requester_id=$requester) OR (self_id=$requester AND requester_id=$self));
            """;
        Add(command, "$id", accepted.Id);
        Add(command, "$self", accepted.SelfId);
        Add(command, "$requester", accepted.RequesterId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
