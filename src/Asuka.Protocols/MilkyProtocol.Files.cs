using System.Text.Json.Nodes;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class MilkyProtocol
{
    private async Task<ProtocolReply> FileApiAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        if (request.Name is "upload_private_file" or "upload_group_file"
            && request.GetText("file_uri")?.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new ProtocolReply(-500, Message: "file:// uploads are unavailable: protocol clients cannot read local files. Use base64:// or http(s):// instead.");
        }

        if (request.Name == "upload_private_file")
        {
            var userId = RequiredFileId(request, "user_id");
            var asset = await ImportSharedFileAsync(request, cancellationToken).ConfigureAwait(false);
            await using var stream = File.OpenRead(_media.GetPath(asset.Id));
            var hash = await TriSha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            var file = await _platform.SharePrivateFileAsync(userId, SelfId, asset, hash, cancellationToken).ConfigureAwait(false);
            return ProtocolReply.Success(new JsonObject { ["file_id"] = file.Id });
        }

        if (request.Name == "get_private_file_download_url")
        {
            var file = await _platform.GetPrivateFileForDownloadAsync(SelfId, RequiredFileId(request, "user_id"),
                RequiredFileText(request, "file_id"), RequiredFileText(request, "file_hash"),
                FileBoolean(request, "is_self_send", false), cancellationToken).ConfigureAwait(false);
            return await SharedFileUrlAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var groupId = RequiredFileId(request, "group_id");
        switch (request.Name)
        {
            case "upload_group_file":
                {
                    var folderId = FileFolder(request, "parent_folder_id");
                    // Validate the target before fetching untrusted, potentially large media.
                    _ = await _platform.GetGroupFileListingAsync(groupId, SelfId, folderId, cancellationToken).ConfigureAwait(false);
                    var asset = await ImportSharedFileAsync(request, cancellationToken).ConfigureAwait(false);
                    var file = await _platform.ShareGroupFileAsync(groupId, SelfId, asset, folderId, cancellationToken).ConfigureAwait(false);
                    return ProtocolReply.Success(new JsonObject { ["file_id"] = file.Id });
                }
            case "get_group_files":
                {
                    var listing = await _platform.GetGroupFileListingAsync(groupId, SelfId,
                        FileFolder(request, "parent_folder_id"), cancellationToken).ConfigureAwait(false);
                    return ProtocolReply.Success(new JsonObject
                    {
                        ["files"] = new JsonArray(listing.Files.Select(EncodeSharedFile).ToArray()),
                        ["folders"] = new JsonArray(listing.Folders.Select(EncodeSharedFolder).ToArray()),
                    });
                }
            case "get_group_file_download_url":
                {
                    var file = await _platform.GetGroupFileForDownloadAsync(groupId, RequiredFileText(request, "file_id"),
                        SelfId, cancellationToken).ConfigureAwait(false);
                    return await SharedFileUrlAsync(file, cancellationToken).ConfigureAwait(false);
                }
            case "move_group_file":
                await _platform.MoveGroupFileAsync(groupId, RequiredFileText(request, "file_id"), SelfId,
                    FileFolder(request, "parent_folder_id"), FileFolder(request, "target_folder_id"), cancellationToken).ConfigureAwait(false);
                break;
            case "rename_group_file":
                await _platform.RenameGroupFileAsync(groupId, RequiredFileText(request, "file_id"), SelfId,
                    RequiredFileText(request, "new_file_name"), FileFolder(request, "parent_folder_id"), cancellationToken).ConfigureAwait(false);
                break;
            case "delete_group_file":
                await _platform.DeleteGroupFileAsync(groupId, RequiredFileText(request, "file_id"), SelfId, cancellationToken).ConfigureAwait(false);
                break;
            case "persist_group_file":
                await _platform.PersistGroupFileAsync(groupId, RequiredFileText(request, "file_id"), SelfId, cancellationToken).ConfigureAwait(false);
                break;
            case "create_group_folder":
                {
                    var folder = await _platform.CreateGroupFolderAsync(groupId, SelfId, RequiredFileText(request, "folder_name"), cancellationToken)
                        .ConfigureAwait(false);
                    return ProtocolReply.Success(new JsonObject { ["folder_id"] = folder.Id });
                }
            case "rename_group_folder":
                await _platform.RenameGroupFolderAsync(groupId, RequiredFileText(request, "folder_id"), SelfId,
                    RequiredFileText(request, "new_folder_name"), cancellationToken).ConfigureAwait(false);
                break;
            case "delete_group_folder":
                await _platform.DeleteGroupFolderAsync(groupId, RequiredFileText(request, "folder_id"), SelfId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                return new ProtocolReply(-404, Message: $"Unknown file API: {request.Name}");
        }

        return ProtocolReply.Success();
    }

    private async Task<Asset> ImportSharedFileAsync(ProtocolCall request, CancellationToken cancellationToken)
    {
        var fileName = RequiredFileText(request, "file_name");
        var uri = RequiredFileText(request, "file_uri");
        if (!uri.StartsWith("base64://", StringComparison.Ordinal)
            && (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "file")))
            throw new ArgumentException("file_uri must use file://, http(s)://, or base64://");
        var asset = await _media.ResolveReferenceAsync(uri, null, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("The file URI could not be resolved");
        // File names are share metadata, independent of the content-addressed asset identity.
        return asset with { Name = fileName };
    }

    private async Task<ProtocolReply> SharedFileUrlAsync(SharedFile file, CancellationToken cancellationToken)
    {
        _ = await _media.GetReferenceAsync(file.Asset, false, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject { ["download_url"] = _media.GetSharedFileUrl(file.Id, SelfId).AbsoluteUri });
    }

    private static JsonNode EncodeSharedFile(SharedFile file) => new JsonObject
    {
        ["group_id"] = Uin(file.GroupId!),
        ["file_id"] = file.Id,
        ["file_name"] = file.Name,
        ["parent_folder_id"] = file.ParentFolderId,
        ["file_size"] = file.Asset.ByteCount,
        ["uploaded_time"] = file.UploadedAt.ToUnixTimeSeconds(),
        ["expire_time"] = file.ExpiresAt?.ToUnixTimeSeconds(),
        ["uploader_id"] = Uin(file.UploaderId),
        ["downloaded_times"] = file.DownloadedTimes,
    };

    private static JsonNode EncodeSharedFolder(SharedFolder folder) => new JsonObject
    {
        ["group_id"] = Uin(folder.GroupId),
        ["folder_id"] = folder.Id,
        ["parent_folder_id"] = folder.ParentFolderId,
        ["folder_name"] = folder.Name,
        ["created_time"] = folder.CreatedAt.ToUnixTimeSeconds(),
        ["last_modified_time"] = folder.LastModifiedAt.ToUnixTimeSeconds(),
        ["creator_id"] = Uin(folder.CreatorId),
        ["file_count"] = folder.FileCount,
    };

    private static string RequiredFileText(ProtocolCall request, string key) =>
        request.Parameters[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new ArgumentException($"Missing or invalid {key}");

    private static string RequiredFileId(ProtocolCall request, string key) =>
        request.GetLong(key) is > 0 and var id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Missing or invalid {key}");

    private static string FileFolder(ProtocolCall request, string key) =>
        !request.Parameters.ContainsKey(key) ? "/" : RequiredFileText(request, key);

    private static bool FileBoolean(ProtocolCall request, string key, bool defaultValue) =>
        !request.Parameters.ContainsKey(key) ? defaultValue
            : request.Parameters[key] is JsonValue value && value.TryGetValue<bool>(out var flag)
                ? flag : throw new ArgumentException($"Invalid {key}");
}
