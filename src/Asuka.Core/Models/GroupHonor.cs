namespace Asuka.Core;

public enum GroupHonorType
{
    Talkative = 0,
    Performer = 1,
    Legend = 2,
    StrongNewbie = 3,
    Emotion = 4,
}

public sealed record GroupHonorEntry(string UserId, string Nickname, string Avatar, string Description);
public sealed record GroupTalkative(string UserId, string Nickname, string Avatar, int DayCount);
public sealed record GroupHonorInfo(
    string GroupId,
    GroupTalkative? CurrentTalkative,
    IReadOnlyList<GroupHonorEntry> TalkativeList,
    IReadOnlyList<GroupHonorEntry> PerformerList,
    IReadOnlyList<GroupHonorEntry> LegendList,
    IReadOnlyList<GroupHonorEntry> StrongNewbieList,
    IReadOnlyList<GroupHonorEntry> EmotionList);

public sealed record GroupHonorChange(string GroupId, string UserId, GroupHonorType Type);
public sealed record GroupLuckyKing(string GroupId, string SenderId, string TargetId);
