namespace Matcha.Core;

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
        string? recalledBy = null)
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
    public bool IsRecalled => RecalledAt is not null;
    public Chat Chat => new(Scene, PeerId, SelfId);
}

public enum RequestKind
{
    Friend,
    GroupJoin,
    GroupInvite,
}

public enum RequestResolutionStatus
{
    Accepted,
    Rejected,
}

public sealed record RequestResolution(RequestResolutionStatus Status, string Reason = "")
{
    public static RequestResolution Accepted { get; } = new(RequestResolutionStatus.Accepted);
    public static RequestResolution Rejected(string reason = "") => new(RequestResolutionStatus.Rejected, reason);
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
        RequestResolution? resolution = null)
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
}

public sealed record ConversationSummary(Chat Chat, string Title, string? Avatar, Message LastMessage)
{
    public string Id => Chat.Id;
    public string Preview => LastMessage.IsRecalled ? "[Recalled]" : LastMessage.Content.TextPreview();
}

public sealed record ActiveChat(Chat Chat, Message LastMessage);
public sealed record FriendInfo(Friendship Friendship, User User);
public sealed record MemberRosterItem(GroupMember Member, User User);
