using Asuka.App;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.App;

[TestClass]
public sealed class GroupManagementPolicyTests
{
    [TestMethod]
    public void AnonymousSettingRequiresOneBot11AndAnAdministratorWithoutASelectedTarget()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.IsFalse(new GroupManagementPolicy(capabilities, null, null).CanSetAnonymous);
            foreach (var role in Enum.GetValues<GroupRole>())
            {
                var actor = Member("actor", role);
                var expected = protocol == ProtocolKind.OneBotV11 && role > GroupRole.Member;
                Assert.AreEqual(expected, new GroupManagementPolicy(capabilities, actor, null).CanSetAnonymous,
                    $"{protocol} / {role}");
                Assert.AreEqual(expected, new GroupManagementPolicy(capabilities, actor, actor).CanSetAnonymous,
                    $"{protocol} / {role} / self selected");
            }
        }
    }

    [TestMethod]
    [DataRow(ProtocolKind.OneBotV11)]
    [DataRow(ProtocolKind.OneBotV12)]
    [DataRow(ProtocolKind.Milky)]
    public void EveryProtocolSupportsAuthorizedRenameAndSelfLeave(ProtocolKind protocol)
    {
        var actor = Member("actor", GroupRole.Admin);
        var policy = new GroupManagementPolicy(ProtocolCapabilities.For(protocol), actor, actor);

        Assert.IsTrue(policy.CanRename);
        Assert.IsTrue(policy.CanRemove);
        Assert.IsFalse(policy.CanMute);
        Assert.IsFalse(policy.CanManageAdmin);
    }

    [TestMethod]
    public void OneBot12HidesQqModerationProfilesAndNudgesEvenForOwner()
    {
        var policy = Policy(ProtocolKind.OneBotV12, GroupRole.Owner, GroupRole.Member);

        Assert.IsFalse(policy.CanSetWholeMute);
        Assert.IsFalse(policy.CanEditCard);
        Assert.IsFalse(policy.CanEditTitle);
        Assert.IsFalse(policy.CanManageAdmin);
        Assert.IsFalse(policy.CanMute);
        Assert.IsFalse(policy.CanRemove);
        Assert.IsFalse(policy.CanNudge);
        Assert.IsFalse(policy.GetProfileChanges("changed card", "changed title").HasChanges);
    }

    [TestMethod]
    [DataRow(ProtocolKind.OneBotV11)]
    [DataRow(ProtocolKind.Milky)]
    public void MemberCanChangeOwnCardWithoutAttemptingOwnerOnlyTitle(ProtocolKind protocol)
    {
        var actor = Member("actor", GroupRole.Member);
        var policy = new GroupManagementPolicy(ProtocolCapabilities.For(protocol), actor, actor);
        var changes = policy.GetProfileChanges("new card", "new title");

        Assert.AreEqual("new card", changes.Card);
        Assert.IsNull(changes.Title);
        Assert.IsTrue(changes.HasChanges);
        Assert.IsTrue(policy.CanRemove);
        Assert.IsFalse(policy.CanRename);
        Assert.IsFalse(policy.CanSetWholeMute);
        Assert.IsFalse(policy.CanManageAdmin);
        Assert.IsFalse(policy.CanMute);
    }

    [TestMethod]
    [DataRow(ProtocolKind.OneBotV11)]
    [DataRow(ProtocolKind.Milky)]
    public void AdminCanModerateLowerRoleButCannotManageAdminOrTitle(ProtocolKind protocol)
    {
        var policy = Policy(protocol, GroupRole.Admin, GroupRole.Member);

        Assert.IsTrue(policy.CanSetWholeMute);
        Assert.IsTrue(policy.CanEditCard);
        Assert.IsTrue(policy.CanMute);
        Assert.IsTrue(policy.CanRemove);
        Assert.IsFalse(policy.CanManageAdmin);
        Assert.IsFalse(policy.CanEditTitle);
        Assert.IsNull(policy.GetProfileChanges("card", "title").Title);
    }

    [TestMethod]
    [DataRow(GroupRole.Admin)]
    [DataRow(GroupRole.Owner)]
    public void AdminCannotEditOrModerateEqualOrHigherRole(GroupRole targetRole)
    {
        var policy = Policy(ProtocolKind.Milky, GroupRole.Admin, targetRole);

        Assert.IsFalse(policy.CanEditCard);
        Assert.IsFalse(policy.CanEditTitle);
        Assert.IsFalse(policy.CanManageAdmin);
        Assert.IsFalse(policy.CanMute);
        Assert.IsFalse(policy.CanRemove);
        Assert.IsFalse(policy.GetProfileChanges("card", "title").HasChanges);
    }

    [TestMethod]
    public void OwnerCanManageAdminButCannotChangeOwnRoleOrMuteSelf()
    {
        var owner = Member("owner", GroupRole.Owner);
        var capabilities = ProtocolCapabilities.For(ProtocolKind.Milky);
        var self = new GroupManagementPolicy(capabilities, owner, owner);
        var admin = new GroupManagementPolicy(capabilities, owner, Member("admin", GroupRole.Admin));

        Assert.IsTrue(self.CanEditCard);
        Assert.IsTrue(self.CanEditTitle);
        Assert.IsFalse(self.CanManageAdmin);
        Assert.IsFalse(self.CanMute);
        Assert.IsTrue(admin.CanManageAdmin);
        Assert.IsTrue(admin.CanMute);
        Assert.IsTrue(admin.CanRemove);
    }

    [TestMethod]
    public void ProfileChangesOmitUnchangedFieldsAndPreserveClearingFields()
    {
        var policy = Policy(ProtocolKind.Milky, GroupRole.Owner, GroupRole.Member);
        Assert.IsFalse(policy.GetProfileChanges("original card", "original title").HasChanges);

        var cardOnly = policy.GetProfileChanges("", "original title");
        Assert.AreEqual("", cardOnly.Card);
        Assert.IsNull(cardOnly.Title);

        var titleOnly = policy.GetProfileChanges("original card", "");
        Assert.IsNull(titleOnly.Card);
        Assert.AreEqual("", titleOnly.Title);
    }

    [TestMethod]
    public void NudgeRequiresOfficialGroupNoticeSupportAndBothParticipantsInRoster()
    {
        var actor = Member("actor", GroupRole.Member);
        var target = Member("target", GroupRole.Owner);
        Assert.IsTrue(new GroupManagementPolicy(ProtocolCapabilities.For(ProtocolKind.Milky), actor, target).CanNudge);
        Assert.IsTrue(new GroupManagementPolicy(ProtocolCapabilities.For(ProtocolKind.OneBotV11), actor, target).CanNudge);
        Assert.IsFalse(new GroupManagementPolicy(ProtocolCapabilities.For(ProtocolKind.OneBotV12), actor, target).CanNudge);
        Assert.IsFalse(new GroupManagementPolicy(ProtocolCapabilities.For(ProtocolKind.Milky), null, target).CanNudge);
        Assert.IsFalse(new GroupManagementPolicy(ProtocolCapabilities.For(ProtocolKind.Milky), actor, null).CanNudge);
    }

    [TestMethod]
    public void MissingActorOrSelectionCannotProduceProfileChanges()
    {
        var capabilities = ProtocolCapabilities.For(ProtocolKind.Milky);
        var missingActor = new GroupManagementPolicy(capabilities, null, Member("target", GroupRole.Member));
        var missingTarget = new GroupManagementPolicy(capabilities, Member("owner", GroupRole.Owner), null);

        Assert.IsFalse(missingActor.CanRename);
        Assert.IsFalse(missingActor.CanRemove);
        Assert.IsFalse(missingActor.GetProfileChanges("card", "title").HasChanges);
        Assert.IsFalse(missingTarget.CanRemove);
        Assert.IsFalse(missingTarget.CanManageAdmin);
        Assert.IsFalse(missingTarget.GetProfileChanges("card", "title").HasChanges);
    }

    private static GroupManagementPolicy Policy(ProtocolKind protocol, GroupRole actorRole, GroupRole targetRole) =>
        new(ProtocolCapabilities.For(protocol), Member("actor", actorRole), Member("target", targetRole));

    private static GroupMember Member(string userId, GroupRole role) =>
        new("group", userId, card: "original card", role: role, title: "original title");
}
