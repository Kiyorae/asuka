using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class MilkyGroupNotificationTests
{
    [TestMethod]
    public async Task NotificationApiCombinesAllFiveVariantsAndPreservesHistoryAfterDisband()
    {
        await using var fixture = new ProtocolTestFixture();
        foreach (var id in new[] { "101", "102", "103", "104", "105", "106", "201", "202" })
            await fixture.Platform.SaveUserAsync(new User(id, id));
        await fixture.Platform.CreateGroupAsync(new Group("Notifications", "500"), "101");
        await fixture.Store.SaveAsync(new GroupMember("500", "201", role: GroupRole.Admin));
        await fixture.Store.SaveAsync(new GroupMember("500", "102"));
        await fixture.Store.SaveAsync(new GroupMember("500", "103"));
        fixture.Platform.RegisterBot("201");
        var protocol = new MilkyProtocol("201", fixture.Platform, fixture.Media);
        await fixture.Platform.RequestJoinGroupAsync("500", "104", "201");
        await fixture.Platform.RequestInvitedJoinGroupAsync("500", "102", "105", "201");
        await fixture.Platform.SetAdminAsync("500", "103", "101", true);
        await fixture.Platform.RemoveMemberAsync("500", "102", "101", GroupMemberChangeReason.Administrative);
        await fixture.Platform.RemoveMemberAsync("500", "103", "103");
        await fixture.Platform.RequestJoinGroupAsync("500", "106", "201", isFiltered: true);

        var first = await ReadAsync(protocol, new JsonObject { ["limit"] = 2 });
        var notices = (JsonArray)first["notifications"]!;
        Assert.HasCount(2, notices);
        Assert.AreEqual("quit", notices[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(103L, notices[0]!["target_user_id"]!.GetValue<long>());
        Assert.IsFalse(((JsonObject)notices[0]!).ContainsKey("operator_id"));
        Assert.IsFalse(((JsonObject)notices[0]!).ContainsKey("time"));
        Assert.AreEqual("kick", notices[1]!["type"]!.GetValue<string>());
        Assert.AreEqual(101L, notices[1]!["operator_id"]!.GetValue<long>());
        var second = await ReadAsync(protocol, new JsonObject { ["limit"] = 2, ["start_notification_seq"] = first["next_notification_seq"]!.DeepClone() });
        var middle = (JsonArray)second["notifications"]!;
        Assert.AreEqual("admin_change", middle[0]!["type"]!.GetValue<string>());
        Assert.IsTrue(middle[0]!["is_set"]!.GetValue<bool>());
        Assert.AreEqual("invited_join_request", middle[1]!["type"]!.GetValue<string>());
        var third = await ReadAsync(protocol, new JsonObject { ["limit"] = 2, ["start_notification_seq"] = second["next_notification_seq"]!.DeepClone() });
        Assert.HasCount(1, (JsonArray)third["notifications"]!);
        Assert.AreEqual("join_request", third["notifications"]![0]!["type"]!.GetValue<string>());
        Assert.IsFalse(third.ContainsKey("next_notification_seq"));
        var filtered = await ReadAsync(protocol, new JsonObject { ["is_filtered"] = true });
        Assert.HasCount(1, (JsonArray)filtered["notifications"]!);
        Assert.AreEqual(106L, filtered["notifications"]![0]!["initiator_id"]!.GetValue<long>());
        var other = new MilkyProtocol("202", fixture.Platform, fixture.Media);
        Assert.HasCount(0, (JsonArray)(await ReadAsync(other, new JsonObject()))["notifications"]!);

        await fixture.Platform.DeleteGroupAsync("500", "101");
        var retained = (JsonArray)(await ReadAsync(protocol, new JsonObject()))["notifications"]!;
        Assert.HasCount(5, retained);
        foreach (var request in retained.OfType<JsonObject>().Where(item => item.ContainsKey("state")))
            Assert.AreEqual("ignored", request["state"]!.GetValue<string>());
    }

    private static async Task<JsonObject> ReadAsync(MilkyProtocol protocol, JsonObject parameters)
    {
        var reply = await protocol.HandleAsync(new ProtocolCall("get_group_notifications", parameters));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        return (JsonObject)reply.Data!;
    }
}
