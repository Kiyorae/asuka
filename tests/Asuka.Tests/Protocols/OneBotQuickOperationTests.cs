using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotQuickOperationTests
{
    // Contracts: onebot-11/communication/http-post.md and event/{message,request}.md.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrivateReplyDistinguishesCqContentFromAutoEscapedText(bool autoEscape)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Friend);
        const string reply = "[CQ:face,id=14] hello &#91;world&#93;";
        var result = await protocol.HandleQuickOperationAsync(original, new JsonObject { ["reply"] = reply, ["auto_escape"] = autoEscape });
        Assert.HasCount(1, result);
        Assert.AreEqual("send_private_msg", result[0].Action);
        Assert.AreEqual(0, result[0].RetCode);
        var sent = (await fixture.Store.GetMessagesAsync(message.Chat)).Single(item => item.SenderId == ProtocolTestFixture.SelfId);
        if (autoEscape)
        {
            Assert.HasCount(1, sent.Content);
            Assert.AreEqual(reply, ((TextSegment)sent.Content[0]).Text);
        }
        else
        {
            Assert.AreEqual("14", ((FaceSegment)sent.Content[0]).Id);
            Assert.AreEqual(" hello [world]", ((TextSegment)sent.Content[1]).Text);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GroupReplyAcceptsSingleSegmentAndDefaultsToMentioningSender(bool disableMention)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        var operation = new JsonObject { ["reply"] = new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "hello" } } };
        if (disableMention) operation["at_sender"] = false;
        Assert.AreEqual(0, (await protocol.HandleQuickOperationAsync(original, operation)).Single().RetCode);
        var sent = (await fixture.Store.GetMessagesAsync(message.Chat)).Single(item => item.SenderId == ProtocolTestFixture.SelfId);
        if (disableMention)
        {
            Assert.IsFalse(sent.Content.Any(item => item is MentionSegment));
            Assert.AreEqual("hello", sent.Content.TextPreview());
        }
        else
        {
            Assert.AreEqual(ProtocolTestFixture.SenderId, ((MentionSegment)sent.Content[0]).UserId);
            Assert.Contains("hello", sent.Content.TextPreview());
        }
    }

    [TestMethod]
    public async Task ReplyInATemporaryPrivateConversationRetainsItsScene()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Temp);
        var result = await protocol.HandleQuickOperationAsync(original, new JsonObject
        {
            ["reply"] = new JsonArray(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "temporary reply" } }),
        });
        Assert.AreEqual(0, result.Single().RetCode);
        Assert.HasCount(2, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task GroupAutoEscapeRetainsItsMentionWithoutParsingReplyCqCodes()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        const string literal = "[CQ:face,id=14]";
        Assert.AreEqual(0, (await protocol.HandleQuickOperationAsync(original, new JsonObject
        {
            ["reply"] = literal,
            ["auto_escape"] = true,
        })).Single().RetCode);
        var sent = (await fixture.Store.GetMessagesAsync(message.Chat)).Single(item => item.SenderId == ProtocolTestFixture.SelfId);
        Assert.AreEqual(ProtocolTestFixture.SenderId, ((MentionSegment)sent.Content[0]).UserId);
        Assert.IsFalse(sent.Content.Any(item => item is FaceSegment));
        Assert.Contains(literal, string.Concat(sent.Content.OfType<TextSegment>().Select(item => item.Text)));
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(12.5)]
    public async Task ExplicitBanDurationSupportsZeroAndFiniteNumericSeconds(double seconds)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (_, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        await fixture.Platform.MuteMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, 60);
        var before = DateTimeOffset.UtcNow;
        Assert.AreEqual(0, (await protocol.HandleQuickOperationAsync(original, new JsonObject
        {
            ["ban"] = true,
            ["ban_duration"] = seconds,
        })).Single().RetCode);
        var until = (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!.MutedUntil;
        if (seconds == 0) Assert.IsNull(until);
        else
        {
            Assert.IsNotNull(until);
            Assert.IsTrue(until >= before.AddSeconds(seconds));
            Assert.IsTrue(until <= DateTimeOffset.UtcNow.AddSeconds(seconds));
        }
    }

    [TestMethod]
    public async Task DeleteAndBanUseOriginalMessageAndDefaultDurationAndKickAllowsAnotherJoinRequest()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        var before = DateTimeOffset.UtcNow;
        var result = await protocol.HandleQuickOperationAsync(original, new JsonObject { ["delete"] = true, ["ban"] = true });
        Assert.HasCount(2, result);
        Assert.IsTrue(result.All(item => item.RetCode == 0));
        Assert.IsTrue((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        var member = (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!;
        Assert.IsNotNull(member.MutedUntil);
        Assert.IsTrue(member.MutedUntil >= before.AddSeconds(1800));
        Assert.IsTrue(member.MutedUntil <= DateTimeOffset.UtcNow.AddSeconds(1800));
        Assert.AreEqual(0, (await protocol.HandleQuickOperationAsync(original, new JsonObject { ["kick"] = true })).Single().RetCode);
        Assert.IsNull(await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId));
        var retry = await fixture.Platform.RequestJoinGroupAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        Assert.IsNull(retry.Resolution);
    }

    [TestMethod]
    public async Task FalseFlagsAndStandaloneOptionsDoNotTriggerActions()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        Assert.IsEmpty(await protocol.HandleQuickOperationAsync(original, new JsonObject
        {
            ["delete"] = false,
            ["kick"] = false,
            ["ban"] = false,
            ["ban_duration"] = 60,
            ["at_sender"] = false,
            ["auto_escape"] = true,
        }));
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        Assert.IsNull((await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!.MutedUntil);
        var request = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var requestEvent = await RequestEventAsync(protocol, request);
        Assert.IsEmpty(await protocol.HandleQuickOperationAsync(requestEvent, new JsonObject { ["remark"] = "unused" }));
        Assert.IsNull((await fixture.Store.GetRequestAsync(request.Id))!.Resolution);
    }

    [TestMethod]
    public async Task InvalidKnownFieldTypesRejectTheWholeOperationBeforeMutation()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        foreach (var invalid in new JsonObject[]
        {
            new() { ["ban"] = "false" }, new() { ["delete"] = 1 }, new() { ["kick"] = "true" },
            new() { ["auto_escape"] = "true" }, new() { ["at_sender"] = 0 },
            new() { ["ban_duration"] = "60" }, new() { ["ban_duration"] = -1 },
            new() { ["ban_duration"] = long.MaxValue }, new() { ["approve"] = "false" },
            new() { ["remark"] = 17 }, new() { ["reply"] = null }, new() { ["reply"] = 17 },
        })
        {
            if (!invalid.ContainsKey("reply")) invalid["reply"] = "must not be sent";
            var results = await protocol.HandleQuickOperationAsync(original, invalid);
            Assert.AreEqual(1400, results.Single().RetCode, invalid.ToJsonString());
        }
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
    }

    [TestMethod]
    public async Task ForgedContextAndInventedAnonymousIdentityCannotActOnStoredMessage()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        foreach (var field in new[] { "self_id", "group_id", "user_id", "message_id" })
        {
            var forged = (JsonObject)original.DeepClone();
            forged[field] = 777_000_001L;
            var results = await protocol.HandleQuickOperationAsync(forged, new JsonObject { ["reply"] = "unsafe", ["delete"] = true });
            Assert.IsTrue(results.All(item => item.RetCode != 0), field);
        }
        var anonymous = (JsonObject)original.DeepClone();
        anonymous["sub_type"] = "anonymous";
        anonymous["anonymous"] = new JsonObject { ["id"] = 1, ["name"] = "anonymous", ["flag"] = "unsupported" };
        Assert.AreEqual(1400, (await protocol.HandleQuickOperationAsync(anonymous, new JsonObject { ["ban"] = true })).Single().RetCode);
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        Assert.IsNull((await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!.MutedUntil);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FriendApproveFalseRejectsAndTrueStoresRemark(bool approve)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var request = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var result = await protocol.HandleQuickOperationAsync(await RequestEventAsync(protocol, request), new JsonObject
        {
            ["approve"] = approve,
            ["remark"] = "Chosen remark",
        });
        Assert.AreEqual("set_friend_add_request", result.Single().Action);
        Assert.AreEqual(0, result.Single().RetCode);
        Assert.AreEqual(approve ? RequestResolutionStatus.Accepted : RequestResolutionStatus.Rejected,
            (await fixture.Store.GetRequestAsync(request.Id))!.Resolution!.Status);
        var friend = await fixture.Store.GetFriendshipAsync(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
        if (approve) Assert.AreEqual("Chosen remark", friend!.Remark);
        else Assert.IsNull(friend);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task GroupJoinAndInvitationRespectApproveFalseAndRejectionReason(bool invitation, bool approve)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        const string applicant = "1000000003";
        await fixture.Store.SaveAsync(new User("Applicant", id: applicant));
        PendingRequest request;
        if (invitation)
        {
            // The invitee must first leave as an ordinary member. Keep a valid
            // owner throughout fixture setup rather than bypassing the owner rule.
            var inviter = (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!;
            var invitee = (await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId))!;
            await fixture.Store.SaveAsync(inviter with { Role = GroupRole.Owner });
            await fixture.Store.SaveAsync(invitee with { Role = GroupRole.Member });
            await fixture.Platform.RemoveMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId);
            request = await fixture.Platform.InviteToGroupAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        }
        else
        {
            request = await fixture.Platform.RequestJoinGroupAsync(ProtocolTestFixture.GroupId, applicant, ProtocolTestFixture.SelfId);
        }
        var result = await protocol.HandleQuickOperationAsync(await RequestEventAsync(protocol, request), new JsonObject
        {
            ["approve"] = approve,
            ["reason"] = "Not now",
        });
        Assert.AreEqual("set_group_add_request", result.Single().Action);
        Assert.AreEqual(0, result.Single().RetCode);
        var resolution = (await fixture.Store.GetRequestAsync(request.Id))!.Resolution!;
        Assert.AreEqual(approve ? RequestResolutionStatus.Accepted : RequestResolutionStatus.Rejected, resolution.Status);
        Assert.AreEqual(approve ? string.Empty : "Not now", resolution.Reason);
        Assert.AreEqual(approve, await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, invitation ? ProtocolTestFixture.SelfId : applicant) is not null);
    }

    [TestMethod]
    public async Task CurrentAuthorityAndRequestOwnerAreRevalidated()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, original) = await MessageAsync(fixture, protocol, ChatScene.Group);
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, role: GroupRole.Member));
        var result = await protocol.HandleQuickOperationAsync(original, new JsonObject { ["delete"] = true, ["ban"] = true, ["kick"] = true });
        Assert.HasCount(3, result);
        Assert.IsTrue(result.All(item => item.RetCode == 1403));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        var pending = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var wrong = await RequestEventAsync(protocol, pending);
        wrong["user_id"] = 555_000_001L;
        Assert.IsTrue((await protocol.HandleQuickOperationAsync(wrong, new JsonObject { ["approve"] = false })).All(item => item.RetCode != 0));
        Assert.IsNull((await fixture.Store.GetRequestAsync(pending.Id))!.Resolution);
    }

    [TestMethod]
    public async Task CancellationAndV12NeverExecuteQuickOperations()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var (message, original) = await MessageAsync(fixture, v11, ChatScene.Group);
        var operation = new JsonObject { ["delete"] = true };
        Assert.AreEqual(10002, (await v12.HandleQuickOperationAsync(original, operation)).Single().RetCode);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => v11.HandleQuickOperationAsync(original, operation, cancellation.Token));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OfflineQuickRepliesAndRequestDecisionsAreRejectedWithoutChangingState(bool approve)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, messageEvent) = await MessageAsync(fixture, protocol, ChatScene.Group);
        var request = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var requestEvent = await RequestEventAsync(protocol, request);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false, "Local logout");
        Assert.AreEqual(1403, (await protocol.HandleQuickOperationAsync(messageEvent, new JsonObject { ["reply"] = "offline reply" })).Single().RetCode);
        Assert.AreEqual(1403, (await protocol.HandleQuickOperationAsync(requestEvent, new JsonObject { ["approve"] = approve })).Single().RetCode);
        Assert.IsEmpty(await protocol.HandleQuickOperationAsync(messageEvent, new JsonObject { ["ban"] = false }));
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsNull((await fixture.Store.GetRequestAsync(request.Id))!.Resolution);
        // A completed protocol operation must not leave its actor scope on native edits.
        await fixture.Platform.SetGroupNameAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, "Native offline edit");
        Assert.AreEqual("Native offline edit", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LogoutQueuedBeforeQuickMutationIsCheckedInsideThePlatformGate(bool requestDecision)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (_, messageEvent) = await MessageAsync(fixture, protocol, ChatScene.Group);
        var request = await fixture.Platform.RequestFriendAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
        var requestEvent = await RequestEventAsync(protocol, request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        void HoldPlatformGate(object? sender, StoreChangedEventArgs change)
        {
            if (Interlocked.Exchange(ref observed, 1) != 0) return;
            entered.TrySetResult();
            release.Task.Wait(timeout.Token);
        }
        fixture.Store.Changed += HoldPlatformGate;
        var blocker = Task.Run(() => fixture.Platform.SaveUserAsync(new User("Gate holder", id: "900"), timeout.Token));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var logout = fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false, cancellationToken: timeout.Token);
            var quick = protocol.HandleQuickOperationAsync(requestDecision ? requestEvent : messageEvent,
                requestDecision ? new JsonObject { ["approve"] = false } : new JsonObject { ["ban"] = true }, timeout.Token);
            Assert.IsFalse(logout.IsCompleted);
            Assert.IsFalse(quick.IsCompleted);
            Assert.IsTrue(fixture.Platform.IsBotOnline(ProtocolTestFixture.SelfId));
            release.TrySetResult();
            await blocker.WaitAsync(timeout.Token);
            await logout.WaitAsync(timeout.Token);
            Assert.AreEqual(1403, (await quick.WaitAsync(timeout.Token)).Single().RetCode);
            Assert.IsNull((await fixture.Store.GetRequestAsync(request.Id, timeout.Token))!.Resolution);
            Assert.IsNull((await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, timeout.Token))!.MutedUntil);
        }
        finally
        {
            release.TrySetResult();
            fixture.Store.Changed -= HoldPlatformGate;
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture) =>
        new(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static async Task<(Message Message, JsonObject Event)> MessageAsync(ProtocolTestFixture fixture, OneBotProtocol protocol, ChatScene scene)
    {
        var message = await fixture.Platform.SendMessageAsync(scene, scene == ChatScene.Group ? ProtocolTestFixture.GroupId : ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("incoming")]);
        var frame = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)))).Single();
        return (message, frame.Payload);
    }

    private static async Task<JsonObject> RequestEventAsync(OneBotProtocol protocol, PendingRequest request) =>
        (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new RequestReceivedEvent(request)))).Single().Payload;
}
