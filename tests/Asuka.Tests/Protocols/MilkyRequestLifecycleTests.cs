using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Official contracts: /struct/FriendRequest, /struct/GroupNotification, /struct/Event,
// /api/friend and /api/group at https://milky.ntqqrev.org/.
[TestClass]
public sealed class MilkyRequestLifecycleTests
{
    private const string Applicant = "1000000030";

    [TestMethod]
    public async Task RepeatedUidApprovalOfLegacyDuplicateRequestsDoesNotRepeatFriendAddedEvents()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new User("Sentinel", id: Applicant));
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var first = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var duplicate = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var historical = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId,
            resolution: RequestResolution.Rejected("old rejection"));
        await fixture.Store.SaveAsync(first);
        await fixture.Store.SaveAsync(duplicate);
        await fixture.Store.SaveAsync(historical);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        Assert.IsTrue((await protocol.HandleAsync(Call("accept_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId)))).IsSuccess);
        Assert.IsFalse((await protocol.HandleAsync(Call("accept_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId)))).IsSuccess);
        await fixture.Platform.RequestFriendAsync(Applicant, ProtocolTestFixture.SelfId);

        var additions = 0;
        while (await next)
        {
            if (events.Current.Payload is RequestReceivedEvent request && request.Request.RequesterId == Applicant) break;
            if (events.Current.Payload is FriendAddedEvent) additions++;
            next = events.MoveNextAsync().AsTask();
        }
        Assert.AreEqual(2, additions, "Only one event per participant should be published for the new friendship.");
        var history = await fixture.Store.GetRequestsAsync(ProtocolTestFixture.SelfId, RequestKind.Friend);
        Assert.AreEqual(1, history.Count(request => request.Resolution?.Status == RequestResolutionStatus.Accepted));
        Assert.AreEqual(1, history.Count(request => request.Resolution?.Status == RequestResolutionStatus.Ignored));
        Assert.AreEqual(historical.Resolution, (await fixture.Store.GetRequestAsync(historical.Id))!.Resolution);
    }

    [TestMethod]
    public async Task FriendResolutionUsesAccountAndExactRequestEvenWhenFlagsAreDuplicated()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var own = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, flag: "same-flag");
        var foreign = new PendingRequest(RequestKind.Friend, ProtocolTestFixture.SenderId,
            "1000000099", flag: own.Flag);
        var differentKind = new PendingRequest(RequestKind.GroupJoin, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, ProtocolTestFixture.GroupId, flag: own.Flag);
        await fixture.Store.SaveAsync(own);
        await fixture.Store.SaveAsync(foreign);
        await fixture.Store.SaveAsync(differentKind);
        var result = await protocol.HandleAsync(Call("reject_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId), ("reason", "declined")));
        Assert.IsTrue(result.IsSuccess, result.Message);
        Assert.AreEqual(RequestResolution.Rejected("declined"),
            (await fixture.Store.GetRequestAsync(own.Id))!.Resolution);
        Assert.IsNull((await fixture.Store.GetRequestAsync(foreign.Id))!.Resolution);
        Assert.IsNull((await fixture.Store.GetRequestAsync(differentKind.Id))!.Resolution);
    }

    [TestMethod]
    public async Task FilteredFriendRequestCanBeResolvedAndRemainsInHistoryWithStableUid()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var pending = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, "hello", isFiltered: true, via: "group");
        var frame = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new RequestReceivedEvent(pending))))[0].Payload;
        Assert.AreEqual(ProtocolTestFixture.SenderId, frame["data"]!["initiator_uid"]!.GetValue<string>());
        Assert.AreEqual("group", frame["data"]!["via"]!.GetValue<string>());
        var wrongBucket = await protocol.HandleAsync(Call("accept_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId)));
        Assert.IsFalse(wrongBucket.IsSuccess);
        Assert.IsNull((await fixture.Store.GetRequestAsync(pending.Id))!.Resolution);
        var accepted = await protocol.HandleAsync(Call("accept_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId), ("is_filtered", true)));
        Assert.IsTrue(accepted.IsSuccess, accepted.Message);
        var history = await protocol.HandleAsync(Call("get_friend_requests", ("is_filtered", true)));
        var row = history.Data!["requests"]![0]!;
        Assert.AreEqual("accepted", row["state"]!.GetValue<string>());
        Assert.IsTrue(row["is_filtered"]!.GetValue<bool>());
        Assert.AreEqual("group", row["via"]!.GetValue<string>());
        Assert.IsNotNull(await fixture.Store.GetFriendshipAsync(ProtocolTestFixture.SelfId,
            ProtocolTestFixture.SenderId));
        Assert.IsFalse((await protocol.HandleAsync(Call("accept_friend_request",
            ("initiator_uid", ProtocolTestFixture.SenderId), ("is_filtered", true)))).IsSuccess);
    }

    [TestMethod]
    public async Task InvitedJoinRequestUsesItsOwnEventListShapeAndResolutionKind()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new User("Invitee", id: Applicant));
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var pending = await fixture.Platform.RequestInvitedJoinGroupAsync(ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, Applicant, ProtocolTestFixture.SelfId);
        var frame = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new RequestReceivedEvent(pending))))[0].Payload;
        Assert.AreEqual("group_invited_join_request", frame["event_type"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(Applicant, System.Globalization.CultureInfo.InvariantCulture), frame["data"]!["target_user_id"]!.GetValue<long>());
        var sequence = frame["data"]!["notification_seq"]!.GetValue<long>();
        Assert.IsFalse((await protocol.HandleAsync(GroupAction("accept_group_request", sequence,
            "join_request"))).IsSuccess);
        Assert.IsFalse((await protocol.HandleAsync(Call("accept_group_invitation",
            ("group_id", ProtocolTestFixture.GroupId), ("invitation_seq", sequence)))).IsSuccess);
        fixture.Platform.SetEchoesSelfEvents(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var received = events.MoveNextAsync().AsTask();
        Assert.IsTrue((await protocol.HandleAsync(GroupAction("accept_group_request", sequence,
            "invited_join_request"))).IsSuccess);
        Assert.IsTrue(await received);
        var joined = (await protocol.EncodeAsync(events.Current))[0].Payload;
        Assert.AreEqual("group_member_increase", joined["event_type"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.SelfId, System.Globalization.CultureInfo.InvariantCulture), joined["data"]!["operator_id"]!.GetValue<long>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.SenderId, System.Globalization.CultureInfo.InvariantCulture), joined["data"]!["invitor_id"]!.GetValue<long>());
        Assert.IsNotNull(await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, Applicant));
        var history = await protocol.HandleAsync(Call("get_group_notifications"));
        var row = history.Data!["notifications"]![0]!;
        Assert.AreEqual("invited_join_request", row["type"]!.GetValue<string>());
        Assert.AreEqual("accepted", row["state"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.SelfId, System.Globalization.CultureInfo.InvariantCulture), row["operator_id"]!.GetValue<long>());
        Assert.AreEqual(long.Parse(Applicant, System.Globalization.CultureInfo.InvariantCulture), row["target_user_id"]!.GetValue<long>());
        Assert.IsFalse(row.AsObject().ContainsKey("comment"));
        Assert.IsFalse(row.AsObject().ContainsKey("is_filtered"));
    }

    [TestMethod]
    public async Task BotInvitationPreservesSourceGroupAndIsNotAGroupNotification()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        const string destination = "500000002";
        await fixture.Platform.CreateGroupAsync(new Group("Destination", id: destination),
            ProtocolTestFixture.SenderId);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var pending = await fixture.Platform.InviteToGroupAsync(destination, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId, sourceGroupId: ProtocolTestFixture.GroupId);
        var frame = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
            new RequestReceivedEvent(pending))))[0].Payload;
        Assert.AreEqual("group_invitation", frame["event_type"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.GroupId, System.Globalization.CultureInfo.InvariantCulture),
            frame["data"]!["source_group_id"]!.GetValue<long>());
        var notifications = await protocol.HandleAsync(Call("get_group_notifications"));
        Assert.IsEmpty(notifications.Data!["notifications"]!.AsArray());
        var accepted = await protocol.HandleAsync(Call("accept_group_invitation", ("group_id", destination),
            ("invitation_seq", frame["data"]!["invitation_seq"]!.GetValue<long>())));
        Assert.IsTrue(accepted.IsSuccess, accepted.Message);
        Assert.IsNotNull(await fixture.Store.GetMemberAsync(destination, ProtocolTestFixture.SelfId));
    }

    [TestMethod]
    public async Task FilteredGroupRequestsRemainSeparateAndResolvedPagesDoNotRepeat()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        for (var index = 0; index < 4; index++)
        {
            var userId = (1000000040L + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await fixture.Store.SaveAsync(new User("Applicant", id: userId));
            var pending = await fixture.Platform.RequestJoinGroupAsync(ProtocolTestFixture.GroupId,
                userId, ProtocolTestFixture.SelfId, isFiltered: index == 3);
            if (index == 1)
            {
                await fixture.Platform.IgnoreRequestAsync(pending.Flag);
            }
            else if (index == 2)
            {
                await fixture.Platform.ResolveRequestAsync(pending.Flag, false, "closed");
            }
        }

        var filtered = await protocol.HandleAsync(Call("get_group_notifications", ("is_filtered", true)));
        Assert.HasCount(1, filtered.Data!["notifications"]!.AsArray());
        var filteredRow = filtered.Data!["notifications"]![0]!;
        var filteredSequence = filteredRow["notification_seq"]!.GetValue<long>();
        Assert.IsFalse((await protocol.HandleAsync(GroupAction("reject_group_request", filteredSequence,
            "join_request"))).IsSuccess);
        Assert.IsTrue((await protocol.HandleAsync(GroupAction("reject_group_request", filteredSequence,
            "join_request", true))).IsSuccess);
        var seen = new HashSet<long>();
        var states = new HashSet<string>();
        long? cursor = null;
        do
        {
            var call = Call("get_group_notifications", ("limit", 1));
            if (cursor is { } value) call.Parameters["start_notification_seq"] = value;
            var page = await protocol.HandleAsync(call);
            Assert.IsTrue(page.IsSuccess, page.Message);
            var row = page.Data!["notifications"]![0]!;
            Assert.IsTrue(seen.Add(row["notification_seq"]!.GetValue<long>()));
            states.Add(row["state"]!.GetValue<string>());
            cursor = page.Data["next_notification_seq"]?.GetValue<long>();
        } while (cursor is not null && seen.Count < 10);
        Assert.HasCount(3, seen);
        CollectionAssert.AreEquivalent((string[])["pending", "ignored", "rejected"], states.ToArray());
    }

    [TestMethod]
    public async Task LostModeratorPrivilegesPreventApprovalAndRejectionWithoutResolving()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new User("Applicant", id: Applicant));
        var pending = await fixture.Platform.RequestJoinGroupAsync(ProtocolTestFixture.GroupId,
            Applicant, ProtocolTestFixture.SelfId);
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SelfId, role: GroupRole.Member));
        foreach (var approve in new[] { true, false })
        {
            var exception = await Assert.ThrowsAsync<PlatformException>(() =>
                fixture.Platform.ResolveRequestAsync(pending.Flag, approve));
            Assert.AreEqual(PlatformError.NotPermitted, exception.Error);
            Assert.IsNull((await fixture.Store.GetRequestAsync(pending.Id))!.Resolution);
        }
        Assert.IsNull(await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, Applicant));
    }

    private static ProtocolCall GroupAction(string action, long sequence, string type, bool filtered = false) =>
        Call(action, ("group_id", ProtocolTestFixture.GroupId), ("notification_seq", sequence),
            ("notification_type", type), ("is_filtered", filtered));

    private static ProtocolCall Call(string action, params (string Key, object Value)[] parameters) =>
        new(action, new JsonObject(parameters.Select(pair => KeyValuePair.Create(pair.Key,
            System.Text.Json.JsonSerializer.SerializeToNode(pair.Value)))));
}
