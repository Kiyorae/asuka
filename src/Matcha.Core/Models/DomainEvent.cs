namespace Matcha.Core;

public sealed record DomainEvent
{
    public DomainEvent(string selfId, DomainEventPayload payload, string? id = null, DateTimeOffset? time = null)
    {
        SelfId = selfId;
        Payload = payload;
        Id = id ?? IdGenerator.RequestId();
        Time = time ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public string SelfId { get; init; }
    public DomainEventPayload Payload { get; init; }
}

public abstract record DomainEventPayload;

public sealed record MessageEvent(Message Message) : DomainEventPayload;
public sealed record MessageRecalledEvent(MessageRecalled Detail) : DomainEventPayload;
public sealed record GroupMemberAddedEvent(GroupMemberChange Change) : DomainEventPayload;
public sealed record GroupMemberRemovedEvent(GroupMemberChange Change) : DomainEventPayload;
public sealed record GroupAdminChangedEvent(GroupAdminChange Change) : DomainEventPayload;
public sealed record GroupMutedEvent(GroupMute Mute) : DomainEventPayload;
public sealed record GroupNameChangedEvent(string GroupId, string OperatorId, string Name) : DomainEventPayload;
public sealed record FriendAddedEvent(string UserId) : DomainEventPayload;
public sealed record FriendRemovedEvent(string UserId) : DomainEventPayload;
public sealed record RequestReceivedEvent(PendingRequest Request) : DomainEventPayload;
public sealed record PokeEvent(PokeInteraction Poke) : DomainEventPayload;
public sealed record MessageReactionEvent(MessageReaction Reaction) : DomainEventPayload;
public sealed record GroupFileUploadedEvent(GroupFileUpload Upload) : DomainEventPayload;
public sealed record ConnectedEvent : DomainEventPayload;
public sealed record DisconnectedEvent : DomainEventPayload;

public sealed record MessageRecalled(
    string MessageId,
    ChatScene Scene,
    string PeerId,
    string SenderId,
    string OperatorId);

public enum GroupMemberChangeReason
{
    Voluntary,
    Administrative,
    Invited,
}

public sealed record GroupMemberChange(
    string GroupId,
    string UserId,
    string OperatorId,
    GroupMemberChangeReason Reason);

public sealed record GroupAdminChange(
    string GroupId,
    string UserId,
    string OperatorId,
    bool Granted);

public sealed record GroupMute
{
    public GroupMute(string groupId, string? userId, string operatorId, bool muted, TimeSpan? duration = null)
    {
        GroupId = groupId;
        UserId = userId;
        OperatorId = operatorId;
        Muted = muted;
        Duration = duration is { } value && value > TimeSpan.Zero ? value : TimeSpan.Zero;
    }

    public string GroupId { get; init; }
    public string? UserId { get; init; }
    public string OperatorId { get; init; }
    public bool Muted { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record PokeInteraction(
    ChatScene Scene,
    string PeerId,
    string SenderId,
    string TargetId);

public sealed record MessageReaction(
    string MessageId,
    ChatScene Scene,
    string PeerId,
    string UserId,
    string Reaction,
    bool Added);

public sealed record GroupFileUpload(string GroupId, string UserId, Asset Asset);
