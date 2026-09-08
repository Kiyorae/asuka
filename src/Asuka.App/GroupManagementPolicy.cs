using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.App;

public sealed record GroupManagementPolicy(
    ProtocolCapabilities Capabilities,
    GroupMember? Actor,
    GroupMember? Target)
{
    public bool IsSelf => Actor is not null && Target?.UserId == Actor.UserId;
    private bool HasAuthority => Actor is not null && Target is not null
        && Actor.Role > GroupRole.Member && Actor.Role > Target.Role;

    public bool CanRename => Actor is { Role: > GroupRole.Member };
    public bool CanSetWholeMute => Capabilities.MemberModeration && CanRename;
    public bool CanSetAnonymous => Capabilities.AnonymousMessages && CanRename;
    public bool CanEditCard => Capabilities.MemberProfiles && (IsSelf || HasAuthority);
    public bool CanEditTitle => Capabilities.MemberProfiles && Actor?.Role == GroupRole.Owner && Target is not null;
    public bool CanManageAdmin => Capabilities.MemberModeration && Actor?.Role == GroupRole.Owner
        && Target is not null && Target.Role != GroupRole.Owner;
    public bool CanMute => Capabilities.MemberModeration && HasAuthority;
    public bool CanRemove => IsSelf || Capabilities.MemberModeration && HasAuthority;
    public bool CanNudge => Capabilities.SupportsNudges(ChatScene.Group) && Actor is not null && Target is not null;

    public GroupProfileChanges GetProfileChanges(string card, string title) => new(
        CanEditCard && Target!.Card != card ? card : null,
        CanEditTitle && Target!.Title != title ? title : null);
}

public sealed record GroupProfileChanges(string? Card, string? Title)
{
    public bool HasChanges => Card is not null || Title is not null;
}
