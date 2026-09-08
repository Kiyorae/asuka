namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task<SharedFile> ShareGroupFileAsync(
        string groupId, string userId, Asset asset, string parentFolderId = "/",
        CancellationToken cancellationToken = default) =>
        ShareGroupFileAsync(groupId, userId, asset, parentFolderId, null, cancellationToken);

    public Task<SharedFile> ShareGroupFileAsync(
        string groupId, string userId, Asset asset, string parentFolderId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(asset);
            ValidateSharedFileName(asset.Name);
            await ValidateCanSendAsync(ChatScene.Group, groupId, userId, userId, token).ConfigureAwait(false);
            await RequireFileFolderAsync(groupId, parentFolderId, token).ConfigureAwait(false);
            ValidateSharedAsset(asset);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                throw new PlatformException(PlatformError.InvalidParameter, "File expiration must be in the future");
            }

            var file = new SharedFile(Guid.NewGuid().ToString("D"), groupId, null, userId, asset,
                asset.Name, parentFolderId, DateTimeOffset.UtcNow, expiresAt);
            await Store.SaveAsync(asset, token).ConfigureAwait(false);
            await Store.SaveSharedFileAsync(file, token).ConfigureAwait(false);
            await PublishToGroupBotsAsync(groupId,
                new GroupFileUploadedEvent(new GroupFileUpload(groupId, userId, asset, file.Id, file.Name)),
                userId, CancellationToken.None).ConfigureAwait(false);
            return file;
        }, cancellationToken);

    public Task<SharedFile> SharePrivateFileAsync(
        string recipientId, string senderId, Asset asset, string fileHash, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(asset);
            ValidateSharedFileName(asset.Name);
            ValidateSharedAsset(asset);
            if (string.IsNullOrEmpty(fileHash) || fileHash.Length != 40 || !fileHash.All(Uri.IsHexDigit))
            {
                throw new PlatformException(PlatformError.InvalidParameter, "Private files require a TriSHA1 hash");
            }

            await RequireFileFriendAsync(senderId, recipientId, token).ConfigureAwait(false);
            var file = new SharedFile(Guid.NewGuid().ToString("D"), null, recipientId, senderId, asset,
                asset.Name, "/", DateTimeOffset.UtcNow, FileHash: fileHash.ToLowerInvariant());
            await Store.SaveAsync(asset, token).ConfigureAwait(false);
            await Store.SaveSharedFileAsync(file, token).ConfigureAwait(false);
            Publish(new DomainEvent(senderId,
                new FriendFileUploadedEvent(new PrivateFileUpload(recipientId, senderId, file))), senderId);
            Publish(new DomainEvent(recipientId,
                new FriendFileUploadedEvent(new PrivateFileUpload(senderId, senderId, file))), senderId);
            return file;
        }, cancellationToken);

    public Task<GroupFileListing> GetGroupFileListingAsync(
        string groupId, string userId, string parentFolderId = "/", CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await RequireFileMemberAsync(groupId, userId, token).ConfigureAwait(false);
            await RequireFileFolderAsync(groupId, parentFolderId, token).ConfigureAwait(false);
            return new GroupFileListing(
                await Store.GetGroupFilesAsync(groupId, parentFolderId, token).ConfigureAwait(false),
                await Store.GetGroupFoldersAsync(groupId, parentFolderId, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<SharedFile> GetGroupFileForDownloadAsync(
        string groupId, string fileId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await RequireFileMemberAsync(groupId, userId, token).ConfigureAwait(false);
            return await RequireSharedGroupFileAsync(groupId, fileId, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Revalidates an opaque download URL at the moment an authenticated client uses it.</summary>
    public Task<SharedFile> GetSharedFileForDownloadAsync(
        string fileId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await Store.GetSharedFileAsync(fileId, token).ConfigureAwait(false)
                ?? throw SharedFileNotFound(fileId);
            if (file.IsExpired) throw SharedFileNotFound(fileId);
            if (file.GroupId is { } groupId)
            {
                _ = await RequireFileMemberAsync(groupId, userId, token).ConfigureAwait(false);
            }
            else if (file.UploaderId != userId && file.RecipientId != userId)
            {
                throw SharedFileNotFound(fileId);
            }

            _ = await Store.GetUserAsync(userId, token).ConfigureAwait(false) ?? throw UserNotFound(userId);
            return file;
        }, cancellationToken);

    public Task<SharedFile> GetPrivateFileForDownloadAsync(
        string userId, string peerId, string fileId, string fileHash, bool isSelfSend = false,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await Store.GetPrivateFileAsync(fileId, token).ConfigureAwait(false) ?? throw SharedFileNotFound(fileId);
            if (file.IsExpired || file.UploaderId != (isSelfSend ? userId : peerId)
                || file.RecipientId != (isSelfSend ? peerId : userId)
                || !string.Equals(file.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                throw SharedFileNotFound(fileId);
            }

            return file;
        }, cancellationToken);

    /// <summary>Called only after the asset server successfully serves this share's bytes.</summary>
    public Task RecordGroupFileDownloadAsync(
        string groupId, string fileId, string assetId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await Store.GetGroupFileAsync(groupId, fileId, token).ConfigureAwait(false);
            if (file is not null && !file.IsExpired && file.Asset.Id == assetId && file.DownloadedTimes < int.MaxValue)
            {
                await Store.RecordSharedFileDownloadAsync(file.Id, token).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task MoveGroupFileAsync(
        string groupId, string fileId, string userId, string parentFolderId = "/", string targetFolderId = "/",
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await RequireManagedGroupFileAsync(groupId, fileId, userId, token).ConfigureAwait(false);
            RequireFileParent(file, parentFolderId);
            await RequireFileFolderAsync(groupId, targetFolderId, token).ConfigureAwait(false);
            if (file.ParentFolderId != targetFolderId)
            {
                await Store.SaveSharedFileAsync(file with { ParentFolderId = targetFolderId }, token).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task RenameGroupFileAsync(
        string groupId, string fileId, string userId, string newName, string parentFolderId = "/",
        CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ValidateSharedFileName(newName);
            var file = await RequireManagedGroupFileAsync(groupId, fileId, userId, token).ConfigureAwait(false);
            RequireFileParent(file, parentFolderId);
            if (file.Name != newName)
            {
                await Store.SaveSharedFileAsync(file with { Name = newName }, token).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task DeleteGroupFileAsync(
        string groupId, string fileId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await RequireManagedGroupFileAsync(groupId, fileId, userId, token, allowExpired: true).ConfigureAwait(false);
            await Store.DeleteSharedFileAsync(file, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task PersistGroupFileAsync(
        string groupId, string fileId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var file = await RequireManagedGroupFileAsync(groupId, fileId, userId, token).ConfigureAwait(false);
            if (file.ExpiresAt is not null)
            {
                await Store.SaveSharedFileAsync(file with { ExpiresAt = null }, token).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task<SharedFolder> CreateGroupFolderAsync(
        string groupId, string userId, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ValidateSharedFileName(name);
            await RequireFileAdministratorAsync(groupId, userId, token).ConfigureAwait(false);
            await RequireUniqueFolderNameAsync(groupId, name, null, token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var folder = new SharedFolder(Guid.NewGuid().ToString("D"), groupId, "/", name, userId, now, now);
            await Store.SaveSharedFolderAsync(folder, token).ConfigureAwait(false);
            return folder;
        }, cancellationToken);

    public Task RenameGroupFolderAsync(
        string groupId, string folderId, string userId, string newName, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            ValidateSharedFileName(newName);
            await RequireFileAdministratorAsync(groupId, userId, token).ConfigureAwait(false);
            var folder = await Store.GetGroupFolderAsync(groupId, folderId, token).ConfigureAwait(false)
                ?? throw SharedFolderNotFound(folderId);
            await RequireUniqueFolderNameAsync(groupId, newName, folderId, token).ConfigureAwait(false);
            if (folder.Name != newName)
            {
                await Store.SaveSharedFolderAsync(folder with { Name = newName, LastModifiedAt = DateTimeOffset.UtcNow }, token)
                    .ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task DeleteGroupFolderAsync(
        string groupId, string folderId, string userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            await RequireFileAdministratorAsync(groupId, userId, token).ConfigureAwait(false);
            var folder = await Store.GetGroupFolderAsync(groupId, folderId, token).ConfigureAwait(false)
                ?? throw SharedFolderNotFound(folderId);
            await Store.DeleteSharedFolderAsync(folder, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<GroupMember> RequireFileMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        _ = await Store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
        return await Store.GetMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false) ?? throw NotAMember(groupId, userId);
    }

    private async Task RequireFileAdministratorAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        if ((await RequireFileMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false)).Role == GroupRole.Member)
        {
            throw NotPermitted("Administrator privileges are required to manage group folders");
        }
    }

    private async Task RequireFileFriendAsync(string userId, string peerId, CancellationToken cancellationToken)
    {
        _ = await Store.GetUserAsync(userId, cancellationToken).ConfigureAwait(false) ?? throw UserNotFound(userId);
        _ = await Store.GetUserAsync(peerId, cancellationToken).ConfigureAwait(false) ?? throw UserNotFound(peerId);
        if (userId == peerId || await Store.GetFriendshipAsync(userId, peerId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw NotPermitted("Private file transfers require a friend");
        }
    }

    private async Task RequireFileFolderAsync(string groupId, string folderId, CancellationToken cancellationToken)
    {
        if (folderId != "/" && await Store.GetGroupFolderAsync(groupId, folderId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw SharedFolderNotFound(folderId);
        }
    }

    private async Task RequireUniqueFolderNameAsync(string groupId, string name, string? excludingId, CancellationToken cancellationToken)
    {
        if ((await Store.GetGroupFoldersAsync(groupId, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Any(folder => folder.Name == name && folder.Id != excludingId))
        {
            throw AlreadyExists($"A folder named {name} already exists");
        }
    }

    private async Task<SharedFile> RequireSharedGroupFileAsync(
        string groupId, string fileId, CancellationToken cancellationToken, bool allowExpired = false)
    {
        var file = await Store.GetGroupFileAsync(groupId, fileId, cancellationToken).ConfigureAwait(false)
            ?? throw SharedFileNotFound(fileId);
        return !allowExpired && file.IsExpired ? throw SharedFileNotFound(fileId) : file;
    }

    private async Task<SharedFile> RequireManagedGroupFileAsync(
        string groupId, string fileId, string userId, CancellationToken cancellationToken, bool allowExpired = false)
    {
        var member = await RequireFileMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false);
        var file = await RequireSharedGroupFileAsync(groupId, fileId, cancellationToken, allowExpired).ConfigureAwait(false);
        if (file.UploaderId != userId && member.Role == GroupRole.Member)
        {
            throw NotPermitted("Only the uploader or a group administrator can manage this file");
        }

        return file;
    }

    private static void RequireFileParent(SharedFile file, string parentFolderId)
    {
        if (file.ParentFolderId != parentFolderId) throw SharedFileNotFound(file.Id);
    }

    private static void ValidateSharedAsset(Asset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Id) || asset.ByteCount < 0)
            throw new PlatformException(PlatformError.InvalidParameter, "Invalid file asset");
    }

    private static void ValidateSharedFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".."
            || name.Any(character => char.IsControl(character) || character is '/' or '\\'))
            throw new PlatformException(PlatformError.InvalidParameter, "A file or folder name must be a nonempty name of at most 255 characters without path separators or control characters");
    }

    private static PlatformException SharedFileNotFound(string id) => new(PlatformError.FileNotFound, $"File not found: {id}") { ResourceId = id };
    private static PlatformException SharedFolderNotFound(string id) => new(PlatformError.FolderNotFound, $"Folder not found: {id}") { ResourceId = id };
}
