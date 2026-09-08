namespace Asuka.Core;

public enum GroupNotificationKind
{
    JoinRequest,
    InvitedJoinRequest,
    AdminChange,
    Kick,
    Quit,
}

/// <summary>Time is local history metadata; Milky's group-notification wire entities do not contain a time field.</summary>
public sealed record GroupNotificationEntry(
    long NotificationSequence,
    string SelfId,
    GroupNotificationKind Kind,
    string GroupId,
    string TargetUserId,
    string? OperatorId,
    bool? IsSet,
    DateTimeOffset Time,
    PendingRequest? Request = null)
{
    public bool IsFiltered => Request?.IsFiltered ?? false;
}

public sealed record GroupNotificationPage(IReadOnlyList<GroupNotificationEntry> Notifications, long? NextSequence);
