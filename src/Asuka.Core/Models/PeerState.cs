namespace Asuka.Core;

/// <summary>Account-specific state for one message scene and peer.</summary>
public sealed record PeerState(Chat Chat, bool IsPinned = false, long LastReadSequence = 0);

/// <summary>Accumulated profile likes sent by one local account to another.</summary>
public sealed record ProfileLikeState(string UserId, string SenderId, long Count, DateTimeOffset LastSentAt);
