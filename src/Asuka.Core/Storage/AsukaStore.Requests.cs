using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    internal Task AcceptFriendRequestAsync(
        PendingRequest request,
        string remark,
        CancellationToken cancellationToken) =>
        WriteAsync(
            async token =>
            {
                using var transaction = _connection.BeginTransaction(deferred: false);
                if (!await ExistsAsync("users", request.SelfId, token, transaction).ConfigureAwait(false))
                {
                    throw new PlatformException(PlatformError.UserNotFound, $"User not found: {request.SelfId}")
                    {
                        UserId = request.SelfId,
                        ResourceId = request.SelfId,
                    };
                }

                if (!await ExistsAsync("users", request.RequesterId, token, transaction).ConfigureAwait(false))
                {
                    throw new PlatformException(PlatformError.UserNotFound, $"User not found: {request.RequesterId}")
                    {
                        UserId = request.RequesterId,
                        ResourceId = request.RequesterId,
                    };
                }

                var createdAt = ToTimestamp(DateTimeOffset.UtcNow);
                using (var friendships = _connection.CreateCommand())
                {
                    friendships.Transaction = transaction;
                    friendships.CommandText = """
                        INSERT INTO friendships(user_id, friend_id, remark, created_at)
                        VALUES($self_id, $requester_id, $remark, $created_at)
                        ON CONFLICT(user_id, friend_id) DO UPDATE SET
                            remark=excluded.remark, created_at=excluded.created_at;
                        INSERT INTO friendships(user_id, friend_id, remark, created_at)
                        VALUES($requester_id, $self_id, '', $created_at)
                        ON CONFLICT(user_id, friend_id) DO UPDATE SET
                            remark=excluded.remark, created_at=excluded.created_at;
                        """;
                    Add(friendships, "$self_id", request.SelfId);
                    Add(friendships, "$requester_id", request.RequesterId);
                    Add(friendships, "$remark", remark);
                    Add(friendships, "$created_at", createdAt);
                    _ = await friendships.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await MarkAcceptedAsync(request.Id, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Requests | StoreChangeKind.Friendships,
            cancellationToken);

    internal Task<GroupMember> AcceptGroupRequestAsync(
        PendingRequest request,
        string groupId,
        string userId,
        string operatorId,
        CancellationToken cancellationToken) =>
        WriteAsync(
            async token =>
            {
                using var transaction = _connection.BeginTransaction(deferred: false);
                int maxMemberCount;
                using (var groupCommand = _connection.CreateCommand())
                {
                    groupCommand.Transaction = transaction;
                    groupCommand.CommandText = "SELECT max_member_count FROM groups WHERE id=$id LIMIT 1;";
                    Add(groupCommand, "$id", groupId);
                    var value = await groupCommand.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (value is null)
                    {
                        throw new PlatformException(PlatformError.GroupNotFound, $"Group not found: {groupId}")
                        {
                            GroupId = groupId,
                            ResourceId = groupId,
                        };
                    }

                    maxMemberCount = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                if (!await ExistsAsync("users", userId, token, transaction).ConfigureAwait(false))
                {
                    throw new PlatformException(PlatformError.UserNotFound, $"User not found: {userId}")
                    {
                        UserId = userId,
                        ResourceId = userId,
                    };
                }

                if (!await MemberExistsAsync(groupId, operatorId, transaction, token).ConfigureAwait(false))
                {
                    throw new PlatformException(
                        PlatformError.NotAMember,
                        $"User {operatorId} is not a member of group {groupId}")
                    {
                        GroupId = groupId,
                        UserId = operatorId,
                    };
                }

                if (await MemberExistsAsync(groupId, userId, transaction, token).ConfigureAwait(false))
                {
                    throw new PlatformException(
                        PlatformError.AlreadyExists,
                        $"Already exists: User {userId} is already a member of group {groupId}")
                    {
                        GroupId = groupId,
                        UserId = userId,
                    };
                }

                using (var countCommand = _connection.CreateCommand())
                {
                    countCommand.Transaction = transaction;
                    countCommand.CommandText = "SELECT COUNT(*) FROM group_members WHERE group_id=$group_id;";
                    Add(countCommand, "$group_id", groupId);
                    var count = Convert.ToInt32(
                        await countCommand.ExecuteScalarAsync(token).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (count >= maxMemberCount)
                    {
                        throw new PlatformException(
                            PlatformError.NotPermitted,
                            $"The group has reached its member limit of {maxMemberCount}")
                        {
                            GroupId = groupId,
                        };
                    }
                }

                var member = new GroupMember(groupId, userId);
                using (var memberCommand = _connection.CreateCommand())
                {
                    memberCommand.Transaction = transaction;
                    memberCommand.CommandText = """
                        INSERT INTO group_members(
                            group_id, user_id, card, role, title, joined_at, last_sent_at, muted_until)
                        VALUES($group_id, $user_id, '', 'member', '', $joined_at, NULL, NULL);
                        """;
                    Add(memberCommand, "$group_id", groupId);
                    Add(memberCommand, "$user_id", userId);
                    Add(memberCommand, "$joined_at", ToTimestamp(member.JoinedAt));
                    _ = await memberCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await MarkAcceptedAsync(request.Id, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return member;
            },
            StoreChangeKind.Requests | StoreChangeKind.Members,
            cancellationToken);

    public Task SaveAsync(PendingRequest request, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO pending_requests(
                        id, flag, kind, requester_id, group_id, self_id, comment, time,
                        resolution_state, resolution_reason)
                    VALUES($id, $flag, $kind, $requester_id, $group_id, $self_id, $comment, $time,
                        $resolution_state, $resolution_reason)
                    ON CONFLICT(id) DO UPDATE SET
                        flag=excluded.flag, kind=excluded.kind, requester_id=excluded.requester_id,
                        group_id=excluded.group_id, self_id=excluded.self_id, comment=excluded.comment,
                        time=excluded.time, resolution_state=excluded.resolution_state,
                        resolution_reason=excluded.resolution_reason;
                    """;
                BindRequest(command, request);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Requests,
            cancellationToken);

    public Task<PendingRequest?> GetRequestAsync(string id, CancellationToken cancellationToken = default) =>
        ReadRequestAsync("id", id, cancellationToken);

    public Task<PendingRequest?> GetRequestByFlagAsync(string flag, CancellationToken cancellationToken = default) =>
        ReadRequestAsync("flag", flag, cancellationToken);

    public Task<IReadOnlyList<PendingRequest>> GetPendingRequestsAsync(
        string selfId,
        RequestKind? kind = null,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ReadPendingRequestsAsync(selfId, kind, token), cancellationToken);

    public Task<PendingRequest?> ResolveRequestAsync(
        string requestId,
        RequestResolution resolution,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = """
                        UPDATE pending_requests
                        SET resolution_state=$state, resolution_reason=$reason
                        WHERE id=$id;
                        """;
                    Add(command, "$state", resolution.Status.ToString().ToLowerInvariant());
                    Add(command, "$reason", resolution.Reason);
                    Add(command, "$id", requestId);
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                    {
                        return null;
                    }
                }

                return await ReadRequestWithoutGateAsync("id", requestId, token).ConfigureAwait(false);
            },
            StoreChangeKind.Requests,
            cancellationToken);

    public Task SaveAsync(Asset asset, CancellationToken cancellationToken = default) =>
        WriteAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO assets(id, name, mime_type, byte_count, source_kind, source_value)
                    VALUES($id, $name, $mime_type, $byte_count, $source_kind, $source_value)
                    ON CONFLICT(id) DO UPDATE SET
                        name=excluded.name, mime_type=excluded.mime_type, byte_count=excluded.byte_count,
                        source_kind=excluded.source_kind, source_value=excluded.source_value;
                    """;
                Add(command, "$id", asset.Id);
                Add(command, "$name", asset.Name);
                Add(command, "$mime_type", asset.MimeType);
                Add(command, "$byte_count", asset.ByteCount);
                Add(command, "$source_kind", asset.Source.Kind.ToString().ToLowerInvariant());
                Add(command, "$source_value", asset.Source.Value);
                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StoreChangeKind.Assets,
            cancellationToken);

    public Task<Asset?> GetAssetAsync(string id, CancellationToken cancellationToken = default) =>
        WithGateAsync(
            async token =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, mime_type, byte_count, source_kind, source_value
                    FROM assets WHERE id=$id LIMIT 1;
                    """;
                Add(command, "$id", id);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadAsset(reader) : null;
            },
            cancellationToken);

    private Task<PendingRequest?> ReadRequestAsync(
        string column,
        string value,
        CancellationToken cancellationToken) =>
        WithGateAsync(token => ReadRequestWithoutGateAsync(column, value, token), cancellationToken);

    private async Task<PendingRequest?> ReadRequestWithoutGateAsync(
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {RequestSelect} WHERE {column}=$value LIMIT 1;
            """;
        Add(command, "$value", value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRequest(reader) : null;
    }

    private async Task<IReadOnlyList<PendingRequest>> ReadPendingRequestsAsync(
        string selfId,
        RequestKind? kind,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = kind is null
            ? $"{RequestSelect} WHERE self_id=$self_id AND resolution_state IS NULL ORDER BY time DESC, id;"
            : $"{RequestSelect} WHERE self_id=$self_id AND resolution_state IS NULL AND kind=$kind ORDER BY time DESC, id;";
        Add(command, "$self_id", selfId);
        if (kind is { } requestKind)
        {
            Add(command, "$kind", requestKind.ToStorageValue());
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var requests = new List<PendingRequest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            requests.Add(ReadRequest(reader));
        }

        return requests;
    }

    private const string RequestSelect = """
        SELECT id, flag, kind, requester_id, group_id, self_id, comment, time,
               resolution_state, resolution_reason
        FROM pending_requests
        """;

    private async Task MarkAcceptedAsync(
        string requestId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE pending_requests SET resolution_state='accepted', resolution_reason=''
            WHERE id=$id AND resolution_state IS NULL;
            """;
        Add(command, "$id", requestId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new PlatformException(PlatformError.NotPermitted, "This request has already been resolved");
        }
    }

    private async Task<bool> MemberExistsAsync(
        string groupId,
        string userId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM group_members WHERE group_id=$group_id AND user_id=$user_id);
            """;
        Add(command, "$group_id", groupId);
        Add(command, "$user_id", userId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void BindRequest(SqliteCommand command, PendingRequest request)
    {
        Add(command, "$id", request.Id);
        Add(command, "$flag", request.Flag);
        Add(command, "$kind", request.Kind.ToStorageValue());
        Add(command, "$requester_id", request.RequesterId);
        Add(command, "$group_id", request.GroupId);
        Add(command, "$self_id", request.SelfId);
        Add(command, "$comment", request.Comment);
        Add(command, "$time", ToTimestamp(request.Time));
        Add(command, "$resolution_state", request.Resolution?.Status.ToString().ToLowerInvariant());
        Add(command, "$resolution_reason", request.Resolution?.Reason);
    }

    private static PendingRequest ReadRequest(SqliteDataReader reader)
    {
        RequestResolution? resolution = null;
        if (!reader.IsDBNull(8))
        {
            var status = EnumStorage.ParseStorageValue<RequestResolutionStatus>(reader.GetString(8));
            resolution = new RequestResolution(status, reader.IsDBNull(9) ? string.Empty : reader.GetString(9));
        }

        return new PendingRequest(
            kind: EnumStorage.ParseStorageValue<RequestKind>(reader.GetString(2)),
            requesterId: reader.GetString(3),
            selfId: reader.GetString(5),
            groupId: reader.IsDBNull(4) ? null : reader.GetString(4),
            comment: reader.GetString(6),
            id: reader.GetString(0),
            flag: reader.GetString(1),
            time: FromTimestamp(reader.GetInt64(7)),
            resolution: resolution);
    }

    private static Asset ReadAsset(SqliteDataReader reader)
    {
        var kind = EnumStorage.ParseStorageValue<AssetSourceKind>(reader.GetString(4));
        var source = new AssetSource(kind, reader.IsDBNull(5) ? null : reader.GetString(5));
        return new Asset(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            source);
    }
}
