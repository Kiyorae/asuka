namespace Asuka.Core;

/// <summary>Runtime login state for one registered bot; no user or message data is removed by logout.</summary>
public sealed record BotPresence(string SelfId, bool IsOnline, string Reason, DateTimeOffset ChangedAt);
