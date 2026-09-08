using System.Security.Cryptography;
using System.Net;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotFileTests
{
    [TestMethod]
    public async Task V12UploadDownloadAndFileMessageRoundTrip()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        byte[] bytes = [0, 1, 2, 253, 254, 255];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var uploaded = await protocol.HandleAsync(new ProtocolCall("upload_file", new JsonObject
        {
            ["type"] = "data",
            ["name"] = "sample.bin",
            ["data"] = Convert.ToBase64String(bytes),
            ["sha256"] = hash,
        }));
        Assert.IsTrue(uploaded.IsSuccess, uploaded.Message);
        var id = uploaded.Data!["file_id"]!.GetValue<string>();
        var downloaded = await protocol.HandleAsync(new ProtocolCall("get_file", new JsonObject { ["file_id"] = id, ["type"] = "data" }));
        Assert.IsTrue(downloaded.IsSuccess, downloaded.Message);
        Assert.AreEqual("sample.bin", downloaded.Data!["name"]!.GetValue<string>());
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(downloaded.Data["data"]!.GetValue<string>()));
        Assert.AreEqual(hash, downloaded.Data["sha256"]!.GetValue<string>());
        var url = await protocol.HandleAsync(new ProtocolCall("get_file", new JsonObject { ["file_id"] = id, ["type"] = "url" }));
        var downloadUrl = new Uri(url.Data!["url"]!.GetValue<string>());
        Assert.AreEqual($"/assets/{id}", downloadUrl.AbsolutePath);
        var grant = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(downloadUrl.Query)[AssetStore.DownloadTokenQueryParameter].Single();
        Assert.IsTrue(fixture.Assets.ValidateAssetDownloadToken(id, grant));
        var path = await protocol.HandleAsync(new ProtocolCall("get_file", new JsonObject { ["file_id"] = id, ["type"] = "path" }));
        Assert.AreEqual(fixture.Assets.LocationOf(id), path.Data!["path"]!.GetValue<string>());

        var sent = await protocol.HandleAsync(new ProtocolCall("send_message", new JsonObject
        {
            ["detail_type"] = "group",
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["message"] = new JsonArray(new JsonObject { ["type"] = "file", ["data"] = new JsonObject { ["file_id"] = id } }),
        }));
        Assert.IsTrue(sent.IsSuccess, sent.Message);
        var message = await fixture.Store.GetMessageAsync(sent.Data!["message_id"]!.GetValue<string>());
        Assert.AreEqual("sample.bin", ((FileSegment)message!.Content[0]).Asset.Name);
        var actions = await protocol.HandleAsync(new ProtocolCall("get_supported_actions", new JsonObject()));
        Assert.IsTrue(((JsonArray)actions.Data!).Any(node => node!.GetValue<string>() == "upload_file"));
    }

    [TestMethod]
    public async Task V12UploadRejectsInvalidDataHashAndLocalPaths()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        foreach (var (parameters, expected) in new (JsonObject, int)[]
        {
            (new JsonObject { ["type"] = "data", ["name"] = "test", ["data"] = "invalid base64" }, 10003),
            (new JsonObject { ["type"] = "data", ["name"] = "test", ["data"] = "AA==", ["sha256"] = new string('0', 64) }, 35000),
            (new JsonObject { ["type"] = "path", ["name"] = "test", ["path"] = @"C:\Windows\win.ini" }, 10004),
            (new JsonObject { ["type"] = "url", ["name"] = "test", ["url"] = "file:///C:/Windows/win.ini" }, 10003),
            (new JsonObject { ["type"] = "url", ["name"] = "test", ["url"] = "https://example.com/a", ["headers"] = new JsonObject { ["Authorization"] = 7 } }, 10003),
            (new JsonObject { ["type"] = "url", ["name"] = "test", ["url"] = "https://example.com/a", ["headers"] = new JsonObject { ["Host"] = "localhost" } }, 10003),
            (new JsonObject { ["type"] = "data", ["name"] = "test", ["data"] = "AA==", ["sha256"] = 0 }, 10003),
        })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("upload_file", parameters));
            Assert.AreEqual(expected, reply.RetCode, reply.Message);
        }
    }

    [TestMethod]
    public async Task V12UrlUploadSendsCustomHeadersAndPreservesNameAndChecksum()
    {
        await using var fixture = new ProtocolTestFixture();
        using var handler = new FileDownloadHandler();
        using var assets = new AssetStore(fixture.Assets.DirectoryPath, handler);
        using var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId,
            fixture.Platform, new MediaService(fixture.Store, assets));
        var reply = await protocol.HandleAsync(new ProtocolCall("upload_file", new JsonObject
        {
            ["type"] = "url",
            ["name"] = "chosen-name.bin",
            ["url"] = "https://example.com/original.bin",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer file-token", ["X-File-Key"] = "file-key" },
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(FileDownloadHandler.Bytes)),
        }));
        Assert.IsTrue(reply.IsSuccess, reply.Message);
        Assert.AreEqual("Bearer file-token", handler.Authorization);
        Assert.AreEqual("file-key", handler.FileKey);
        var asset = await fixture.Store.GetAssetAsync(reply.Data!["file_id"]!.GetValue<string>());
        Assert.AreEqual("chosen-name.bin", asset!.Name);
        Assert.AreEqual("https://example.com/original.bin", asset.Source!.Value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task V12UrlChecksumFailureDoesNotLeaveAnOrphanOrDeleteSharedAsset(bool alreadyStored)
    {
        await using var fixture = new ProtocolTestFixture();
        using var assets = new AssetStore(fixture.Assets.DirectoryPath, new FileDownloadHandler());
        using var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId,
            fixture.Platform, new MediaService(fixture.Store, assets));
        var id = Convert.ToHexStringLower(SHA256.HashData(FileDownloadHandler.Bytes));
        if (alreadyStored)
        {
            var existing = await assets.StoreAsync(FileDownloadHandler.Bytes, "existing.bin");
            await fixture.Store.SaveAsync(existing);
        }

        var reply = await protocol.HandleAsync(new ProtocolCall("upload_file", new JsonObject
        {
            ["type"] = "url",
            ["name"] = "rejected.bin",
            ["url"] = "https://example.com/original.bin",
            ["sha256"] = new string('0', 64),
        }));
        Assert.AreEqual(35000, reply.RetCode, reply.Message);
        Assert.HasCount(alreadyStored ? 1 : 0, Directory.GetFiles(assets.DirectoryPath));
        var metadata = await fixture.Store.GetAssetAsync(id);
        if (alreadyStored)
        {
            Assert.AreEqual("existing.bin", metadata!.Name);
            CollectionAssert.AreEqual(FileDownloadHandler.Bytes, await assets.GetBytesAsync(id));
        }
        else
        {
            Assert.IsNull(metadata);
            Assert.IsFalse(assets.Exists(id));
        }
    }

    [TestMethod]
    [DataRow(false, 32000)]
    [DataRow(true, 33000)]
    public async Task V12UrlUploadClassifiesFilesystemAndNetworkFailures(bool networkFailure, int expectedCode)
    {
        await using var fixture = new ProtocolTestFixture();
        using var assets = new AssetStore(fixture.Assets.DirectoryPath, new FailingDownloadHandler(networkFailure));
        using var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId,
            fixture.Platform, new MediaService(fixture.Store, assets));
        var reply = await protocol.HandleAsync(new ProtocolCall("upload_file", new JsonObject
        {
            ["type"] = "url",
            ["name"] = "failed.bin",
            ["url"] = "https://example.com/original.bin",
        }));
        Assert.AreEqual(expectedCode, reply.RetCode, reply.Message);
        Assert.IsEmpty(Directory.GetFiles(assets.DirectoryPath));
    }

    private sealed class FailingDownloadHandler(bool networkFailure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(networkFailure
                ? new HttpRequestException("Connection failed") : new IOException("File write failed"));
    }

    private sealed class FileDownloadHandler : HttpMessageHandler
    {
        internal static readonly byte[] Bytes = [1, 2, 3, 4];
        internal string? Authorization { get; private set; }
        internal string? FileKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            FileKey = request.Headers.TryGetValues("X-File-Key", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Bytes),
            });
        }
    }
}
