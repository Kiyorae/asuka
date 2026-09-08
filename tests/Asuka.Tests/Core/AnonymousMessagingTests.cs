using System.Globalization;
using System.Text.Json;
using Asuka.Core;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AnonymousMessagingTests
{
    private const string Owner = "10001";
    private const string Admin = "10002";
    private const string Member = "10003";
    private const string Bot = "10004";
    private const string Outsider = "10005";
    private const string GroupId = "50001";
    private const string OtherGroupId = "50002";

    [TestMethod]
    public async Task GroupsDefaultToDisabledAndOrdinaryMessagesKeepTheirSenderAndContent()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        Assert.IsFalse(new Group("New group").AnonymousEnabled);
        Assert.IsFalse((await store.GetGroupAsync(GroupId))!.AnonymousEnabled);

        var ordinary = await SendOrdinaryAsync(platform);

        Assert.IsNull(ordinary.Anonymous);
        Assert.AreEqual(Member, ordinary.SenderId);
        Assert.AreEqual("ordinary", ((TextSegment)ordinary.Content.Single()).Text);
        Assert.IsNull((await store.GetMessageAsync(ordinary.Id))!.Anonymous);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        Assert.IsTrue((await store.GetGroupAsync(GroupId))!.AnonymousEnabled);
        Assert.IsNull((await SendOrdinaryAsync(platform)).Anonymous);
    }

    [TestMethod]
    public async Task DisabledModeRejectsStrictSendsAndOnlyExplicitIgnoreFallsBack()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);

        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
        Assert.IsEmpty(await store.GetMessagesAsync(Chat));
        var fallback = await SendAnonymousAsync(platform, ignore: true);
        Assert.IsNull(fallback.Anonymous);
        Assert.AreEqual(Member, fallback.SenderId);
        AssertOnlyText(fallback);

        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var enabled = await SendAnonymousAsync(platform, ignore: true);
        Assert.IsNotNull(enabled.Anonymous);
        await platform.SetGroupAnonymousAsync(GroupId, Owner, false);
        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
        Assert.AreEqual(enabled.Anonymous, (await store.GetMessageAsync(enabled.Id))!.Anonymous);
    }

    [TestMethod]
    [DataRow(ChatScene.Friend, false)]
    [DataRow(ChatScene.Friend, true)]
    [DataRow(ChatScene.Temp, false)]
    [DataRow(ChatScene.Temp, true)]
    public async Task AnonymousMarkersAreRejectedInEveryPrivateSceneEvenWhenIgnored(ChatScene scene, bool ignore)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);

        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
            scene, Member, Member, Bot, [new AnonymousSegment(ignore), new TextSegment("private")]));

        Assert.IsEmpty(await store.GetMessagesAsync(new Chat(scene, Member, Bot)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateAndDeeplyNestedMarkersFailWithoutStoringMessages(bool ignore)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);

        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
            ChatScene.Group, GroupId, Member, Bot,
            [new AnonymousSegment(ignore), new TextSegment("duplicate"), new AnonymousSegment(ignore)]));
        var inner = new ForwardSegment("inner", [new ForwardNode(Member, "Forwarded author",
            [new TextSegment("nested"), new AnonymousSegment(ignore)])]);
        var outer = new ForwardSegment("outer", [new ForwardNode(Member, "Forwarded author", [inner])]);
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
            ChatScene.Group, GroupId, Member, Bot, [outer]));
        await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
            ChatScene.Group, GroupId, Member, Bot, [new AnonymousSegment(ignore), outer]));
        foreach (var type in new[] { "anonymous", "ANONYMOUS" })
        {
            var raw = new UnsupportedSegment(type, JsonValue.Parse("{\"ignore\":true}"));
            await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
                ChatScene.Group, GroupId, Member, Bot, [raw]));
            var rawForward = new ForwardSegment("raw", [new ForwardNode(Member, "Forwarded author", [raw])]);
            await AssertErrorAsync(PlatformError.InvalidParameter, () => platform.SendMessageAsync(
                ChatScene.Group, GroupId, Member, Bot, [rawForward]));
        }

        Assert.IsEmpty(await store.GetMessagesAsync(Chat));
    }

    [TestMethod]
    public async Task IdentitiesAreStablePerGroupAndRealSenderAndContainOnlyAnonymousMetadata()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        await platform.SetGroupAnonymousAsync(OtherGroupId, Owner);
        var previousActivity = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        await store.SaveAsync((await store.GetMemberAsync(GroupId, Member))! with { LastSentAt = previousActivity });

        var first = await SendAnonymousAsync(platform);
        var second = await SendAnonymousAsync(platform);
        var otherAccount = await platform.SendMessageAsync(ChatScene.Group, GroupId, Member, Owner,
            [new AnonymousSegment(), new TextSegment("anonymous")]);
        var otherGroup = await SendAnonymousAsync(platform, groupId: OtherGroupId);
        var otherSender = await SendAnonymousAsync(platform, senderId: Admin);
        var identity = first.Anonymous!;

        Assert.IsNotNull(identity);
        Assert.AreEqual(identity, second.Anonymous);
        Assert.AreEqual(identity, otherAccount.Anonymous);
        Assert.AreNotEqual(identity.Id, otherGroup.Anonymous!.Id);
        Assert.AreNotEqual(identity.Flag, otherGroup.Anonymous.Flag);
        Assert.AreNotEqual(identity.Id, otherSender.Anonymous!.Id);
        Assert.AreNotEqual(identity.Flag, otherSender.Anonymous.Flag);
        Assert.IsGreaterThan(0L, identity.Id);
        Assert.AreNotEqual(long.Parse(Member, CultureInfo.InvariantCulture), identity.Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(identity.Name));
        Assert.IsFalse(string.IsNullOrWhiteSpace(identity.Flag));
        Assert.AreNotEqual("Private member nickname", identity.Name);
        Assert.AreNotEqual("Private member card", identity.Name);
        var metadata = JsonSerializer.Serialize(identity);
        Assert.DoesNotContain("Private member", metadata);
        Assert.DoesNotContain("SenderId", metadata);
        Assert.DoesNotContain("UserId", metadata);
        Assert.AreEqual(identity, await store.GetAnonymousIdentityAsync(GroupId, identity.Flag));
        Assert.IsNull(await store.GetAnonymousIdentityAsync(OtherGroupId, identity.Flag));
        AssertOnlyText(first);
        AssertOnlyText((await store.GetMessageAsync(first.Id))!);
        Assert.AreEqual(previousActivity, (await store.GetMemberAsync(GroupId, Member))!.LastSentAt,
            "Anonymous sends must not expose the actor through roster activity.");
    }

    [TestMethod]
    public async Task OwnersAndAdministratorsCanToggleModeAndBanButMembersAndOutsidersCannot()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);

        await AssertErrorAsync(PlatformError.NotPermitted, () => platform.SetGroupAnonymousAsync(GroupId, Member));
        await AssertErrorAsync(PlatformError.NotPermitted, () => platform.SetGroupAnonymousAsync(GroupId, Outsider));
        Assert.IsFalse((await store.GetGroupAsync(GroupId))!.AnonymousEnabled);
        await platform.SetGroupAnonymousAsync(GroupId, Admin);
        var identity = (await SendAnonymousAsync(platform)).Anonymous!;
        await AssertErrorAsync(PlatformError.NotPermitted,
            () => platform.BanAnonymousAsync(GroupId, identity.Flag, Member, TimeSpan.FromMinutes(1)));
        await AssertErrorAsync(PlatformError.NotPermitted,
            () => platform.BanAnonymousAsync(GroupId, identity.Flag, Outsider, TimeSpan.FromMinutes(1)));
        Assert.IsNotNull((await SendAnonymousAsync(platform)).Anonymous);

        await platform.BanAnonymousAsync(GroupId, identity.Flag, Admin, TimeSpan.FromMinutes(1));
        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
        await platform.SetGroupAnonymousAsync(GroupId, Admin, false);
        Assert.IsFalse((await store.GetGroupAsync(GroupId))!.AnonymousEnabled);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var other = (await SendAnonymousAsync(platform, senderId: Admin)).Anonymous!;
        await platform.BanAnonymousAsync(GroupId, other.Flag, Owner, TimeSpan.FromMinutes(1));
        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform, senderId: Admin));
    }

    [TestMethod]
    public async Task AnonymousBanDoesNotMuteTheMemberOrEmitAFakeMemberMuteEvent()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        platform.RegisterBot(Bot);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var identity = (await SendAnonymousAsync(platform)).Anonymous!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        await platform.BanAnonymousAsync(GroupId, identity.Flag, Owner, TimeSpan.FromMinutes(1), timeout.Token);
        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
        Assert.IsNull((await store.GetMemberAsync(GroupId, Member))!.MutedUntil);
        var fallback = await SendAnonymousAsync(platform, ignore: true);
        Assert.IsNull(fallback.Anonymous);
        AssertOnlyText(fallback);
        Assert.IsTrue(await next);
        Assert.IsInstanceOfType<MessageEvent>(events.Current.Payload);
        Assert.AreEqual(fallback.Id, ((MessageEvent)events.Current.Payload).Message.Id,
            "The first event after the ban must be the actual fallback message.");
        Assert.IsNull((await SendOrdinaryAsync(platform)).Anonymous);
        Assert.IsNotNull((await SendAnonymousAsync(platform, senderId: Admin)).Anonymous);
    }

    [TestMethod]
    public async Task BanDurationsMustHaveAPositiveRepresentableExpiryAndFlagsAreGroupScoped()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        await platform.SetGroupAnonymousAsync(OtherGroupId, Owner);
        var identity = (await SendAnonymousAsync(platform)).Anonymous!;

        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
            await AssertErrorAsync(PlatformError.InvalidParameter,
                () => platform.BanAnonymousAsync(GroupId, identity.Flag, Owner, duration));
        await Assert.ThrowsAsync<PlatformException>(
            () => platform.BanAnonymousAsync(OtherGroupId, identity.Flag, Owner, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<PlatformException>(
            () => platform.BanAnonymousAsync(GroupId, "unknown-flag", Owner, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(identity, (await SendAnonymousAsync(platform)).Anonymous);

        await platform.BanAnonymousAsync(GroupId, identity.Flag, Owner, TimeSpan.FromDays(60));
        await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
        Assert.IsNotNull((await SendAnonymousAsync(platform, groupId: OtherGroupId)).Anonymous);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnonymousSendingAndFallbackCannotBypassMemberOrWholeGroupMute(bool ignore)
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        await platform.MuteMemberAsync(GroupId, Member, Owner, TimeSpan.FromMinutes(1));

        await AssertErrorAsync(PlatformError.Muted, () => SendAnonymousAsync(platform, ignore: ignore));
        await platform.SetGroupAnonymousAsync(GroupId, Owner, false);
        await AssertErrorAsync(PlatformError.Muted, () => SendAnonymousAsync(platform, ignore: ignore));
        await platform.MuteMemberAsync(GroupId, Member, Owner, TimeSpan.Zero);
        await platform.SetWholeMuteAsync(GroupId, Owner, true);
        await AssertErrorAsync(PlatformError.WholeGroupMuted, () => SendAnonymousAsync(platform, ignore: ignore));
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        await AssertErrorAsync(PlatformError.WholeGroupMuted, () => SendAnonymousAsync(platform, ignore: ignore));
        Assert.IsEmpty(await store.GetMessagesAsync(Chat));
        Assert.IsNotNull((await SendAnonymousAsync(platform, senderId: Admin, ignore: ignore)).Anonymous);
    }

    [TestMethod]
    public async Task RecallRetainsAnonymousMetadataInMessageHistoryAndRecallEvent()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var message = await SendAnonymousAsync(platform);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();

        var recalled = await platform.RecallMessageAsync(message.Id, Member, timeout.Token);

        Assert.IsTrue(recalled.IsRecalled);
        Assert.AreEqual(message.Anonymous, recalled.Anonymous);
        Assert.AreEqual(message.Anonymous, (await store.GetMessageAsync(message.Id))!.Anonymous);
        Assert.AreEqual(message.Anonymous, (await store.GetMessageAsync(ChatScene.Group, GroupId, message.Seq, Bot))!.Anonymous);
        Assert.AreEqual(message.Anonymous, (await store.GetMessagesAsync(Chat)).Single().Anonymous);
        Assert.AreEqual(message.Anonymous, (await store.GetHistoryAsync(ChatScene.Group, GroupId, Bot)).Single().Anonymous);
        Assert.IsTrue(await next);
        Assert.IsInstanceOfType<MessageRecalledEvent>(events.Current.Payload);
        Assert.AreEqual(message.Anonymous, ((MessageRecalledEvent)events.Current.Payload).Detail.Anonymous);
    }

    [TestMethod]
    public async Task RemovalAndRejoinDiscardTheAliasAndBanWhileOldMessagesKeepTheirIdentity()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var old = await SendAnonymousAsync(platform);
        await platform.BanAnonymousAsync(GroupId, old.Anonymous!.Flag, Owner, TimeSpan.FromDays(1));

        await platform.RemoveMemberAsync(GroupId, Member, Owner, GroupMemberChangeReason.Administrative);

        Assert.IsNull(await store.GetAnonymousIdentityAsync(GroupId, old.Anonymous.Flag));
        Assert.AreEqual(old.Anonymous, (await store.GetMessageAsync(old.Id))!.Anonymous);
        await platform.AddMemberAsync(GroupId, Member, Owner);
        var replacement = await SendAnonymousAsync(platform);
        Assert.AreNotEqual(old.Anonymous.Id, replacement.Anonymous!.Id);
        Assert.AreNotEqual(old.Anonymous.Flag, replacement.Anonymous.Flag);
        Assert.IsNull(await store.GetAnonymousIdentityAsync(GroupId, old.Anonymous.Flag));
        Assert.AreEqual(old.Anonymous, (await platform.RecallMessageAsync(old.Id, Owner)).Anonymous);
    }

    [TestMethod]
    public async Task GroupModeIdentityBanAndRecalledMetadataSurviveDatabaseReopening()
    {
        var database = TemporaryDatabase();
        try
        {
            Message message;
            AnonymousIdentity unbannedIdentity;
            await using (var store = new AsukaStore(database))
            await using (var platform = new PlatformService(store))
            {
                await SeedAsync(store);
                await platform.SetGroupAnonymousAsync(GroupId, Owner);
                message = await SendAnonymousAsync(platform);
                await platform.RecallMessageAsync(message.Id, Member);
                await platform.BanAnonymousAsync(GroupId, message.Anonymous!.Flag, Owner, TimeSpan.FromDays(1));
                unbannedIdentity = (await SendAnonymousAsync(platform, senderId: Admin)).Anonymous!;
            }

            await using (var reopened = new AsukaStore(database))
            await using (var platform = new PlatformService(reopened))
            {
                Assert.IsTrue((await reopened.GetGroupAsync(GroupId))!.AnonymousEnabled);
                var restored = (await reopened.GetMessageAsync(message.Id))!;
                Assert.IsTrue(restored.IsRecalled);
                Assert.AreEqual(message.Anonymous, restored.Anonymous);
                AssertOnlyText(restored);
                Assert.AreEqual(message.Anonymous, await reopened.GetAnonymousIdentityAsync(GroupId, message.Anonymous!.Flag));
                await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
                Assert.IsNull((await SendAnonymousAsync(platform, ignore: true)).Anonymous);
                Assert.AreEqual(unbannedIdentity, (await SendAnonymousAsync(platform, senderId: Admin)).Anonymous);
            }
        }
        finally { DeleteDatabase(database); }
    }

    [TestMethod]
    public async Task ShorterBansAndModeTogglesCannotClearABanAndExpiryReusesTheIdentity()
    {
        var database = TemporaryDatabase();
        try
        {
            await using var store = new AsukaStore(database);
            await SeedAsync(store);
            await using var platform = new PlatformService(store);
            await platform.SetGroupAnonymousAsync(GroupId, Owner);
            var identity = (await SendAnonymousAsync(platform)).Anonymous!;
            await platform.BanAnonymousAsync(GroupId, identity.Flag, Owner, TimeSpan.FromDays(1));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT muted_until FROM group_anonymous_identities WHERE group_id=$group AND flag=$flag;";
            command.Parameters.AddWithValue("$group", GroupId);
            command.Parameters.AddWithValue("$flag", identity.Flag);
            var originalUntil = (long)(await command.ExecuteScalarAsync())!;

            await platform.BanAnonymousAsync(GroupId, identity.Flag, Admin, TimeSpan.FromMinutes(1));
            await platform.SetGroupAnonymousAsync(GroupId, Owner, false);
            await platform.SetGroupAnonymousAsync(GroupId, Owner);

            Assert.AreEqual(originalUntil, (long)(await command.ExecuteScalarAsync())!);
            await AssertErrorAsync(PlatformError.NotPermitted, () => SendAnonymousAsync(platform));
            Assert.AreEqual(identity, await store.GetAnonymousIdentityAsync(GroupId, identity.Flag));

            // Simulate elapsed time without a timing-sensitive sleep or shortening
            // a ban through the public API, which deliberately forbids that.
            command.CommandText = "UPDATE group_anonymous_identities SET muted_until=1 WHERE group_id=$group AND flag=$flag;";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
            Assert.AreEqual(identity, (await SendAnonymousAsync(platform)).Anonymous);
        }
        finally { DeleteDatabase(database); }
    }

    [TestMethod]
    public async Task InvalidReplyCannotCreateAnAliasAndDirectStoreWritesRejectUnresolvedMarkers()
    {
        var database = TemporaryDatabase();
        try
        {
            await using var store = new AsukaStore(database);
            await SeedAsync(store);
            await using var platform = new PlatformService(store);
            await platform.SetGroupAnonymousAsync(GroupId, Owner);

            var invalidReply = new Message(ChatScene.Group, GroupId, Member, Bot,
                [new AnonymousSegment(), new ReplySegment("missing-message"), new TextSegment("invalid reply")],
                MessageDirection.Outgoing);
            // AppendLive creates the alias before validating replies inside its
            // transaction, so failure must roll back both the alias and message.
            await AssertErrorAsync(PlatformError.InvalidParameter, () => store.AppendLiveMessageAsync(invalidReply));
            var unresolved = new Message(ChatScene.Group, GroupId, Member, Bot,
                [new AnonymousSegment(), new TextSegment("unresolved")], MessageDirection.Outgoing);
            await AssertErrorAsync(PlatformError.InvalidParameter, () => store.AppendMessageAsync(unresolved));
            await AssertErrorAsync(PlatformError.InvalidParameter, () => store.SaveAsync(unresolved));
            Assert.IsEmpty(await store.GetMessagesAsync(Chat));

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM group_anonymous_identities;";
            Assert.AreEqual(0L, await command.ExecuteScalarAsync());
        }
        finally { DeleteDatabase(database); }
    }

    [TestMethod]
    public async Task ParallelSendsThroughSeparateServicesReuseOneIdentity()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var first = new PlatformService(store);
        await using var second = new PlatformService(store);
        await first.SetGroupAnonymousAsync(GroupId, Owner);

        var messages = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => SendAnonymousAsync(index % 2 == 0 ? first : second)));

        Assert.AreEqual(1, messages.Select(message => message.Anonymous).Distinct().Count());
        Assert.IsNotNull(messages[0].Anonymous);
        Assert.AreEqual(12, messages.Select(message => message.Seq).Distinct().Count());
        Assert.HasCount(12, await store.GetMessagesAsync(Chat));
    }

    [TestMethod]
    public async Task CancelledControlsDoNotChangeModeOrBanAndCancelledSendDoesNotAppend()
    {
        await using var store = new AsukaStore();
        await SeedAsync(store);
        await using var platform = new PlatformService(store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => platform.SetGroupAnonymousAsync(GroupId, Owner, cancellationToken: cancelled.Token));
        Assert.IsFalse((await store.GetGroupAsync(GroupId))!.AnonymousEnabled);
        await platform.SetGroupAnonymousAsync(GroupId, Owner);
        var identity = (await SendAnonymousAsync(platform)).Anonymous!;
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.BanAnonymousAsync(
            GroupId, identity.Flag, Owner, TimeSpan.FromMinutes(1), cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.SendMessageAsync(
            ChatScene.Group, GroupId, Member, Bot, [new AnonymousSegment(), new TextSegment("cancelled")], cancelled.Token));
        Assert.HasCount(1, await store.GetMessagesAsync(Chat));
        Assert.AreEqual(identity, (await SendAnonymousAsync(platform)).Anonymous);
    }

    [TestMethod]
    public async Task FrozenVersionElevenMigratesPreservingOrdinaryMessagesMembersAndHonors()
    {
        var database = TemporaryDatabase();
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                VersionElevenStoreSchema.Create(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO users(id,name,nickname,sex,sign,created_at)
                        VALUES('10001','Owner','Owner','unknown','',0),('10003','Member','Member','unknown','',0),
                            ('10004','Bot','Bot','unknown','',0);
                    INSERT INTO groups(id,name,intro,level,max_member_count,whole_muted,created_at)
                        VALUES('50001','Historical group','',1,200,0,0);
                    INSERT INTO group_members(group_id,user_id,card,role,title,joined_at)
                        VALUES('50001','10001','','owner','',0),('50001','10003','','member','',0),
                            ('50001','10004','','member','',0);
                    INSERT INTO messages(id,seq,scene,peer_id,sender_id,self_id,content,time,direction)
                        VALUES('historical-message',1,'group','50001','10003','10004',
                            '[{"type":"text","text":"historical text"}]',0,'outgoing');
                    INSERT INTO group_honors(group_id,user_id,type,description)
                        VALUES('50001','10003',0,'Historical honor');
                    INSERT INTO group_current_talkative(group_id,user_id,day_count)
                        VALUES('50001','10003',3);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var migrated = new AsukaStore(database))
            await using (var platform = new PlatformService(migrated))
            {
                Assert.HasCount(3, await migrated.GetMembersAsync(GroupId));
                Assert.IsFalse((await migrated.GetGroupAsync(GroupId))!.AnonymousEnabled);
                var ordinary = (await migrated.GetMessageAsync("historical-message"))!;
                Assert.IsNull(ordinary.Anonymous);
                Assert.AreEqual(Member, ordinary.SenderId);
                Assert.AreEqual("historical text", ((TextSegment)ordinary.Content.Single()).Text);
                var honors = await migrated.GetGroupHonorInfoAsync(GroupId, Owner);
                Assert.AreEqual(Member, honors.CurrentTalkative!.UserId);
                Assert.AreEqual(3, honors.CurrentTalkative.DayCount);
                Assert.AreEqual("Historical honor", honors.TalkativeList.Single().Description);
                await platform.SetGroupAnonymousAsync(GroupId, Owner);
                var anonymous = await SendAnonymousAsync(platform);
                Assert.AreEqual(2L, anonymous.Seq);
                Assert.IsNotNull(anonymous.Anonymous);
                AssertOnlyText(anonymous);
            }
            await using var reopened = new AsukaStore(database);
            Assert.IsTrue((await reopened.GetGroupAsync(GroupId))!.AnonymousEnabled);
            Assert.HasCount(2, await reopened.GetMessagesAsync(Chat));
        }
        finally { DeleteDatabase(database); }
    }

    private static Chat Chat => new(ChatScene.Group, GroupId, Bot);

    private static Task<Message> SendAnonymousAsync(PlatformService platform, string groupId = GroupId,
        string senderId = Member, bool ignore = false) =>
        platform.SendMessageAsync(ChatScene.Group, groupId, senderId, Bot,
            [new AnonymousSegment(ignore), new TextSegment("anonymous")]);

    private static Task<Message> SendOrdinaryAsync(PlatformService platform) =>
        platform.SendMessageAsync(ChatScene.Group, GroupId, Member, Bot, [new TextSegment("ordinary")]);

    private static void AssertOnlyText(Message message)
    {
        Assert.HasCount(1, message.Content);
        Assert.IsInstanceOfType<TextSegment>(message.Content.Single());
        Assert.AreEqual("anonymous", ((TextSegment)message.Content.Single()).Text);
    }

    private static async Task AssertErrorAsync(PlatformError expected, Func<Task> operation) =>
        Assert.AreEqual(expected, (await Assert.ThrowsAsync<PlatformException>(operation)).Error);

    private static async Task SeedAsync(AsukaStore store)
    {
        await store.SaveAsync(new User("Owner", id: Owner));
        await store.SaveAsync(new User("Admin", id: Admin));
        await store.SaveAsync(new User("Private member name", id: Member, nickname: "Private member nickname"));
        await store.SaveAsync(new User("Bot", id: Bot));
        await store.SaveAsync(new User("Outsider", id: Outsider));
        foreach (var groupId in new[] { GroupId, OtherGroupId })
        {
            await store.SaveAsync(new Group("Group", id: groupId));
            await store.SaveAsync(new GroupMember(groupId, Owner, role: GroupRole.Owner));
            await store.SaveAsync(new GroupMember(groupId, Admin, role: GroupRole.Admin));
            await store.SaveAsync(new GroupMember(groupId, Member, card: "Private member card"));
            await store.SaveAsync(new GroupMember(groupId, Bot));
        }
    }

    private static string TemporaryDatabase() =>
        Path.Combine(Path.GetTempPath(), $"asuka-anonymous-{Guid.NewGuid():N}.sqlite3");

    private static void DeleteDatabase(string database)
    {
        File.Delete(database);
        File.Delete(database + "-wal");
        File.Delete(database + "-shm");
    }
}
