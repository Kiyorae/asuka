using Microsoft.Data.Sqlite;

namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion4()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE shared_folders (
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                parent_folder_id TEXT NOT NULL DEFAULT '/',
                name TEXT NOT NULL,
                creator_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                last_modified_at INTEGER NOT NULL,
                UNIQUE(group_id, parent_folder_id, name)
            );
            CREATE INDEX ix_shared_folders_group ON shared_folders(group_id, parent_folder_id);
            CREATE TABLE shared_files (
                id TEXT PRIMARY KEY NOT NULL,
                group_id TEXT NULL REFERENCES groups(id) ON DELETE CASCADE,
                recipient_id TEXT NULL REFERENCES users(id) ON DELETE CASCADE,
                uploader_id TEXT NOT NULL,
                asset_id TEXT NOT NULL REFERENCES assets(id),
                name TEXT NOT NULL,
                parent_folder_id TEXT NOT NULL DEFAULT '/',
                uploaded_at INTEGER NOT NULL,
                expires_at INTEGER NULL,
                downloaded_times INTEGER NOT NULL DEFAULT 0 CHECK(downloaded_times >= 0),
                file_hash TEXT NOT NULL DEFAULT '',
                CHECK((group_id IS NOT NULL AND recipient_id IS NULL) OR
                    (group_id IS NULL AND recipient_id IS NOT NULL))
            );
            CREATE INDEX ix_shared_files_group ON shared_files(group_id, parent_folder_id, uploaded_at);
            CREATE INDEX ix_shared_files_private ON shared_files(recipient_id, uploader_id, uploaded_at);
            PRAGMA user_version=4;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<SharedFile?> GetGroupFileAsync(string groupId, string fileId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token => FirstSharedItem(await ReadSharedFilesAsync("f.group_id=$peer AND f.id=$id", groupId, fileId, null, token)
            .ConfigureAwait(false)), cancellationToken);

    public Task<SharedFile?> GetPrivateFileAsync(string fileId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token => FirstSharedItem(await ReadSharedFilesAsync("f.group_id IS NULL AND f.id=$id", null, fileId, null, token)
            .ConfigureAwait(false)), cancellationToken);

    public Task<SharedFile?> GetSharedFileAsync(string fileId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token => FirstSharedItem(await ReadSharedFilesAsync("f.id=$id", null, fileId, null, token)
            .ConfigureAwait(false)), cancellationToken);

    public Task<IReadOnlyList<SharedFile>> GetGroupFilesAsync(
        string groupId, string parentFolderId = "/", CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ReadSharedFilesAsync(
            "f.group_id=$peer AND f.parent_folder_id=$folder AND (f.expires_at IS NULL OR f.expires_at>$now)",
            groupId, null, parentFolderId, token), cancellationToken);

    public Task<IReadOnlyList<SharedFile>> GetPrivateFilesAsync(
        string userId, string peerId, CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ReadSharedFilesAsync(
            "f.group_id IS NULL AND ((f.recipient_id=$peer AND f.uploader_id=$id) OR (f.recipient_id=$id AND f.uploader_id=$peer)) " +
            "AND (f.expires_at IS NULL OR f.expires_at>$now)", peerId, userId, null, token), cancellationToken);

    public Task<SharedFolder?> GetGroupFolderAsync(string groupId, string folderId, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token => FirstSharedItem(await ReadSharedFoldersAsync(groupId, folderId, null, token).ConfigureAwait(false)), cancellationToken);

    private static T? FirstSharedItem<T>(IReadOnlyList<T> values) where T : class => values.Count == 0 ? null : values[0];

    public Task<IReadOnlyList<SharedFolder>> GetGroupFoldersAsync(
        string groupId, string parentFolderId = "/", CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ReadSharedFoldersAsync(groupId, null, parentFolderId, token), cancellationToken);

    internal Task SaveSharedFileAsync(SharedFile file, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE shared_folders SET last_modified_at=$modified
                WHERE group_id=$group AND id=(SELECT parent_folder_id FROM shared_files WHERE id=$id);
                INSERT INTO shared_files(id, group_id, recipient_id, uploader_id, asset_id, name, parent_folder_id,
                    uploaded_at, expires_at, downloaded_times, file_hash)
                VALUES($id, $group, $recipient, $uploader, $asset, $name, $folder, $uploaded, $expires, $downloads, $hash)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name, parent_folder_id=excluded.parent_folder_id,
                    expires_at=excluded.expires_at, downloaded_times=excluded.downloaded_times;
                UPDATE shared_folders SET last_modified_at=$modified WHERE group_id=$group AND id=$folder;
                """;
            Add(command, "$id", file.Id);
            Add(command, "$group", file.GroupId);
            Add(command, "$recipient", file.RecipientId);
            Add(command, "$uploader", file.UploaderId);
            Add(command, "$asset", file.Asset.Id);
            Add(command, "$name", file.Name);
            Add(command, "$folder", file.ParentFolderId);
            Add(command, "$uploaded", ToTimestamp(file.UploadedAt));
            Add(command, "$expires", ToTimestamp(file.ExpiresAt));
            Add(command, "$downloads", file.DownloadedTimes);
            Add(command, "$hash", file.FileHash);
            Add(command, "$modified", ToTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Files, cancellationToken);

    internal Task DeleteSharedFileAsync(SharedFile file, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM shared_files WHERE id=$id;
                UPDATE shared_folders SET last_modified_at=$modified WHERE group_id=$group AND id=$folder;
                """;
            Add(command, "$id", file.Id);
            Add(command, "$group", file.GroupId);
            Add(command, "$folder", file.ParentFolderId);
            Add(command, "$modified", ToTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Files, cancellationToken);

    internal Task RecordSharedFileDownloadAsync(string fileId, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE shared_files SET downloaded_times=downloaded_times + 1
                WHERE id=$id AND downloaded_times<2147483647;
                """;
            Add(command, "$id", fileId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Files, cancellationToken);

    internal Task SaveSharedFolderAsync(SharedFolder folder, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO shared_folders(id, group_id, parent_folder_id, name, creator_id, created_at, last_modified_at)
                VALUES($id, $group, $parent, $name, $creator, $created, $modified)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name, last_modified_at=excluded.last_modified_at;
                """;
            Add(command, "$id", folder.Id);
            Add(command, "$group", folder.GroupId);
            Add(command, "$parent", folder.ParentFolderId);
            Add(command, "$name", folder.Name);
            Add(command, "$creator", folder.CreatorId);
            Add(command, "$created", ToTimestamp(folder.CreatedAt));
            Add(command, "$modified", ToTimestamp(folder.LastModifiedAt));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Files, cancellationToken);

    internal Task DeleteSharedFolderAsync(SharedFolder folder, CancellationToken cancellationToken) =>
        WriteAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            // Official Milky create_group_folder creates root-level folders only.
            command.CommandText = """
                DELETE FROM shared_files WHERE group_id=$group AND parent_folder_id=$id;
                DELETE FROM shared_folders WHERE group_id=$group AND id=$id;
                """;
            Add(command, "$id", folder.Id);
            Add(command, "$group", folder.GroupId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, StoreChangeKind.Files, cancellationToken);

    private async Task<IReadOnlyList<SharedFile>> ReadSharedFilesAsync(
        string predicate, string? peer, string? id, string? folder, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.id, f.group_id, f.recipient_id, f.uploader_id, f.name, f.parent_folder_id, f.uploaded_at,
                f.expires_at, f.downloaded_times, f.file_hash,
                a.id, a.name, a.mime_type, a.byte_count, a.source_kind, a.source_value
            FROM shared_files f JOIN assets a ON a.id=f.asset_id WHERE {predicate} ORDER BY f.uploaded_at, f.id;
            """;
        Add(command, "$peer", peer);
        Add(command, "$id", id);
        Add(command, "$folder", folder);
        Add(command, "$now", ToTimestamp(DateTimeOffset.UtcNow));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<SharedFile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var asset = new Asset(reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt64(13), new AssetSource(EnumStorage.ParseStorageValue<AssetSourceKind>(reader.GetString(14)),
                    reader.IsDBNull(15) ? null : reader.GetString(15)));
            files.Add(new SharedFile(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), asset,
                reader.GetString(4), reader.GetString(5), FromTimestamp(reader.GetInt64(6)),
                GetOptionalTimestamp(reader, 7), reader.GetInt32(8), reader.GetString(9)));
        }

        return files;
    }

    private async Task<IReadOnlyList<SharedFolder>> ReadSharedFoldersAsync(
        string groupId, string? folderId, string? parentId, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.group_id, d.parent_folder_id, d.name, d.creator_id, d.created_at, d.last_modified_at,
                (SELECT COUNT(*) FROM shared_files f WHERE f.group_id=d.group_id AND f.parent_folder_id=d.id
                    AND (f.expires_at IS NULL OR f.expires_at>$now))
            FROM shared_folders d WHERE d.group_id=$group AND ($id IS NULL OR d.id=$id)
                AND ($parent IS NULL OR d.parent_folder_id=$parent) ORDER BY d.created_at, d.id;
            """;
        Add(command, "$group", groupId);
        Add(command, "$id", folderId);
        Add(command, "$parent", parentId);
        Add(command, "$now", ToTimestamp(DateTimeOffset.UtcNow));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var folders = new List<SharedFolder>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new SharedFolder(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), FromTimestamp(reader.GetInt64(5)), FromTimestamp(reader.GetInt64(6)), reader.GetInt32(7)));
        }

        return folders;
    }
}
