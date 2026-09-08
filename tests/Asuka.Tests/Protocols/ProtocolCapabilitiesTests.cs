using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ProtocolCapabilitiesTests
{
    [TestMethod]
    public void MaintenanceFollowsOneBot11OfficialActions()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.AreEqual(protocol == ProtocolKind.OneBotV11, capabilities.CacheCleanup);
            Assert.AreEqual(protocol == ProtocolKind.OneBotV11, capabilities.ImplementationRestart);
        }
    }

    [TestMethod]
    public void AnonymousSendingIsOnlyOfferedInOneBot11Groups()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.AreEqual(protocol == ProtocolKind.OneBotV11, capabilities.AnonymousMessages);
            Assert.AreEqual(protocol == ProtocolKind.OneBotV11, capabilities.SupportsSegment(new AnonymousSegment()));
            foreach (var scene in Enum.GetValues<ChatScene>())
            {
                Assert.AreEqual(protocol == ProtocolKind.OneBotV11 && scene == ChatScene.Group,
                    capabilities.SupportsAnonymous(scene), $"{protocol} / {scene}");
            }
        }
    }

    [TestMethod]
    public void NudgesFollowOfficialNoticeScenes()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.AreEqual(protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11, capabilities.GroupNudgeEvents);
            foreach (var scene in Enum.GetValues<ChatScene>())
            {
                var expected = scene == ChatScene.Group && protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11
                    || scene == ChatScene.Friend && protocol == ProtocolKind.Milky;
                Assert.AreEqual(expected, capabilities.SupportsNudges(scene), $"{protocol} / {scene}");
            }
        }
    }

    [TestMethod]
    public void FileUploadNoticesDoNotEnableUnsupportedFileManagementApis()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.AreEqual(protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11, capabilities.GroupFileUploadEvents);
            Assert.AreEqual(protocol == ProtocolKind.Milky, capabilities.SharedFiles);
            foreach (var scene in Enum.GetValues<ChatScene>())
            {
                var expected = scene == ChatScene.Group && protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11
                    || scene == ChatScene.Friend && protocol == ProtocolKind.Milky;
                Assert.AreEqual(expected, capabilities.SupportsSharedFileUploads(scene), $"{protocol} / {scene}");
            }
        }
    }

    [TestMethod]
    public void HonorsAndCredentialsHaveSeparateOfficialProtocolScopes()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            Assert.AreEqual(protocol == ProtocolKind.OneBotV11, capabilities.GroupHonors);
            Assert.AreEqual(protocol is ProtocolKind.Milky or ProtocolKind.OneBotV11, capabilities.AccountCredentials);
        }
    }

    [TestMethod]
    public void ReactionIsMilkyGroupOnly()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var capabilities = ProtocolCapabilities.For(protocol);
            foreach (var scene in Enum.GetValues<ChatScene>())
            {
                Assert.AreEqual(protocol == ProtocolKind.Milky && scene == ChatScene.Group,
                    capabilities.SupportsReactions(scene));
            }
        }
    }

    [TestMethod]
    public void OneBot12DoesNotOfferQqExtensions()
    {
        var capabilities = ProtocolCapabilities.For(ProtocolKind.OneBotV12);
        Assert.IsFalse(capabilities.Nudges);
        Assert.IsFalse(capabilities.CustomFaces);
        Assert.IsFalse(capabilities.Requests);
        Assert.IsFalse(capabilities.MemberModeration);
        Assert.IsFalse(capabilities.SupportsSegment(new FaceSegment("14")));
        Assert.IsFalse(capabilities.SupportsSegment(new ForwardSegment("1", [])));
        Assert.IsTrue(capabilities.SupportsSegment(new ReplySegment("1")));
        Assert.IsTrue(capabilities.SupportsSegment(new AudioSegment(new Asset("1", "music.wav"))));
    }

    [TestMethod]
    public void GenericFileMessageIsNotOneBot11Standard()
    {
        var file = new FileSegment(new Asset("1", "document.txt"));
        Assert.IsFalse(ProtocolCapabilities.For(ProtocolKind.OneBotV11).SupportsSegment(file));
        Assert.IsTrue(ProtocolCapabilities.For(ProtocolKind.OneBotV12).SupportsSegment(file));
        Assert.IsFalse(ProtocolCapabilities.For(ProtocolKind.Milky).SupportsSegment(file));
    }

    [TestMethod]
    public void CustomImageCollectionIsOnlyExposedByMilky()
    {
        Assert.IsTrue(ProtocolCapabilities.For(ProtocolKind.Milky).CustomFaces);
        Assert.IsFalse(ProtocolCapabilities.For(ProtocolKind.OneBotV11).CustomFaces);
        Assert.IsFalse(ProtocolCapabilities.For(ProtocolKind.OneBotV12).CustomFaces);
    }
}
