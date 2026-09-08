namespace Asuka.Core;

public enum MessageDirection
{
    Outgoing,
    Incoming,
}

public sealed record Message
{
    public Message(
        ChatScene scene,
        string peerId,
        string senderId,
        string selfId,
        IEnumerable<MessageSegment> content,
        MessageDirection direction,
        string? id = null,
        long seq = 0,
        DateTimeOffset? time = null,
        DateTimeOffset? recalledAt = null,
        string? recalledBy = null,
        AnonymousIdentity? anonymous = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfId);
        Id = id ?? IdGenerator.MessageId();
        Seq = seq;
        Scene = scene;
        PeerId = peerId;
        SenderId = senderId;
        SelfId = selfId;
        Content = content.ToArray();
        Time = time ?? DateTimeOffset.UtcNow;
        Direction = direction;
        RecalledAt = recalledAt;
        RecalledBy = recalledBy;
        Anonymous = anonymous;
    }

    public string Id { get; init; }
    public long Seq { get; init; }
    public ChatScene Scene { get; init; }
    public string PeerId { get; init; }
    public string SenderId { get; init; }
    public string SelfId { get; init; }
    public IReadOnlyList<MessageSegment> Content { get; init; }
    public DateTimeOffset Time { get; init; }
    public MessageDirection Direction { get; init; }
    public DateTimeOffset? RecalledAt { get; init; }
    public string? RecalledBy { get; init; }
    public AnonymousIdentity? Anonymous { get; init; }
    public bool IsRecalled => RecalledAt is not null;
    public Chat Chat => new(Scene, PeerId, SelfId);
}

public enum RequestKind
{
    Friend,
    GroupJoin,
    GroupInvite,
    GroupInvitedJoin,
}

public enum RequestResolutionStatus
{
    Accepted,
    Rejected,
    Ignored,
}

public sealed record RequestResolution(RequestResolutionStatus Status, string Reason = "")
{
    public static RequestResolution Accepted { get; } = new(RequestResolutionStatus.Accepted);
    public static RequestResolution Rejected(string reason = "") => new(RequestResolutionStatus.Rejected, reason);
    public static RequestResolution Ignored { get; } = new(RequestResolutionStatus.Ignored);
}

public sealed record PendingRequest
{
    public PendingRequest(
        RequestKind kind,
        string requesterId,
        string selfId,
        string? groupId = null,
        string comment = "",
        string? id = null,
        string? flag = null,
        DateTimeOffset? time = null,
        RequestResolution? resolution = null,
        string? targetUserId = null,
        string? sourceGroupId = null,
        bool isFiltered = false,
        string via = "asuka",
        string? resolvedBy = null,
        long notificationSequence = 0)
    {
        Id = id ?? IdGenerator.RequestId();
        Flag = flag ?? IdGenerator.Flag();
        Kind = kind;
        RequesterId = requesterId;
        GroupId = groupId;
        SelfId = selfId;
        Comment = comment;
        Time = time ?? DateTimeOffset.UtcNow;
        Resolution = resolution;
        TargetUserId = targetUserId;
        SourceGroupId = sourceGroupId;
        IsFiltered = isFiltered;
        Via = via;
        ResolvedBy = resolvedBy;
        NotificationSequence = notificationSequence;
    }

    public string Id { get; init; }
    public string Flag { get; init; }
    public RequestKind Kind { get; init; }
    public string RequesterId { get; init; }
    public string? GroupId { get; init; }
    public string SelfId { get; init; }
    public string Comment { get; init; }
    public DateTimeOffset Time { get; init; }
    public RequestResolution? Resolution { get; init; }
    public string? TargetUserId { get; init; }
    public string? SourceGroupId { get; init; }
    public bool IsFiltered { get; init; }
    public string Via { get; init; }
    public string? ResolvedBy { get; init; }
    public long NotificationSequence { get; init; }
}

public sealed record ConversationSummary(Chat Chat, string Title, string? Avatar, Message LastMessage)
{
    public string Id => Chat.Id;
    public string Preview => LastMessage.IsRecalled ? "[Recalled]" : LastMessage.Content.TextPreview();
}

public sealed record ActiveChat(Chat Chat, Message LastMessage);
public sealed record MessageReactionState(
    string MessageId,
    string UserId,
    string Reaction,
    string ReactionType = "face");
public sealed record FriendInfo(Friendship Friendship, User User);
public sealed record MemberRosterItem(GroupMember Member, User User);
