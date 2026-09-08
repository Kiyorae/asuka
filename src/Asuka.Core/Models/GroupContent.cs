namespace Asuka.Core;

public sealed record GroupAnnouncement(
    string Id, string GroupId, string UserId, DateTimeOffset Time, string Content, Asset? Image = null);

public sealed record GroupEssenceMessage(
    Message Message, string SenderName, string OperatorId, string OperatorName, DateTimeOffset OperationTime);

public sealed record GroupEssencePage(IReadOnlyList<GroupEssenceMessage> Messages, bool IsEnd);
