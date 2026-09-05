namespace Matcha.Core;

public enum Sex
{
    Male,
    Female,
    Unknown,
}

public enum GroupRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

public enum ChatScene
{
    Friend,
    Group,
    Temp,
}

public static class ChatSceneExtensions
{
    public static bool IsPrivate(this ChatScene scene) => scene != ChatScene.Group;
}

public sealed record User
{
    public User(
        string name,
        string? id = null,
        string? nickname = null,
        string? avatar = null,
        Sex sex = Sex.Unknown,
        int? age = null,
        string sign = "",
        DateTimeOffset? createdAt = null)
    {
        Id = id ?? IdGenerator.UserId();
        Name = name;
        Nickname = string.IsNullOrEmpty(nickname) ? name : nickname;
        Avatar = avatar;
        Sex = sex;
        Age = age;
        Sign = sign;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public string Nickname { get; init; }
    public string? Avatar { get; init; }
    public Sex Sex { get; init; }
    public int? Age { get; init; }
    public string Sign { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string DisplayName => string.IsNullOrEmpty(Nickname) ? Name : Nickname;
}

public sealed record Group
{
    public Group(
        string name,
        string? id = null,
        string? avatar = null,
        string intro = "",
        int level = 1,
        int maxMemberCount = 200,
        bool wholeMuted = false,
        DateTimeOffset? createdAt = null)
    {
        Id = id ?? IdGenerator.GroupId();
        Name = name;
        Avatar = avatar;
        Intro = intro;
        Level = level;
        MaxMemberCount = maxMemberCount;
        WholeMuted = wholeMuted;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public string? Avatar { get; init; }
    public string Intro { get; init; }
    public int Level { get; init; }
    public int MaxMemberCount { get; init; }
    public bool WholeMuted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record GroupMember
{
    public GroupMember(
        string groupId,
        string userId,
        string card = "",
        GroupRole role = GroupRole.Member,
        string title = "",
        DateTimeOffset? joinedAt = null,
        DateTimeOffset? lastSentAt = null,
        DateTimeOffset? mutedUntil = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        GroupId = groupId;
        UserId = userId;
        Card = card;
        Role = role;
        Title = title;
        JoinedAt = joinedAt ?? DateTimeOffset.UtcNow;
        LastSentAt = lastSentAt;
        MutedUntil = mutedUntil;
    }

    public string Id => $"{GroupId}:{UserId}";
    public string GroupId { get; init; }
    public string UserId { get; init; }
    public string Card { get; init; }
    public GroupRole Role { get; init; }
    public string Title { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
    public DateTimeOffset? LastSentAt { get; init; }
    public DateTimeOffset? MutedUntil { get; init; }
    public bool IsMuted => MutedUntil is { } until && until > DateTimeOffset.UtcNow;
}

public sealed record Friendship
{
    public Friendship(string userId, string friendId, string remark = "", DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendId);
        UserId = userId;
        FriendId = friendId;
        Remark = remark;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    public string Id => $"{UserId}:{FriendId}";
    public string UserId { get; init; }
    public string FriendId { get; init; }
    public string Remark { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record Chat(ChatScene Scene, string PeerId, string SelfId)
{
    public string Id => $"{Scene.ToStorageValue()}:{SelfId}:{PeerId}";

    public string? CounterpartId(string participantId)
    {
        if (!Scene.IsPrivate() || SelfId == PeerId)
        {
            return null;
        }

        return participantId == SelfId ? PeerId : participantId == PeerId ? SelfId : null;
    }
}

internal static class EnumStorage
{
    public static string ToStorageValue(this Sex value) => value.ToString().ToLowerInvariant();
    public static string ToStorageValue(this GroupRole value) => value.ToString().ToLowerInvariant();
    public static string ToStorageValue(this ChatScene value) => value.ToString().ToLowerInvariant();
    public static string ToStorageValue(this MessageDirection value) => value.ToString().ToLowerInvariant();
    public static string ToStorageValue(this RequestKind value) => value switch
    {
        RequestKind.Friend => "friend",
        RequestKind.GroupJoin => "groupJoin",
        RequestKind.GroupInvite => "groupInvite",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static T ParseStorageValue<T>(string value)
        where T : struct, Enum => Enum.TryParse<T>(value, true, out var result)
            ? result
            : throw new StorePersistenceException($"Invalid {typeof(T).Name} value in the database: {value}");
}
