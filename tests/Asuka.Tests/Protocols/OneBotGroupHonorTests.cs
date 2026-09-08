using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Official contracts: https://github.com/botuniverse/onebot-11/blob/master/api/public.md#get_group_honor_info-获取群荣誉信息
// and https://github.com/botuniverse/onebot-11/blob/master/event/notice.md (honor and lucky_king).
[TestClass]
public sealed class OneBotGroupHonorTests
{
    [TestMethod]
    [DataRow("talkative", 3)]
    [DataRow("performer", 2)]
    [DataRow("legend", 2)]
    [DataRow("strong_newbie", 2)]
    [DataRow("emotion", 2)]
    [DataRow("all", 7)]
    public async Task QueryReturnsOnlyRequestedTypesWithExactOfficialFields(string requestedType, int fieldCount)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync((await fixture.Store.GetUserAsync(ProtocolTestFixture.SenderId))! with { Avatar = "https://example.com/alice.png" });
        foreach (var type in Enum.GetValues<GroupHonorType>())
            await fixture.Platform.SetGroupHonorAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, type,
                ProtocolTestFixture.SelfId, $"{type} description", 9);
        var protocol = V11(fixture);
        var reply = await protocol.HandleAsync(Call(requestedType));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        var data = reply.Data!.AsObject();
        Assert.HasCount(fieldCount, data);
        Assert.AreEqual(500_000_001L, data["group_id"]!.GetValue<long>());
        foreach (var (key, model) in new[]
        {
            ("talkative", GroupHonorType.Talkative), ("performer", GroupHonorType.Performer),
            ("legend", GroupHonorType.Legend), ("strong_newbie", GroupHonorType.StrongNewbie), ("emotion", GroupHonorType.Emotion),
        })
        {
            if (requestedType != "all" && requestedType != key)
            {
                Assert.IsFalse(data.ContainsKey(key + "_list"));
                continue;
            }
            var list = data[key + "_list"]!.AsArray();
            Assert.HasCount(1, list);
            var entry = list[0]!.AsObject();
            Assert.HasCount(4, entry);
            Assert.AreEqual(1_000_000_002L, entry["user_id"]!.GetValue<long>());
            Assert.AreEqual("Alice", entry["nickname"]!.GetValue<string>());
            Assert.AreEqual("https://example.com/alice.png", entry["avatar"]!.GetValue<string>());
            Assert.AreEqual($"{model} description", entry["description"]!.GetValue<string>());
        }
        if (requestedType is "all" or "talkative")
        {
            var current = data["current_talkative"]!.AsObject();
            Assert.HasCount(4, current);
            Assert.AreEqual(1_000_000_002L, current["user_id"]!.GetValue<long>());
            Assert.AreEqual("Alice", current["nickname"]!.GetValue<string>());
            Assert.AreEqual("https://example.com/alice.png", current["avatar"]!.GetValue<string>());
            Assert.AreEqual(9, current["day_count"]!.GetValue<int>());
        }
        else Assert.IsFalse(data.ContainsKey("current_talkative"));
    }

    [TestMethod]
    public async Task EmptyHonorsAreRealEmptyStateAndDoNotExposeLocalAvatarPaths()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = V11(fixture);
        var empty = (await protocol.HandleAsync(Call("all"))).Data!.AsObject();
        Assert.IsTrue(empty.ContainsKey("current_talkative"));
        Assert.IsNull(empty["current_talkative"]);
        foreach (var key in new[] { "talkative_list", "performer_list", "legend_list", "strong_newbie_list", "emotion_list" })
            Assert.IsEmpty(empty[key]!.AsArray());
        await fixture.Store.SaveAsync((await fixture.Store.GetUserAsync(ProtocolTestFixture.SenderId))! with { Avatar = @"C:\private\avatar.png" });
        await fixture.Platform.SetGroupHonorAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId,
            GroupHonorType.Talkative, ProtocolTestFixture.SelfId);
        var populated = (await protocol.HandleAsync(Call("talkative"))).Data!;
        Assert.AreEqual(string.Empty, populated["current_talkative"]!["avatar"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, populated["talkative_list"]![0]!["avatar"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task QueryRejectsInvalidTypesAndGroupIdsInsteadOfReturningFakeSuccess()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = V11(fixture);
        foreach (var json in new[]
        {
            "{}", "{\"group_id\":500000001}", "{\"group_id\":500000001,\"type\":null}",
            "{\"group_id\":500000001,\"type\":3}", "{\"group_id\":500000001,\"type\":{}}",
            "{\"group_id\":500000001,\"type\":\"ALL\"}", "{\"group_id\":500000001,\"type\":\"\"}",
            "{\"group_id\":500000001,\"type\":\"lucky_king\"}", "{\"group_id\":0,\"type\":\"all\"}",
            "{\"group_id\":-1,\"type\":\"all\"}", "{\"group_id\":true,\"type\":\"all\"}",
            "{\"group_id\":500000001.5,\"type\":\"all\"}", "{\"group_id\":{},\"type\":\"all\"}",
        })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("get_group_honor_info", JsonNode.Parse(json)!.AsObject()));
            Assert.AreEqual(1400, reply.RetCode, json);
        }
        var missing = Call("all") with { Parameters = new JsonObject { ["group_id"] = 111L, ["type"] = "all" } };
        Assert.IsFalse((await protocol.HandleAsync(missing)).IsSuccess);
        await fixture.Store.RemoveMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        Assert.IsFalse((await protocol.HandleAsync(Call("all"))).IsSuccess);
    }

    [TestMethod]
    [DataRow(GroupHonorType.Talkative, "talkative")]
    [DataRow(GroupHonorType.Performer, "performer")]
    [DataRow(GroupHonorType.Emotion, "emotion")]
    public async Task HonorNoticeHasOnlyOfficialFieldsAndIsV11Only(GroupHonorType type, string wireType)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var domain = new DomainEvent(ProtocolTestFixture.SelfId,
            new GroupHonorChangedEvent(new GroupHonorChange(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, type)),
            time: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var frames = await V11(fixture).EncodeAsync(domain);
        Assert.HasCount(1, frames);
        var data = frames[0].Payload;
        Assert.HasCount(8, data);
        Assert.AreEqual(1_700_000_000L, data["time"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, data["self_id"]!.GetValue<long>());
        Assert.AreEqual("notice", data["post_type"]!.GetValue<string>());
        Assert.AreEqual("notify", data["notice_type"]!.GetValue<string>());
        Assert.AreEqual("honor", data["sub_type"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, data["group_id"]!.GetValue<long>());
        Assert.AreEqual(wireType, data["honor_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_002L, data["user_id"]!.GetValue<long>());
        Assert.IsEmpty(await V11(fixture).EncodeAsync(domain with { SelfId = ProtocolTestFixture.SenderId }));
        Assert.IsEmpty(await new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media).EncodeAsync(domain));
        Assert.IsEmpty(await new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media).EncodeAsync(domain));
    }

    [TestMethod]
    public async Task OtherHonorCategoriesHaveNoInventedNoticeAndLuckyKingKeepsSenderAndWinnerDistinct()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = V11(fixture);
        foreach (var type in new[] { GroupHonorType.Legend, GroupHonorType.StrongNewbie, (GroupHonorType)99 })
            Assert.IsEmpty(await protocol.EncodeAsync(new DomainEvent(ProtocolTestFixture.SelfId,
                new GroupHonorChangedEvent(new GroupHonorChange(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, type)))));
        var lucky = new DomainEvent(ProtocolTestFixture.SelfId, new GroupLuckyKingEvent(
            new GroupLuckyKing(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId)));
        var data = (await protocol.EncodeAsync(lucky)).Single().Payload;
        Assert.HasCount(8, data);
        Assert.AreEqual("notice", data["post_type"]!.GetValue<string>());
        Assert.AreEqual("notify", data["notice_type"]!.GetValue<string>());
        Assert.AreEqual("lucky_king", data["sub_type"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, data["group_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_001L, data["user_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, data["target_id"]!.GetValue<long>());
        var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        Assert.IsEmpty(await v12.EncodeAsync(lucky));
        Assert.AreEqual(10002, (await v12.HandleAsync(Call("all"))).RetCode);
        Assert.IsEmpty(await new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media).EncodeAsync(lucky));
    }

    private static ProtocolCall Call(string type) => new("get_group_honor_info",
        new JsonObject { ["group_id"] = 500_000_001L, ["type"] = type });

    [TestMethod]
    public async Task WebSocketSessionPublishesAwardsAndReturnsTheAlreadyCommittedHonorState()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var settings = new ConnectionSettings
        {
            Port = ProtocolTestFixture.ReserveEphemeralPort(),
            AccessToken = "honor-test-token",
        };
        await using var session = new ProtocolSession(V11(fixture), fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer honor-test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(settings.BuildWebSocketUri(), timeout.Token);
        Assert.AreEqual("lifecycle", (await ReadSocketAsync(socket, timeout.Token))["meta_event_type"]!.GetValue<string>());
        await fixture.Platform.SetGroupHonorAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId,
            GroupHonorType.Talkative, ProtocolTestFixture.SelfId, "Session dragon", 12, timeout.Token);
        var notice = await ReadSocketAsync(socket, timeout.Token);
        Assert.AreEqual("honor", notice["sub_type"]!.GetValue<string>());
        Assert.AreEqual("talkative", notice["honor_type"]!.GetValue<string>());
        await socket.SendAsync(Encoding.UTF8.GetBytes("""
            {"action":"get_group_honor_info","params":{"group_id":500000001,"type":"talkative"},"echo":"honor-query"}
            """).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var query = await ReadSocketAsync(socket, timeout.Token);
        Assert.AreEqual("honor-query", query["echo"]!.GetValue<string>());
        Assert.AreEqual(0, query["retcode"]!.GetValue<int>());
        Assert.AreEqual(12, query["data"]!["current_talkative"]!["day_count"]!.GetValue<int>());
        Assert.AreEqual(notice["user_id"]!.GetValue<long>(), query["data"]!["current_talkative"]!["user_id"]!.GetValue<long>());
        await fixture.Platform.PublishGroupLuckyKingAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId,
            ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, timeout.Token);
        var lucky = await ReadSocketAsync(socket, timeout.Token);
        Assert.AreEqual("lucky_king", lucky["sub_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_001L, lucky["user_id"]!.GetValue<long>());
        Assert.AreEqual(1_000_000_002L, lucky["target_id"]!.GetValue<long>());
        await session.StopAsync().WaitAsync(timeout.Token);
    }

    private static async Task<JsonObject> ReadSocketAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            await body.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(body.ToArray())!.AsObject();
    }

    private static OneBotProtocol V11(ProtocolTestFixture fixture) =>
        new(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
}
