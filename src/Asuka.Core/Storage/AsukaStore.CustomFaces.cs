namespace Asuka.Core;

public sealed partial class AsukaStore
{
    private void ApplyVersion10()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE custom_faces (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                self_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                asset_id TEXT NOT NULL REFERENCES assets(id),
                added_at INTEGER NOT NULL,
                UNIQUE(self_id, asset_id)
            );
            CREATE INDEX custom_faces_account_order ON custom_faces(self_id, sequence);
            PRAGMA user_version=10;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Task<IReadOnlyList<CustomFace>> GetCustomFacesAsync(string selfId, CancellationToken cancellationToken = default) =>
        WithGateAsync<IReadOnlyList<CustomFace>>(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"{CustomFaceSelect} WHERE c.self_id=$self_id ORDER BY c.sequence;";
            Add(command, "$self_id", selfId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var faces = new List<CustomFace>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) faces.Add(ReadCustomFace(reader));
            return faces;
        }, cancellationToken);

    internal async Task<CustomFace> AddCustomFaceAsync(string selfId, Asset asset, CancellationToken cancellationToken)
    {
        var result = await WithGateAsync(async token =>
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO assets(id, name, mime_type, byte_count, source_kind, source_value)
                    VALUES($asset_id, $name, $mime_type, $byte_count, 'inline', NULL)
                    ON CONFLICT(id) DO NOTHING;
                INSERT INTO custom_faces(self_id, asset_id, added_at)
                    VALUES($self_id, $asset_id, $added_at) ON CONFLICT(self_id, asset_id) DO NOTHING;
                """;
            Add(command, "$asset_id", asset.Id);
            Add(command, "$name", asset.Name);
            Add(command, "$mime_type", asset.MimeType);
            Add(command, "$byte_count", asset.ByteCount);
            Add(command, "$self_id", selfId);
            Add(command, "$added_at", ToTimestamp(DateTimeOffset.UtcNow));
            var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0;
            command.CommandText = $"{CustomFaceSelect} WHERE c.self_id=$self_id AND c.asset_id=$asset_id;";
            CustomFace face;
            using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new StorePersistenceException("The custom image was not stored.");
                face = ReadCustomFace(reader);
            }
            transaction.Commit();
            return (Face: face, Changed: changed);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Changed) Publish(StoreChangeKind.CustomFaces);
        return result.Face;
    }

    internal async Task<bool> RemoveCustomFaceAsync(string selfId, string assetId, CancellationToken cancellationToken)
    {
        var changed = await WithGateAsync(async token =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM custom_faces WHERE self_id=$self_id AND asset_id=$asset_id;";
            Add(command, "$self_id", selfId);
            Add(command, "$asset_id", assetId);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0;
        }, cancellationToken).ConfigureAwait(false);
        if (changed) Publish(StoreChangeKind.CustomFaces);
        return changed;
    }

    private const string CustomFaceSelect = """
        SELECT a.id, a.name, a.mime_type, a.byte_count, 'inline', NULL, c.self_id, c.added_at, c.sequence
        FROM custom_faces c JOIN assets a ON a.id=c.asset_id
        """;

    private static CustomFace ReadCustomFace(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetString(6), ReadAsset(reader), FromTimestamp(reader.GetInt64(7)), reader.GetInt64(8));
}
