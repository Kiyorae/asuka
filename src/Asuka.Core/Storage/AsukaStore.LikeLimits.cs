namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion8()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        // Older databases have no daily breakdown. Conservatively attribute their
        // cumulative count to the last-send day rather than granting extra likes.
        command.CommandText = """
            CREATE TABLE profile_like_days (
                user_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                day_number INTEGER NOT NULL,
                count INTEGER NOT NULL CHECK(typeof(count)='integer' AND count > 0),
                PRIMARY KEY(user_id, sender_id, day_number),
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
                FOREIGN KEY(sender_id) REFERENCES users(id) ON DELETE CASCADE
            );
            INSERT INTO profile_like_days(user_id, sender_id, day_number, count)
                SELECT user_id, sender_id, (last_sent_at + 288000000000) / 864000000000, count
                FROM profile_likes;
            PRAGMA user_version=8;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // The simulator uses fixed UTC+8 midnight for QQ's per-day profile-like quota.
    // A supplied instant keeps rollover tests independent of the wall clock.
    internal Task AddProfileLikesAsync(
        string userId,
        string senderId,
        int count,
        int? dailyLimit,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            if (count <= 0 || dailyLimit is <= 0)
                throw new PlatformException(PlatformError.InvalidParameter, "Like count and any daily limit must be positive");
            var day = DateOnly.FromDateTime(sentAt.ToOffset(TimeSpan.FromHours(8)).DateTime).DayNumber;
            using var transaction = _connection.BeginTransaction();
            using var current = _connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = """
                SELECT count FROM profile_like_days
                WHERE user_id=$user_id AND sender_id=$sender_id AND day_number=$day;
                """;
            Add(current, "$user_id", userId);
            Add(current, "$sender_id", senderId);
            Add(current, "$day", day);
            var used = (long?)await current.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0;
            if (dailyLimit is { } limit && (used >= limit || count > limit - used))
                throw new PlatformException(PlatformError.NotPermitted, $"The daily limit of {limit} profile likes has been reached");

            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                INSERT INTO profile_like_days(user_id, sender_id, day_number, count)
                VALUES($user_id, $sender_id, $day, $count)
                ON CONFLICT(user_id, sender_id, day_number) DO UPDATE SET
                    count=profile_like_days.count + excluded.count;
                INSERT INTO profile_likes(user_id, sender_id, count, last_sent_at)
                VALUES($user_id, $sender_id, $count, $last_sent_at)
                ON CONFLICT(user_id, sender_id) DO UPDATE SET
                    count=profile_likes.count + excluded.count, last_sent_at=excluded.last_sent_at;
                """;
            Add(update, "$user_id", userId);
            Add(update, "$sender_id", senderId);
            Add(update, "$day", day);
            Add(update, "$count", count);
            Add(update, "$last_sent_at", ToTimestamp(sentAt));
            _ = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
        }, StoreChangeKind.Users, cancellationToken);
}
