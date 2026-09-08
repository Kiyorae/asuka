using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Milky 1.3 contracts: https://milky.ntqqrev.org/api/system, /api/message,
// /api/friend and /struct/Event. These exercise state changes, not success stubs.
[TestClass]
public sealed class MilkyPeerStateTests
{
    [TestMethod]
    public async Task PinsDefaultToTrueAndReturnOnlyTheCurrentAccountsFriendsAndGroups()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var protocol = Create(fixture);
        Assert.IsTrue((await protocol.HandleAsync(PeerCall("set_peer_pin", "friend", ProtocolTestFixture.SenderId))).IsSuccess);
        Assert.IsTrue((await protocol.HandleAsync(PeerCall("set_peer_pin", "group", ProtocolTestFixture.GroupId))).IsSuccess);
        var pinned = await protocol.HandleAsync(new ProtocolCall("get_peer_pins", new JsonObject()));
        Assert.IsTrue(pinned.IsSuccess, pinned.Message);
        Assert.HasCount(1, pinned.Data!["friends"]!.AsArray());
        Assert.HasCount(1, pinned.Data!["groups"]!.AsArray());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.SenderId, System.Globalization.CultureInfo.InvariantCulture), pinned.Data!["friends"]![0]!["user_id"]!.GetValue<long>());
        var other = new MilkyProtocol(ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        var othersPins = await other.HandleAsync(new ProtocolCall("get_peer_pins", new JsonObject()));
        Assert.HasCount(0, othersPins.Data!["friends"]!.AsArray());
        Assert.HasCount(0, othersPins.Data!["groups"]!.AsArray());
        var unpin = PeerCall("set_peer_pin", "group", ProtocolTestFixture.GroupId);
        unpin.Parameters["is_pinned"] = false;
        Assert.IsTrue((await protocol.HandleAsync(unpin)).IsSuccess);
        Assert.IsFalse((await fixture.Store.GetPeerStateAsync(GroupChat())).IsPinned);
        Assert.IsTrue((await fixture.Store.GetPeerStateAsync(FriendChat())).IsPinned);
    }

    [TestMethod]
    public async Task PinChangesEmitTheOfficialEventOnlyForTheAccountWhosePinChanged()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator();
        var next = events.MoveNextAsync().AsTask();
        await fixture.Platform.SetPeerPinAsync(GroupChat(), true);
        Assert.IsTrue(await next);
        Assert.AreEqual(ProtocolTestFixture.SelfId, events.Current.SelfId);
        var payload = (PeerPinChangedEvent)events.Current.Payload;
        Assert.IsTrue(payload.IsPinned);
        var frames = await Create(fixture).EncodeAsync(events.Current);
        Assert.HasCount(1, frames);
        Assert.AreEqual("peer_pin_change", frames[0].Payload["event_type"]!.GetValue<string>());
        Assert.AreEqual("group", frames[0].Payload["data"]!["message_scene"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.GroupId, System.Globalization.CultureInfo.InvariantCulture), frames[0].Payload["data"]!["peer_id"]!.GetValue<long>());
        Assert.IsTrue(frames[0].Payload["data"]!["is_pinned"]!.GetValue<bool>());
        var other = new MilkyProtocol(ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        Assert.HasCount(0, await other.EncodeAsync(events.Current));
        next = events.MoveNextAsync().AsTask();
        await fixture.Platform.SetPeerPinAsync(GroupChat(), true);
        await fixture.Platform.SetPeerPinAsync(GroupChat(), false);
        Assert.IsTrue(await next);
        Assert.IsFalse(((PeerPinChangedEvent)events.Current.Payload).IsPinned,
            "Repeating a pin without a state change must not emit another change event.");
    }

    [TestMethod]
    public async Task ReadWatermarksAreScopedMonotonicAndRejectUnknownSequences()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var first = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("first")]);
        var last = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("second")]);
        var protocol = Create(fixture);
        foreach (var seq in new[] { last.Seq, first.Seq, last.Seq })
        {
            var read = PeerCall("mark_message_as_read", "group", ProtocolTestFixture.GroupId);
            read.Parameters["message_seq"] = seq;
            Assert.IsTrue((await protocol.HandleAsync(read)).IsSuccess);
        }

        Assert.AreEqual(last.Seq, (await fixture.Store.GetPeerStateAsync(GroupChat())).LastReadSequence);
        Assert.AreEqual(0L, (await fixture.Store.GetPeerStateAsync(FriendChat())).LastReadSequence);
        Assert.AreEqual(0L, (await fixture.Store.GetPeerStateAsync(new Chat(ChatScene.Group,
            ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))).LastReadSequence);
        var invalid = PeerCall("mark_message_as_read", "group", ProtocolTestFixture.GroupId);
        invalid.Parameters["message_seq"] = last.Seq + 1;
        Assert.IsFalse((await protocol.HandleAsync(invalid)).IsSuccess);
        Assert.AreEqual(last.Seq, (await fixture.Store.GetPeerStateAsync(GroupChat())).LastReadSequence);
    }

    [TestMethod]
    public async Task TempPinsAndReadPositionDoNotLeakIntoFriendState()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var message = await fixture.Platform.SendMessageAsync(ChatScene.Temp, ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("temporary")]);
        var protocol = Create(fixture);
        Assert.IsTrue((await protocol.HandleAsync(PeerCall("set_peer_pin", "temp", ProtocolTestFixture.SenderId))).IsSuccess);
        var read = PeerCall("mark_message_as_read", "temp", ProtocolTestFixture.SenderId);
        read.Parameters["message_seq"] = message.Seq;
        Assert.IsTrue((await protocol.HandleAsync(read)).IsSuccess);
        var state = await fixture.Store.GetPeerStateAsync(new Chat(ChatScene.Temp,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId));
        Assert.IsTrue(state.IsPinned);
        Assert.AreEqual(message.Seq, state.LastReadSequence);
        Assert.IsFalse((await fixture.Store.GetPeerStateAsync(FriendChat())).IsPinned);
        var pins = await protocol.HandleAsync(new ProtocolCall("get_peer_pins", new JsonObject()));
        Assert.HasCount(0, pins.Data!["friends"]!.AsArray());
    }

    [TestMethod]
    public async Task ProfileUpdatesAndLikesArePersistedAndVisibleThroughProtocolReads()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var protocol = Create(fixture);
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("set_nickname",
            new JsonObject { ["new_nickname"] = "New Asuka" }))).IsSuccess);
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("set_bio",
            new JsonObject { ["new_bio"] = "A new bio" }))).IsSuccess);
        var login = await protocol.HandleAsync(new ProtocolCall("get_login_info", new JsonObject()));
        Assert.AreEqual("New Asuka", login.Data!["nickname"]!.GetValue<string>());
        var profile = await protocol.HandleAsync(new ProtocolCall("get_user_profile",
            new JsonObject { ["user_id"] = ProtocolTestFixture.SelfId }));
        Assert.AreEqual("A new bio", profile.Data!["bio"]!.GetValue<string>());
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("set_bio",
            new JsonObject { ["new_bio"] = "" }))).IsSuccess);
        Assert.AreEqual("", (await fixture.Store.GetUserAsync(ProtocolTestFixture.SelfId))!.Sign);
        foreach (var count in new int?[] { null, 4 })
        {
            var parameters = new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId };
            if (count is not null) parameters["count"] = count;
            var like = await protocol.HandleAsync(new ProtocolCall("send_profile_like", parameters));
            Assert.IsTrue(like.IsSuccess, parameters.ToJsonString() + ": " + like.Message);
        }
        var likes = await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId);
        Assert.HasCount(1, likes);
        Assert.AreEqual(5L, likes[0].Count);
        Assert.AreEqual(ProtocolTestFixture.SelfId, likes[0].SenderId);
        var customFaces = await protocol.HandleAsync(new ProtocolCall("get_custom_face_url_list", new JsonObject()));
        Assert.IsTrue(customFaces.IsSuccess);
        Assert.HasCount(0, customFaces.Data!["urls"]!.AsArray());
    }

    [TestMethod]
    public async Task InvalidPeerProfileArgumentsCannotMutateState()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var protocol = Create(fixture);
        var invalidPin = PeerCall("set_peer_pin", "group", ProtocolTestFixture.GroupId);
        invalidPin.Parameters["is_pinned"] = "invalid";
        ProtocolCall[] invalidCalls =
        [
            invalidPin,
            PeerCall("set_peer_pin", "invalid", ProtocolTestFixture.SenderId),
            PeerCall("set_peer_pin", "group", "999"),
            new("set_nickname", new JsonObject { ["new_nickname"] = "  " }),
            new("set_nickname", new JsonObject { ["new_nickname"] = 123 }),
            new("set_bio", new JsonObject()),
            new("set_bio", new JsonObject { ["new_bio"] = new JsonArray() }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = 0 }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = -1 }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = 2147483648L }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = "invalid" }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = true }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SenderId, ["count"] = 1.5 }),
            new("send_profile_like", new JsonObject { ["user_id"] = ProtocolTestFixture.SelfId }),
        ];
        foreach (var call in invalidCalls)
        {
            Assert.IsFalse((await protocol.HandleAsync(call)).IsSuccess, call.Name + ": " + call.Parameters.ToJsonString());
        }
        Assert.HasCount(0, await fixture.Store.GetPeerStatesAsync(ProtocolTestFixture.SelfId));
        Assert.HasCount(0, await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId));
        Assert.AreEqual("Asuka Bot", (await fixture.Store.GetUserAsync(ProtocolTestFixture.SelfId))!.Nickname);
    }

    [TestMethod]
    public async Task AvatarIngestsThroughSafeMediaBoundaryAndRejectsLocalPathsOrNonImages()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedAsync(fixture);
        var protocol = Create(fixture);
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRZsAAAAASUVORK5CYII=";
        var response = await protocol.HandleAsync(new ProtocolCall("set_avatar",
            new JsonObject { ["uri"] = "base64://" + png }));
        Assert.IsTrue(response.IsSuccess, response.Message);
        var avatar = (await fixture.Store.GetUserAsync(ProtocolTestFixture.SelfId))!.Avatar;
        Assert.IsTrue(Uri.TryCreate(avatar, UriKind.Absolute, out var uri) && uri.IsFile);
        Assert.IsTrue(File.Exists(uri!.LocalPath));
        foreach (var input in new[] { "file:///C:/Windows/win.ini", "\\\\server\\share\\avatar.png", "base64://AQID" })
        {
            Assert.IsFalse((await protocol.HandleAsync(new ProtocolCall("set_avatar", new JsonObject { ["uri"] = input }))).IsSuccess);
        }
        Assert.AreEqual(avatar, (await fixture.Store.GetUserAsync(ProtocolTestFixture.SelfId))!.Avatar);
    }

    private static async Task SeedAsync(ProtocolTestFixture fixture)
    {
        await fixture.SeedGroupAsync();
        await fixture.Platform.AddFriendshipAsync(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
    }

    private static MilkyProtocol Create(ProtocolTestFixture fixture) => new(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
    private static Chat GroupChat() => new(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
    private static Chat FriendChat() => new(ChatScene.Friend, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId);
    private static ProtocolCall PeerCall(string name, string scene, string peerId) => new(name,
        new JsonObject { ["message_scene"] = scene, ["peer_id"] = peerId });
}
