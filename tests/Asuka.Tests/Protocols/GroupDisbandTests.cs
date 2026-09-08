using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Sources: Milky /struct/Event#group_disband; OneBot 11 api/public.md and event/notice.md;
// OneBot 12 /interface/group/actions/ and /interface/group/notice-events/.
// OneBot has no disband notice: member-decrease translation below is compatibility policy.
[TestClass]
public sealed class GroupDisbandTests
{
    private const string OutsideBot = "1000000033";
    private const string UnregisteredMember = "1000000044";

    [TestMethod]
    public async Task MilkyDisbandReachesOnlyFormerRegisteredMembersAndEncodesAfterGroupDeletion()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new User("Outside", id: OutsideBot));
        await fixture.Store.SaveAsync(new User("Ordinary member", id: UnregisteredMember));
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, UnregisteredMember));
        await fixture.Platform.AddFriendshipAsync(ProtocolTestFixture.SelfId, OutsideBot);
        fixture.Platform.RegisterBot(ProtocolTestFixture.SenderId);
        fixture.Platform.RegisterBot(OutsideBot);
        fixture.Platform.SetEchoesSelfEvents(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        var denied = await Assert.ThrowsAsync<PlatformException>(() => fixture.Platform.DeleteGroupAsync(
            ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, timeout.Token));
        Assert.AreEqual(PlatformError.NotPermitted, denied.Error);
        Assert.IsNotNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        await fixture.Platform.DeleteGroupAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, timeout.Token);
        Assert.IsNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        Assert.IsEmpty(await fixture.Store.GetMembersAsync(ProtocolTestFixture.GroupId));

        // A later unrelated event is a deterministic queue barrier: no timeout-based
        // assertion is needed to prove that outside accounts received no disband.
        var barrier = await fixture.Platform.SendMessageAsync(ChatScene.Friend, ProtocolTestFixture.SelfId,
            ProtocolTestFixture.SelfId, OutsideBot, [new TextSegment("after disband barrier")], timeout.Token);
        var delivered = new List<DomainEvent>();
        while (await next)
        {
            var current = events.Current;
            if (current.Payload is MessageEvent message && message.Message.Id == barrier.Id) break;
            delivered.Add(current);
            next = events.MoveNextAsync().AsTask();
        }
        Assert.HasCount(2, delivered);
        CollectionAssert.AreEquivalent(new[] { ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId },
            delivered.Select(item => item.SelfId).ToArray());

        var protocols = new[]
        {
            new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media),
            new MilkyProtocol(ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media),
            new MilkyProtocol(OutsideBot, fixture.Platform, fixture.Media),
        };
        foreach (var domainEvent in delivered)
        {
            Assert.IsInstanceOfType<GroupDisbandedEvent>(domainEvent.Payload);
            var detail = (GroupDisbandedEvent)domainEvent.Payload;
            Assert.AreEqual(ProtocolTestFixture.GroupId, detail.GroupId);
            Assert.AreEqual(ProtocolTestFixture.SelfId, detail.OperatorId);
            foreach (var protocol in protocols)
            {
                var frames = await protocol.EncodeAsync(domainEvent, timeout.Token);
                if (protocol.SelfId != domainEvent.SelfId)
                {
                    Assert.IsEmpty(frames);
                    continue;
                }
                Assert.HasCount(1, frames);
                Assert.AreEqual("milky_event", frames[0].EventName);
                var wire = frames[0].Payload;
                Assert.HasCount(4, wire);
                Assert.AreEqual("group_disband", wire["event_type"]!.GetValue<string>());
                Assert.AreEqual(domainEvent.Time.ToUnixTimeSeconds(), wire["time"]!.GetValue<long>());
                Assert.AreEqual(domainEvent.SelfId == ProtocolTestFixture.SelfId ? 1_000_000_001L : 1_000_000_002L,
                    wire["self_id"]!.GetValue<long>());
                Assert.HasCount(2, wire["data"]!.AsObject());
                Assert.AreEqual(500_000_001L, wire["data"]!["group_id"]!.GetValue<long>());
                Assert.AreEqual(1_000_000_001L, wire["data"]!["operator_id"]!.GetValue<long>());
            }
        }
    }

    [TestMethod]
    [DataRow(OneBotVersion.V11, true)]
    [DataRow(OneBotVersion.V11, false)]
    [DataRow(OneBotVersion.V12, true)]
    [DataRow(OneBotVersion.V12, false)]
    public async Task OneBotUsesStandardSelfDepartureNoticesForDisbandWithoutInventingAnEvent(OneBotVersion version, bool ownerView)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.DeleteGroupAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        var selfId = ownerView ? ProtocolTestFixture.SelfId : ProtocolTestFixture.SenderId;
        var protocol = new OneBotProtocol(version, selfId, fixture.Platform, fixture.Media);
        var domainEvent = new DomainEvent(selfId, new GroupDisbandedEvent(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId));
        var frames = await protocol.EncodeAsync(domainEvent);
        Assert.HasCount(1, frames);
        var wire = frames[0].Payload;
        if (version == OneBotVersion.V11)
        {
            Assert.AreEqual("notice", wire["post_type"]!.GetValue<string>());
            Assert.AreEqual("group_decrease", wire["notice_type"]!.GetValue<string>());
            Assert.AreEqual(ownerView ? "leave" : "kick_me", wire["sub_type"]!.GetValue<string>());
            Assert.AreEqual(500_000_001L, wire["group_id"]!.GetValue<long>());
            Assert.AreEqual(1_000_000_001L, wire["operator_id"]!.GetValue<long>());
            Assert.AreEqual(ownerView ? 1_000_000_001L : 1_000_000_002L, wire["user_id"]!.GetValue<long>());
            Assert.AreEqual(wire["self_id"]!.GetValue<long>(), wire["user_id"]!.GetValue<long>());
        }
        else
        {
            Assert.AreEqual("notice", wire["type"]!.GetValue<string>());
            Assert.AreEqual("group_member_decrease", wire["detail_type"]!.GetValue<string>());
            // The standard allows an empty subtype for other departure causes.
            // Administrative-removal compatibility may instead use its standard kick subtype.
            if (ownerView) Assert.AreEqual("leave", wire["sub_type"]!.GetValue<string>());
            else Assert.IsTrue(wire["sub_type"]!.GetValue<string>() is "" or "kick");
            Assert.AreEqual(ProtocolTestFixture.GroupId, wire["group_id"]!.GetValue<string>());
            Assert.AreEqual(ProtocolTestFixture.SelfId, wire["operator_id"]!.GetValue<string>());
            Assert.AreEqual(selfId, wire["user_id"]!.GetValue<string>());
            Assert.AreEqual(selfId, wire["self"]!["user_id"]!.GetValue<string>());
        }
        Assert.IsFalse(wire.ContainsKey("event_type"));
        Assert.IsEmpty(await protocol.EncodeAsync(domainEvent with { SelfId = OutsideBot }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task V11OwnerRequiresAnExplicitTrueDismissFlag(bool provideFalse)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var parameters = new JsonObject { ["group_id"] = 500_000_001L };
        if (provideFalse) parameters["is_dismiss"] = false;
        var denied = await protocol.HandleAsync(new ProtocolCall("set_group_leave", parameters));
        Assert.IsFalse(denied.IsSuccess);
        Assert.IsNotNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        Assert.AreEqual(GroupRole.Owner, (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId))!.Role);
        parameters["is_dismiss"] = true;
        var dismissed = await protocol.HandleAsync(new ProtocolCall("set_group_leave", parameters));
        Assert.IsTrue(dismissed.IsSuccess, dismissed.Message);
        Assert.IsNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        Assert.IsEmpty(await fixture.Store.GetMembersAsync(ProtocolTestFixture.GroupId));
    }

    [TestMethod]
    public async Task V12OwnerLeaveCannotLeaveAnOwnerlessGroupBehind()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var result = await protocol.HandleAsync(new ProtocolCall("leave_group", new JsonObject { ["group_id"] = ProtocolTestFixture.GroupId }));
        var group = await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId);
        var owner = await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        if (!result.IsSuccess)
        {
            Assert.IsNotNull(group);
            Assert.IsNotNull(owner);
            Assert.AreEqual(GroupRole.Owner, owner.Role);
        }
        else
        {
            Assert.IsNull(owner);
            var members = await fixture.Store.GetMembersAsync(ProtocolTestFixture.GroupId);
            if (group is null) Assert.IsEmpty(members);
            else Assert.IsTrue(members.Any(member => member.Role == GroupRole.Owner));
        }
    }

    [TestMethod]
    [DataRow(OneBotVersion.V11)]
    [DataRow(OneBotVersion.V12)]
    public async Task OrdinaryMemberLeavePreservesTheGroupAndOwner(OneBotVersion version)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(version, ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        var result = await protocol.HandleAsync(new ProtocolCall(version == OneBotVersion.V11 ? "set_group_leave" : "leave_group",
            new JsonObject { ["group_id"] = ProtocolTestFixture.GroupId }));
        Assert.IsTrue(result.IsSuccess, result.Message);
        Assert.IsNull(await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId));
        Assert.IsNotNull(await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId));
        Assert.AreEqual(GroupRole.Owner, (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId))!.Role);
    }
}
