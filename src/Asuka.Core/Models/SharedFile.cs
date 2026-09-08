namespace Asuka.Core;

/// <summary>A share has its own identity and name even when its asset bytes are deduplicated.</summary>
public sealed record SharedFile(
    string Id,
    string? GroupId,
    string? RecipientId,
    string UploaderId,
    Asset Asset,
    string Name,
    string ParentFolderId,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ExpiresAt = null,
    int DownloadedTimes = 0,
    string FileHash = "")
{
    public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;
}

public sealed record SharedFolder(
    string Id,
    string GroupId,
    string ParentFolderId,
    string Name,
    string CreatorId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    int FileCount = 0);

public sealed record GroupFileListing(IReadOnlyList<SharedFile> Files, IReadOnlyList<SharedFolder> Folders);
