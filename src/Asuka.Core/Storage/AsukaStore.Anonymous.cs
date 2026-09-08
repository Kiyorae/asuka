using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion12()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE groups ADD COLUMN anonymous_enabled INTEGER NOT NULL DEFAULT 0 CHECK(anonymous_enabled IN (0,1));
            ALTER TABLE messages ADD COLUMN anonymous_id INTEGER NULL;
            ALTER TABLE messages ADD COLUMN anonymous_name TEXT NULL;
            ALTER TABLE messages ADD COLUMN anonymous_flag TEXT NULL;
            CREATE TABLE group_anonymous_identities (
                group_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                anonymous_id INTEGER NOT NULL UNIQUE CHECK(anonymous_id > 0),
                name TEXT NOT NULL,
                flag TEXT NOT NULL UNIQUE,
                muted_until INTEGER NULL,
                PRIMARY KEY(group_id,sender_id),
                FOREIGN KEY(group_id,sender_id) REFERENCES group_members(group_id,user_id) ON DELETE CASCADE
            );
            PRAGMA user_version=12;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<AnonymousIdentity?> GetAnonymousIdentityAsync(string groupId, string flag,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT anonymous_id,name,flag FROM group_anonymous_identities WHERE group_id=$group AND flag=$flag;";
            Add(command, "$group", groupId);
            Add(command, "$flag", flag);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? new AnonymousIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
        }, cancellationToken);

    internal Task SetGroupAnonymousAsync(string groupId, string operatorId, bool enable,
        CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            await RequireAnonymousAdministratorAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE groups SET anonymous_enabled=$enabled WHERE id=$group;";
            Add(command, "$group", groupId);
            Add(command, "$enabled", enable);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Groups, cancellationToken);

    internal Task BanAnonymousAsync(string groupId, string flag, string operatorId, TimeSpan duration,
        CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            var now = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(flag) || duration <= TimeSpan.Zero || duration > DateTimeOffset.MaxValue - now)
                throw new PlatformException(PlatformError.InvalidParameter,
                    "Anonymous bans require a flag and a positive duration with a representable expiry; they cannot be cancelled");
            using var transaction = _connection.BeginTransaction(deferred: false);
            await RequireAnonymousAdministratorAsync(groupId, operatorId, transaction, token).ConfigureAwait(false);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE group_anonymous_identities SET muted_until=MAX(COALESCE(muted_until,0),$until)
                WHERE group_id=$group AND flag=$flag;
                """;
            Add(command, "$group", groupId);
            Add(command, "$flag", flag);
            Add(command, "$until", ToTimestamp(now + duration));
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                throw new PlatformException(PlatformError.InvalidParameter, "The anonymous identity does not exist in this group");
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Groups, cancellationToken);

    private async Task RequireAnonymousAdministratorAsync(string groupId, string operatorId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await ExistsAsync("groups", groupId, cancellationToken, transaction).ConfigureAwait(false))
            throw new PlatformException(PlatformError.GroupNotFound, "The group does not exist");
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT role FROM group_members WHERE group_id=$group AND user_id=$operator;";
        Add(command, "$group", groupId);
        Add(command, "$operator", operatorId);
        var role = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (role is not ("owner" or "admin"))
            throw new PlatformException(PlatformError.NotPermitted, "Administrator privileges are required to manage anonymous messaging");
    }

    private async Task<Message> PrepareLiveMessageAsync(Message message, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var markers = message.Content.OfType<AnonymousSegment>().ToArray();
        if (markers.Length > 1 || message.Content.OfType<ForwardSegment>().Any(ContainsAnonymousMarker))
            throw new PlatformException(PlatformError.InvalidParameter, "Only one top-level anonymous marker is allowed");
        if (markers.Length != 0 && message.Scene != ChatScene.Group)
            throw new PlatformException(PlatformError.InvalidParameter, "Anonymous messages are only available in groups");
        if (message.Scene != ChatScene.Group) return message;

        // The same SQLite transaction protects group mode, membership, bans, alias creation and append.
        // This also closes races with callers that use another PlatformService or write to the store directly.
        bool enabled;
        bool wholeMuted;
        using (var group = _connection.CreateCommand())
        {
            group.Transaction = transaction;
            group.CommandText = "SELECT anonymous_enabled,whole_muted FROM groups WHERE id=$group;";
            Add(group, "$group", message.PeerId);
            using var reader = await group.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PlatformException(PlatformError.GroupNotFound, "The group does not exist");
            enabled = reader.GetBoolean(0);
            wholeMuted = reader.GetBoolean(1);
        }
        using (var member = _connection.CreateCommand())
        {
            member.Transaction = transaction;
            member.CommandText = "SELECT role,muted_until FROM group_members WHERE group_id=$group AND user_id=$sender;";
            Add(member, "$group", message.PeerId);
            Add(member, "$sender", message.SenderId);
            using var reader = await member.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PlatformException(PlatformError.NotAMember, "The sender is not a member of this group");
            if (GetOptionalTimestamp(reader, 1) is { } until && until > DateTimeOffset.UtcNow)
                throw new PlatformException(PlatformError.Muted, "The sender is muted");
            if (wholeMuted && reader.GetString(0) == "member")
                throw new PlatformException(PlatformError.WholeGroupMuted, "Group-wide mute is enabled");
        }
        using (var self = _connection.CreateCommand())
        {
            self.Transaction = transaction;
            self.CommandText = "SELECT EXISTS(SELECT 1 FROM group_members WHERE group_id=$group AND user_id=$self);";
            Add(self, "$group", message.PeerId);
            Add(self, "$self", message.SelfId);
            if ((long)(await self.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
                throw new PlatformException(PlatformError.NotAMember, "The bot account is not a member of this group");
        }
        if (markers.Length == 0) return message;
        var result = message with { Content = message.Content.Where(segment => segment is not AnonymousSegment).ToArray() };
        if (!enabled)
        {
            if (markers[0].Ignore) return result;
            throw new PlatformException(PlatformError.NotPermitted, "Anonymous messaging is disabled in this group");
        }
        AnonymousIdentity? identity = null;
        using (var existing = _connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT anonymous_id,name,flag,muted_until FROM group_anonymous_identities
                WHERE group_id=$group AND sender_id=$sender;
                """;
            Add(existing, "$group", message.PeerId);
            Add(existing, "$sender", message.SenderId);
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (GetOptionalTimestamp(reader, 3) is { } until && until > DateTimeOffset.UtcNow)
                {
                    if (markers[0].Ignore) return result;
                    throw new PlatformException(PlatformError.NotPermitted, "This anonymous identity is banned");
                }
                identity = new AnonymousIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
            }
        }
        identity ??= await CreateAnonymousIdentityAsync(message.PeerId, message.SenderId, transaction, cancellationToken)
            .ConfigureAwait(false);
        return result with { Anonymous = identity };
    }

    private async Task<AnonymousIdentity> CreateAnonymousIdentityAsync(string groupId, string senderId,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Positive 53-bit aliases remain exact for JSON consumers and do not derive from an account ID.
            var id = (long)(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong))) & ((1UL << 53) - 1));
            if (id == 0) continue;
            using (var collision = _connection.CreateCommand())
            {
                collision.Transaction = transaction;
                collision.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM users WHERE id=$id)
                        OR EXISTS(SELECT 1 FROM group_anonymous_identities WHERE anonymous_id=$numeric);
                    """;
                Add(collision, "$id", id.ToString(CultureInfo.InvariantCulture));
                Add(collision, "$numeric", id);
                if ((long)(await collision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0) continue;
            }
            var identity = new AnonymousIdentity(id, $"Anonymous {Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO group_anonymous_identities(group_id,sender_id,anonymous_id,name,flag)
                VALUES($group,$sender,$id,$name,$flag);
                """;
            Add(insert, "$group", groupId);
            Add(insert, "$sender", senderId);
            Add(insert, "$id", identity.Id);
            Add(insert, "$name", identity.Name);
            Add(insert, "$flag", identity.Flag);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return identity;
        }
    }

    private static bool ContainsAnonymousMarker(MessageSegment segment) => segment is AnonymousSegment
        || segment is UnsupportedSegment unsupported && string.Equals(unsupported.Type, "anonymous", StringComparison.OrdinalIgnoreCase)
        || segment is ForwardSegment forward && forward.Nodes.Any(node => node.Content.Any(ContainsAnonymousMarker));

    private static void ValidateStoredAnonymousMessage(Message message)
    {
        if (message.Content.Any(ContainsAnonymousMarker))
            throw new PlatformException(PlatformError.InvalidParameter, "Anonymous markers must be resolved before messages are stored");
        if (message.Anonymous is { } identity
            && (message.Scene != ChatScene.Group || identity.Id <= 0
                || string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.Flag)))
            throw new PlatformException(PlatformError.InvalidParameter, "Invalid anonymous message identity");
    }
}
