using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Optional properties: Milky GroupNotification and Event/group_invitation schemas.
[TestClass]
public sealed class MilkyRequestOptionalFieldTests
{
    [TestMethod]
    [DataRow(RequestKind.GroupJoin)]
    [DataRow(RequestKind.GroupInvitedJoin)]
    public void GroupNotificationsOmitUnknownOperatorsAndPreserveKnownNumericOperators(RequestKind kind)
    {
        var request = new PendingRequest(kind, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId,
            ProtocolTestFixture.GroupId, comment: "hello", targetUserId: "1000000030", notificationSequence: 42);
        var pending = MilkyEntityEncoder.GroupNotification(request);
        Assert.AreEqual("pending", pending["state"]!.GetValue<string>());
        Assert.IsFalse(pending.ContainsKey("operator_id"));
        Assert.HasCount(kind == RequestKind.GroupJoin ? 7 : 6, pending);
        Assert.AreEqual(kind == RequestKind.GroupJoin, pending.ContainsKey("comment"));
        Assert.AreEqual(kind == RequestKind.GroupJoin, pending.ContainsKey("is_filtered"));
        Assert.AreEqual(kind == RequestKind.GroupInvitedJoin, pending.ContainsKey("target_user_id"));

        var legacy = MilkyEntityEncoder.GroupNotification(request with { Resolution = RequestResolution.Accepted });
        Assert.AreEqual("accepted", legacy["state"]!.GetValue<string>());
        Assert.IsFalse(legacy.ContainsKey("operator_id"));
        var resolved = MilkyEntityEncoder.GroupNotification(request with
        {
            Resolution = RequestResolution.Accepted,
            ResolvedBy = ProtocolTestFixture.SelfId,
        });
        Assert.AreEqual(1_000_000_001L, resolved["operator_id"]!.GetValue<long>());
        Assert.AreEqual("accepted", resolved["state"]!.GetValue<string>());
        Assert.HasCount(kind == RequestKind.GroupJoin ? 8 : 7, resolved);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(ProtocolTestFixture.GroupId)]
    public async Task GroupInvitationOmitsAbsentSourceAndPreservesProvidedSourceGroup(string? sourceGroupId)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var request = new PendingRequest(RequestKind.GroupInvite, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, "500000002", sourceGroupId: sourceGroupId, notificationSequence: 42);
        var frames = await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new RequestReceivedEvent(request)));
        Assert.HasCount(1, frames);
        Assert.AreEqual("group_invitation", frames[0].Payload["event_type"]!.GetValue<string>());
        var data = frames[0].Payload["data"]!.AsObject();
        Assert.HasCount(sourceGroupId is null ? 3 : 4, data);
        Assert.AreEqual(500_000_002L, data["group_id"]!.GetValue<long>());
        Assert.AreEqual(42L, data["invitation_seq"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, data["initiator_id"]!.GetValue<long>());
        if (sourceGroupId is null) Assert.IsFalse(data.ContainsKey("source_group_id"));
        else Assert.AreEqual(500_000_001L, data["source_group_id"]!.GetValue<long>());
    }
}
