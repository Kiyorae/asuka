using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotAnonymousTests
{
    private static readonly string[] ExpectedAnonymousQuickActions = ["send_group_msg", "delete_msg", "set_group_anonymous_ban"];
    // Official contracts: onebot-11/event/message.md, api/public.md,
    // and message/segment.md. Defaults not specified there are named as Asuka policy.
    [TestMethod]
    public async Task AnonymousApisAreV11OnlyAndToggleDefaultsToEnabled()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = Protocol(fixture, OneBotVersion.V12);
        foreach (var action in new[] { "set_group_anonymous", "set_group_anonymous_ban" })
            Assert.AreEqual(10002, (await v12.HandleAsync(new ProtocolCall(action, GroupParameters()))).RetCode);
        Assert.IsFalse((await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.AnonymousEnabled);
        Assert.AreEqual(0, (await v11.HandleAsync(new ProtocolCall("set_group_anonymous", GroupParameters()))).RetCode);
        Assert.IsTrue((await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.AnonymousEnabled);
        Assert.AreEqual(0, (await v11.HandleAsync(new ProtocolCall("set_group_anonymous", GroupParameters()))).RetCode);
        var disable = GroupParameters();
        disable["enable"] = false;
        Assert.AreEqual(0, (await v11.HandleAsync(new ProtocolCall("set_group_anonymous", disable))).RetCode);
        Assert.IsFalse((await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.AnonymousEnabled);
    }

    [TestMethod]
    public async Task AnonymousToggleRejectsInvalidBooleanAndRequiresGroupAuthority()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        foreach (var invalid in new JsonNode?[] { null, 1, "yes", "", new JsonObject() })
        {
            var parameters = GroupParameters();
            parameters["enable"] = invalid?.DeepClone();
            Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous", parameters))).RetCode);
        }
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, role: GroupRole.Member));
        Assert.AreEqual(1403, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous", GroupParameters()))).RetCode);
        Assert.IsFalse((await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.AnonymousEnabled);
    }

    [TestMethod]
    [DataRow("[CQ:anonymous]secret")]
    [DataRow("[CQ:anonymous,ignore=0]secret")]
    public async Task CqAnonymousSendPersistsIdentityWithoutPersistingSendingInstruction(string content)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.SetGroupAnonymousAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        using var protocol = Protocol(fixture);
        var parameters = GroupParameters();
        parameters["message"] = content;
        var reply = await protocol.HandleAsync(new ProtocolCall("send_group_msg", parameters));
        Assert.AreEqual(0, reply.RetCode);
        var message = await fixture.Store.GetMessageAsync(reply.Data!["message_id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture));
        Assert.IsNotNull(message);
        Assert.IsNotNull(message.Anonymous);
        Assert.IsFalse(message.Content.Any(segment => segment is AnonymousSegment));
        Assert.AreEqual("secret", message.Content.PlainText());
        var wire = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)))).Single().Payload;
        Assert.AreEqual("anonymous", wire["sub_type"]!.GetValue<string>());
        Assert.AreEqual(message.Anonymous.Id, wire["user_id"]!.GetValue<long>());
        Assert.AreEqual(message.Anonymous.Id, wire["anonymous"]!["id"]!.GetValue<long>());
        Assert.AreEqual(message.Anonymous.Name, wire["anonymous"]!["name"]!.GetValue<string>());
        Assert.AreEqual(message.Anonymous.Flag, wire["anonymous"]!["flag"]!.GetValue<string>());
        Assert.AreEqual("secret", wire["raw_message"]!.GetValue<string>());
        Assert.HasCount(1, (JsonArray)wire["message"]!);
        Assert.AreEqual("text", wire["message"]![0]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArrayAnonymousIgnoreSupportsNumericAndCqStringValues(bool textValues)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var parameters = GroupParameters();
        parameters["message"] = MessageArray(textValues ? System.Text.Json.Nodes.JsonValue.Create("0") : System.Text.Json.Nodes.JsonValue.Create(0));
        Assert.AreNotEqual(0, (await protocol.HandleAsync(new ProtocolCall("send_group_msg", parameters))).RetCode);
        parameters["message"] = MessageArray(textValues ? System.Text.Json.Nodes.JsonValue.Create("1") : System.Text.Json.Nodes.JsonValue.Create(1));
        var reply = await protocol.HandleAsync(new ProtocolCall("send_group_msg", parameters));
        Assert.AreEqual(0, reply.RetCode);
        var message = await fixture.Store.GetMessageAsync(reply.Data!["message_id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture));
        Assert.IsNotNull(message);
        Assert.IsNull(message.Anonymous);
        Assert.AreEqual("secret", message.Content.PlainText());
        var wire = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)))).Single().Payload;
        Assert.AreEqual("normal", wire["sub_type"]!.GetValue<string>());
        Assert.IsTrue(wire.ContainsKey("anonymous"));
        Assert.IsNull(wire["anonymous"]);
    }

    [TestMethod]
    public async Task AnonymousSendingRejectsMalformedIgnorePrivateChatsAndV12Segments()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = Protocol(fixture, OneBotVersion.V12);
        foreach (var invalid in new JsonNode?[] { null, true, false, 2, -1, 0.5, "true", "yes", new JsonObject() })
        {
            var parameters = GroupParameters();
            parameters["message"] = MessageArray(invalid);
            Assert.AreEqual(1400, (await v11.HandleAsync(new ProtocolCall("send_group_msg", parameters))).RetCode, invalid?.ToJsonString());
        }
        var privateReply = await v11.HandleAsync(new ProtocolCall("send_private_msg", new JsonObject
        {
            ["user_id"] = ProtocolTestFixture.SenderId,
            ["message"] = "[CQ:anonymous,ignore=1]secret",
        }));
        Assert.AreNotEqual(0, privateReply.RetCode);
        var v12Parameters = GroupParameters();
        v12Parameters["detail_type"] = "group";
        v12Parameters["message"] = MessageArray(0);
        Assert.AreEqual(10005, (await v12.HandleAsync(new ProtocolCall("send_message", v12Parameters))).RetCode);
        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task AnonymousEventAndGetMsgExposeOnlySyntheticSender()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, wire) = await IncomingAsync(fixture, protocol);
        var reply = await protocol.HandleAsync(new ProtocolCall("get_msg", new JsonObject { ["message_id"] = message.Id }));
        Assert.AreEqual(0, reply.RetCode);
        foreach (var sender in new[] { wire["sender"]!, reply.Data!["sender"]! })
        {
            Assert.AreEqual(message.Anonymous!.Id, sender["user_id"]!.GetValue<long>());
            Assert.AreEqual(message.Anonymous.Name, sender["nickname"]!.GetValue<string>());
            Assert.IsFalse(((JsonObject)sender).ContainsKey("card"));
            Assert.IsFalse(((JsonObject)sender).ContainsKey("title"));
            Assert.IsFalse(((JsonObject)sender).ContainsKey("role"));
        }
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, wire.ToJsonString());
        Assert.DoesNotContain("Alice", wire.ToJsonString());
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, reply.Data!.ToJsonString());
        Assert.DoesNotContain("Alice", reply.Data.ToJsonString());
        // get_msg preserves the official six-field response; no extra anonymous
        // metadata field is invented to make the sender branch safe.
        Assert.HasCount(6, (JsonObject)reply.Data);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecallUsesAnonymousSenderAndOnlyRevealsIndependentModerator(bool authorRecalls)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = Protocol(fixture, OneBotVersion.V12);
        var (message, _) = await IncomingAsync(fixture, v11);
        var actor = authorRecalls ? ProtocolTestFixture.SenderId : ProtocolTestFixture.SelfId;
        await fixture.Platform.RecallMessageAsync(message.Id, actor);
        var recall = new MessageRecalled(message.Id, message.Scene, message.PeerId, message.SenderId, actor, message.Anonymous);
        var domainEvent = new DomainEvent(ProtocolTestFixture.SelfId, new MessageRecalledEvent(recall));
        var wire = (await v11.EncodeAsync(domainEvent)).Single().Payload;
        Assert.AreEqual(message.Anonymous!.Id, wire["user_id"]!.GetValue<long>());
        Assert.AreEqual(authorRecalls ? message.Anonymous.Id : long.Parse(ProtocolTestFixture.SelfId, CultureInfo.InvariantCulture),
            wire["operator_id"]!.GetValue<long>());
        Assert.DoesNotContain(ProtocolTestFixture.SenderId, wire.ToJsonString());
        Assert.IsEmpty(await v12.EncodeAsync(domainEvent));
    }

    [TestMethod]
    public async Task V12SuppressesAnonymousMessagesAndSanitizesImplicitAndExplicitReplyAuthors()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = Protocol(fixture);
        using var v12 = Protocol(fixture, OneBotVersion.V12);
        var (message, _) = await IncomingAsync(fixture, v11);
        Assert.IsEmpty(await v12.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message))));
        var codec = new OneBotSegmentCodec(OneBotVersion.V12, new StubProtocolAssetResolver(), fixture.Store);
        foreach (var sender in new[] { null, ProtocolTestFixture.SenderId })
        {
            var segments = await codec.EncodeAsync([new ReplySegment(message.Id, sender)]);
            Assert.AreEqual(message.Anonymous!.Id.ToString(CultureInfo.InvariantCulture), segments[0]!["data"]!["user_id"]!.GetValue<string>());
            Assert.DoesNotContain(ProtocolTestFixture.SenderId, segments.ToJsonString());
        }
    }

    [TestMethod]
    [DataRow("anonymous_flag")]
    [DataRow("flag")]
    public async Task AnonymousBanByFlagUsesDefaultThirtyMinutesAndLeavesRealMemberUnmuted(string key)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, _) = await IncomingAsync(fixture, protocol);
        var parameters = GroupParameters();
        parameters[key] = message.Anonymous!.Flag;
        Assert.AreEqual(0, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
        Assert.IsNull((await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId))!.MutedUntil);
        var error = await Assert.ThrowsAsync<PlatformException>(() => fixture.Platform.SendMessageAsync(ChatScene.Group,
            ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId,
            [new AnonymousSegment(), new TextSegment("blocked")]));
        Assert.AreEqual(PlatformError.NotPermitted, error.Error);
        // A normal message still works; the flag does not mute the real member.
        await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment("normal")]);
    }

    [TestMethod]
    public async Task AnonymousObjectTakesPrecedenceAndMustMatchAllStoredIdentityFields()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (_, wire) = await IncomingAsync(fixture, protocol);
        foreach (var field in new[] { "id", "name", "flag" })
        {
            var forged = (JsonObject)wire["anonymous"]!.DeepClone();
            forged[field] = field == "id" ? System.Text.Json.Nodes.JsonValue.Create(7) : System.Text.Json.Nodes.JsonValue.Create("forged");
            var parameters = GroupParameters();
            parameters["anonymous"] = forged;
            parameters["anonymous_flag"] = wire["anonymous"]!["flag"]!.DeepClone();
            Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode, field);
        }
        var valid = GroupParameters();
        valid["anonymous"] = wire["anonymous"]!.DeepClone();
        valid["anonymous_flag"] = "wrong fallback";
        valid["duration"] = 0.25;
        Assert.AreEqual(0, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", valid))).RetCode);
    }

    [TestMethod]
    public async Task AnonymousBanRejectsZeroNegativeInvalidAndUnknownTargetsWithoutFallingBack()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, _) = await IncomingAsync(fixture, protocol);
        foreach (var duration in new JsonNode?[] { null, 0, -1, true, "oops", long.MaxValue, new JsonObject() })
        {
            var parameters = GroupParameters();
            parameters["flag"] = message.Anonymous!.Flag;
            parameters["duration"] = duration?.DeepClone();
            Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
        }
        foreach (var invalid in new JsonNode?[] { null, "not an object", new JsonObject(), 1 })
        {
            var parameters = GroupParameters();
            parameters["anonymous"] = invalid?.DeepClone();
            parameters["flag"] = message.Anonymous!.Flag;
            Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
        }
        var unknown = GroupParameters();
        unknown["flag"] = "unknown";
        Assert.AreNotEqual(0, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", unknown))).RetCode);
        await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new AnonymousSegment(), new TextSegment("still allowed")]);
    }

    [TestMethod]
    public async Task AnonymousBanAcceptsMoreThanThirtyDaysAndBindsFlagToItsGroup()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, _) = await IncomingAsync(fixture, protocol);
        const string otherGroup = "500000009";
        await fixture.Store.SaveAsync(new Group("Other group", id: otherGroup));
        await fixture.Store.SaveAsync(new GroupMember(otherGroup, ProtocolTestFixture.SelfId, role: GroupRole.Owner));
        var parameters = new JsonObject
        {
            ["group_id"] = otherGroup,
            ["flag"] = message.Anonymous!.Flag,
            ["duration"] = 60 * 86400,
        };
        Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
        parameters["group_id"] = ProtocolTestFixture.GroupId;
        Assert.AreEqual(0, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
    }

    [TestMethod]
    public async Task AnonymousQuickReplyDeleteAndBanUseExactAliasAndIgnoreMentionAndKick()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, wire) = await IncomingAsync(fixture, protocol);
        var result = await protocol.HandleQuickOperationAsync(wire, new JsonObject
        {
            ["reply"] = "hello",
            ["at_sender"] = true,
            ["delete"] = true,
            ["ban"] = true,
            ["kick"] = true,
        });
        CollectionAssert.AreEqual(ExpectedAnonymousQuickActions, result.Select(item => item.Action).ToArray());
        Assert.IsTrue(result.All(item => item.RetCode == 0));
        Assert.IsTrue((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        var member = await fixture.Store.GetMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId);
        Assert.IsNotNull(member);
        Assert.IsNull(member.MutedUntil);
        var reply = (await fixture.Store.GetMessagesAsync(message.Chat)).Single(item => item.SenderId == ProtocolTestFixture.SelfId);
        Assert.HasCount(1, reply.Content);
        Assert.AreEqual("hello", ((TextSegment)reply.Content[0]).Text);
        await Assert.ThrowsAsync<PlatformException>(() => fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new AnonymousSegment(), new TextSegment("blocked")]));
    }

    [TestMethod]
    public async Task AnonymousQuickOperationRejectsForgedIdentityAndNormalSubtypeBeforeAnyAction()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (message, wire) = await IncomingAsync(fixture, protocol);
        foreach (var field in new[] { "id", "name", "flag", "user_id", "sub_type", "group_id", "self_id", "message_id", "anonymous" })
        {
            var forged = (JsonObject)wire.DeepClone();
            if (field == "id") forged["anonymous"]![field] = 1;
            else if (field is "name" or "flag") forged["anonymous"]![field] = "forged";
            else if (field == "sub_type") forged[field] = "normal";
            else if (field == "anonymous") forged[field] = null;
            else forged[field] = long.Parse(ProtocolTestFixture.SenderId, CultureInfo.InvariantCulture);
            var result = await protocol.HandleQuickOperationAsync(forged, new JsonObject
            {
                ["reply"] = "must not send",
                ["delete"] = true,
                ["ban"] = true,
                ["kick"] = true,
            });
            Assert.AreEqual(1400, result.Single().RetCode, field);
        }
        Assert.HasCount(1, await fixture.Store.GetMessagesAsync(message.Chat));
        Assert.IsFalse((await fixture.Store.GetMessageAsync(message.Id))!.IsRecalled);
        Assert.IsEmpty(await protocol.HandleQuickOperationAsync(wire, new JsonObject { ["kick"] = true }));
    }

    [TestMethod]
    public async Task AnonymousQuickBanCannotCancelAndRequiresModeratorPermission()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var protocol = Protocol(fixture);
        var (_, wire) = await IncomingAsync(fixture, protocol);
        Assert.AreEqual(1400, (await protocol.HandleQuickOperationAsync(wire,
            new JsonObject { ["ban"] = true, ["ban_duration"] = 0 })).Single().RetCode);
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, role: GroupRole.Member));
        var denied = await protocol.HandleQuickOperationAsync(wire, new JsonObject { ["ban"] = true });
        Assert.AreEqual("set_group_anonymous_ban", denied.Single().Action);
        Assert.AreEqual(1403, denied.Single().RetCode);
        var parameters = GroupParameters();
        parameters["anonymous"] = wire["anonymous"]!.DeepClone();
        Assert.AreEqual(1403, (await protocol.HandleAsync(new ProtocolCall("set_group_anonymous_ban", parameters))).RetCode);
    }

    [TestMethod]
    public async Task V11HttpAnonymousToggleCqSendAndFlagBanRoundTrip()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = Protocol(fixture);
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        const string token = "anonymous-contract-test";
        await using var session = new ProtocolSession(protocol, fixture.Platform, new ConnectionSettings
        {
            Transport = TransportMode.OneBotHttpServer,
            Host = "127.0.0.1",
            Port = port,
            AccessToken = token,
        }, fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var toggle = await http.GetAsync($"set_group_anonymous?group_id={ProtocolTestFixture.GroupId}&enable=true");
        Assert.AreEqual(HttpStatusCode.OK, toggle.StatusCode);
        Assert.AreEqual(0, JsonNode.Parse(await toggle.Content.ReadAsStringAsync())!["retcode"]!.GetValue<int>());
        using var send = await http.PostAsync("send_group_msg", new StringContent(new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["message"] = "[CQ:anonymous]http secret",
        }.ToJsonString(), Encoding.UTF8, "application/json"));
        var sent = JsonNode.Parse(await send.Content.ReadAsStringAsync())!;
        Assert.AreEqual(0, sent["retcode"]!.GetValue<int>());
        var message = (await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId))).Single();
        Assert.IsNotNull(message.Anonymous);
        using var ban = await http.PostAsync("set_group_anonymous_ban", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["anonymous_flag"] = message.Anonymous.Flag,
            ["duration"] = "60",
        }));
        Assert.AreEqual(HttpStatusCode.OK, ban.StatusCode);
        Assert.AreEqual(0, JsonNode.Parse(await ban.Content.ReadAsStringAsync())!["retcode"]!.GetValue<int>());
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture, OneBotVersion version = OneBotVersion.V11) =>
        new(version, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static JsonObject GroupParameters() => new() { ["group_id"] = ProtocolTestFixture.GroupId };

    private static JsonArray MessageArray(JsonNode? ignore) =>
    [
        new JsonObject { ["type"] = "anonymous", ["data"] = new JsonObject { ["ignore"] = ignore?.DeepClone() } },
        new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "secret" } },
    ];

    private static async Task<(Message Message, JsonObject Event)> IncomingAsync(ProtocolTestFixture fixture, OneBotProtocol protocol)
    {
        await fixture.Platform.SetGroupAnonymousAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        var message = await fixture.Platform.SendMessageAsync(ChatScene.Group, ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new AnonymousSegment(), new TextSegment("anonymous content")]);
        var wire = (await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId, new MessageEvent(message)))).Single().Payload;
        return (message, wire);
    }
}
