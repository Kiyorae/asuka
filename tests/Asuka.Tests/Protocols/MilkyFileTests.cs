using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Contract: https://milky.ntqqrev.org/api/file and GroupFileEntity/GroupFolderEntity (1.3).
[TestClass]
public sealed class MilkyFileTests
{
    [TestMethod]
    public async Task PublishedDownloadUrlServesTheUploadedBytesThroughTheMilkyService()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, role: GroupRole.Owner));
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, role: GroupRole.Admin));
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint("127.0.0.1", port);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = "127.0.0.1",
            Port = port,
            AccessToken = "file-test-token",
        }, fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "file-test-token");
        using var body = new StringContent(Request("upload_group_file", ("file_uri", "base64://AQID"),
            ("file_name", "download.txt")).Parameters.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/api/upload_group_file", body);
        var upload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("ok", upload["status"]!.GetValue<string>());
        var url = await Call(protocol, "get_group_file_download_url", ("file_id", upload["data"]!["file_id"]!.GetValue<string>()));
        Assert.AreEqual(0, (await Call(protocol, "get_group_files"))["files"]![0]!["downloaded_times"]!.GetValue<int>());
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.Changed += (_, args) =>
        {
            if ((args.Changes & StoreChangeKind.Files) != 0) recorded.TrySetResult();
        };
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(url["download_url"]!.GetValue<string>()));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, (await Call(protocol, "get_group_files"))["files"]![0]!["downloaded_times"]!.GetValue<int>());
        StringAssert.StartsWith(new Uri(url["download_url"]!.GetValue<string>()).AbsolutePath, "/files/");
        await Call(protocol, "delete_group_file", ("file_id", upload["data"]!["file_id"]!.GetValue<string>()));
        using var deletedResponse = await http.GetAsync(url["download_url"]!.GetValue<string>());
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, deletedResponse.StatusCode);
        var second = await Call(protocol, "upload_group_file", ("file_uri", "base64://BAUG"), ("file_name", "membership.txt"));
        var secondUrl = await Call(protocol, "get_group_file_download_url", ("file_id", second["file_id"]!.GetValue<string>()));
        await fixture.Platform.RemoveMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId);
        using var departedResponse = await http.GetAsync(secondUrl["download_url"]!.GetValue<string>());
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, departedResponse.StatusCode);
    }

    [TestMethod]
    public async Task GroupFileAndFolderLifecycleUsesOfficialIdentifiersAndMetadata()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var created = await Call(protocol, "create_group_folder", ("folder_name", "Documents"));
        var folderId = created["folder_id"]!.GetValue<string>();
        var uploaded = await Call(protocol, "upload_group_file", ("file_uri", "base64://AQID"), ("file_name", "测试.txt"));
        var fileId = uploaded["file_id"]!.GetValue<string>();
        var duplicate = await Call(protocol, "upload_group_file", ("file_uri", "base64://AQID"), ("file_name", "copy.txt"));
        Assert.AreNotEqual(fileId, duplicate["file_id"]!.GetValue<string>());
        var root = await Call(protocol, "get_group_files");
        var file = root["files"]!.AsArray().Single(x => x!["file_id"]!.GetValue<string>() == fileId)!;
        Assert.AreEqual("测试.txt", file["file_name"]!.GetValue<string>());
        Assert.AreEqual(3L, file["file_size"]!.GetValue<long>());
        Assert.AreEqual("/", file["parent_folder_id"]!.GetValue<string>());
        Assert.AreEqual(long.Parse(ProtocolTestFixture.SelfId, System.Globalization.CultureInfo.InvariantCulture), file["uploader_id"]!.GetValue<long>());
        Assert.IsGreaterThan(0L, file["uploaded_time"]!.GetValue<long>());
        Assert.IsTrue(file.AsObject().ContainsKey("expire_time"));
        Assert.AreEqual(0, file["downloaded_times"]!.GetValue<int>());
        await Call(protocol, "move_group_file", ("file_id", fileId), ("target_folder_id", folderId));
        await Call(protocol, "rename_group_file", ("file_id", fileId), ("parent_folder_id", folderId), ("new_file_name", "renamed.txt"));
        await Call(protocol, "persist_group_file", ("file_id", fileId));
        var nested = await Call(protocol, "get_group_files", ("parent_folder_id", folderId));
        Assert.AreEqual("renamed.txt", nested["files"]![0]!["file_name"]!.GetValue<string>());
        Assert.IsNull(nested["files"]![0]!["expire_time"]);
        var url = await Call(protocol, "get_group_file_download_url", ("file_id", fileId));
        Assert.IsTrue(Uri.TryCreate(url["download_url"]!.GetValue<string>(), UriKind.Absolute, out _));
        await Call(protocol, "rename_group_folder", ("folder_id", folderId), ("new_folder_name", "Archive"));
        root = await Call(protocol, "get_group_files");
        var folder = root["folders"]![0]!;
        Assert.AreEqual("Archive", folder["folder_name"]!.GetValue<string>());
        Assert.AreEqual(1, folder["file_count"]!.GetValue<int>());
        Assert.AreEqual("/", folder["parent_folder_id"]!.GetValue<string>());
        Assert.IsGreaterThanOrEqualTo(folder["created_time"]!.GetValue<long>(), folder["last_modified_time"]!.GetValue<long>());
        await Call(protocol, "delete_group_folder", ("folder_id", folderId));
        Assert.IsFalse((await protocol.HandleAsync(Request("get_group_file_download_url", ("file_id", fileId)))).IsSuccess);
        Assert.HasCount(1, (await Call(protocol, "get_group_files"))["files"]!.AsArray());
        await Call(protocol, "delete_group_file", ("file_id", duplicate["file_id"]!.GetValue<string>()));
        Assert.IsEmpty((await Call(protocol, "get_group_files"))["files"]!.AsArray());
        Assert.IsEmpty((await Call(protocol, "get_group_files"))["folders"]!.AsArray());
    }

    [TestMethod]
    public async Task InvalidFileRequestsCannotReadLocalPathsOrCrossGroupBoundaries()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var local = await protocol.HandleAsync(Request("upload_group_file", ("file_uri", "file:///C:/Windows/win.ini"), ("file_name", "local.txt")));
        Assert.AreEqual(-500, local.RetCode);
        StringAssert.Contains(local.Message, "base64://");
        foreach (var uri in new[] { "file:///C:/Windows/win.ini", "C:\\Windows\\win.ini", "\\\\localhost\\share\\secret.txt" })
        {
            Assert.IsFalse((await protocol.HandleAsync(Request("upload_group_file", ("file_uri", uri), ("file_name", "local.txt")))).IsSuccess);
        }

        Assert.IsFalse((await protocol.HandleAsync(Request("upload_group_file", ("file_uri", "base64://AQID"),
            ("file_name", "a.txt"), ("parent_folder_id", "missing")))).IsSuccess);
        var upload = await Call(protocol, "upload_group_file", ("file_uri", "base64://AQID"), ("file_name", "valid.txt"));
        var fileId = upload["file_id"]!.GetValue<string>();
        await fixture.Store.SaveAsync(new Group("Other", id: "500000002"));
        await fixture.Store.SaveAsync(new GroupMember("500000002", ProtocolTestFixture.SelfId, role: GroupRole.Owner));
        foreach (var api in new[] { "delete_group_file", "persist_group_file", "get_group_file_download_url" })
        {
            var crossGroup = Request(api, ("file_id", fileId));
            crossGroup.Parameters["group_id"] = "500000002";
            Assert.IsFalse((await protocol.HandleAsync(crossGroup)).IsSuccess);
        }

        Assert.IsFalse((await protocol.HandleAsync(Request("rename_group_file", ("file_id", fileId),
            ("parent_folder_id", "wrong"), ("new_file_name", "changed.txt")))).IsSuccess);
        Assert.AreEqual("valid.txt", (await Call(protocol, "get_group_files"))["files"]![0]!["file_name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PrivateDownloadsValidatePeerHashAndSendDirection()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.AddFriendshipAsync(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
        fixture.Platform.SetEchoesSelfEvents(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Platform.Events(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = events.MoveNextAsync().AsTask();
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var upload = await protocol.HandleAsync(new ProtocolCall("upload_private_file", new JsonObject
        {
            ["user_id"] = ProtocolTestFixture.SenderId,
            ["file_name"] = "private.txt",
            ["file_uri"] = "base64://AQID",
        }));
        Assert.IsTrue(upload.IsSuccess, upload.Message);
        Assert.IsTrue(await next);
        var frames = await protocol.EncodeAsync(events.Current);
        var notice = frames.Single().Payload;
        Assert.AreEqual("friend_file_upload", notice["event_type"]!.GetValue<string>());
        var data = notice["data"]!;
        Assert.IsTrue(data["is_self"]!.GetValue<bool>());
        Assert.AreEqual("private.txt", data["file_name"]!.GetValue<string>());
        var request = new ProtocolCall("get_private_file_download_url", new JsonObject
        {
            ["user_id"] = ProtocolTestFixture.SenderId,
            ["file_id"] = upload.Data!["file_id"]!.DeepClone(),
            ["file_hash"] = data["file_hash"]!.DeepClone(),
            ["is_self_send"] = true,
        });
        Assert.IsTrue((await protocol.HandleAsync(request)).IsSuccess);
        request.Parameters.Remove("is_self_send");
        Assert.IsFalse((await protocol.HandleAsync(request)).IsSuccess);
        request.Parameters["is_self_send"] = true;
        request.Parameters["file_hash"] = "wrong";
        Assert.IsFalse((await protocol.HandleAsync(request)).IsSuccess);
    }

    private static ProtocolCall Request(string api, params (string Name, string Value)[] values)
    {
        var args = new JsonObject { ["group_id"] = ProtocolTestFixture.GroupId };
        foreach (var (name, value) in values) args[name] = value;
        return new ProtocolCall(api, args);
    }

    private static async Task<JsonNode> Call(MilkyProtocol protocol, string api, params (string Name, string Value)[] values)
    {
        var reply = await protocol.HandleAsync(Request(api, values));
        Assert.IsTrue(reply.IsSuccess, $"{api}: {reply.Message}");
        return reply.EffectiveData;
    }
}
