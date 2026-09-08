using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class DownloadGrantHttpTests
{
    private const string Bearer = "global-download-test-secret";

    [TestMethod]
    public async Task AuthenticatedOneBotFileUrlDownloadsWithAFreshClientAndOnlyAuthorizesItsAsset()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var first = await fixture.Assets.StoreAsync(new byte[] { 1, 2, 3 }, "first.bin");
        var other = await fixture.Assets.StoreAsync(new byte[] { 4, 5, 6 }, "other.bin");
        await fixture.Store.SaveAsync(first);
        await fixture.Store.SaveAsync(other);
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint("127.0.0.1", port);
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port, TransportMode.WebSocketServer), fixture.Assets);
        var diagnostic = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TrafficObserved += (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.Reply) diagnostic.TrySetResult(entry.Payload);
        };
        await session.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Bearer}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
        var request = new JsonObject
        {
            ["action"] = "get_file",
            ["params"] = new JsonObject { ["file_id"] = first.Id, ["type"] = "url" },
            ["echo"] = "download-grant",
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token);
        JsonObject reply;
        do { reply = await ReceiveAsync(socket, timeout.Token); }
        while (reply["echo"]?.GetValue<string>() != "download-grant");
        Assert.AreEqual("ok", reply["status"]!.GetValue<string>());
        var url = new Uri(reply["data"]!["url"]!.GetValue<string>());
        var token = Token(url);
        Assert.IsTrue(fixture.Assets.ValidateAssetDownloadToken(first.Id, token));
        Assert.DoesNotContain(Bearer, url.AbsoluteUri);
        Assert.DoesNotContain(token, (await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToJsonString());

        using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await fresh.GetByteArrayAsync(url));
        using var unsigned = await fresh.GetAsync(new UriBuilder(url) { Query = "" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var globalQuery = await fresh.GetAsync(new UriBuilder(url) { Query = $"access_token={Bearer}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, globalQuery.StatusCode);
        using var crossAsset = await fresh.GetAsync(new UriBuilder(url) { Path = $"/assets/{other.Id}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, crossAsset.StatusCode);
        using var duplicate = await fresh.GetAsync(url.AbsoluteUri + $"&download_token={token}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        var expired = fixture.Assets.CreateAssetDownloadToken(first.Id, DateTimeOffset.UtcNow.AddMinutes(-6));
        using var expiry = await fresh.GetAsync(new UriBuilder(url) { Query = $"download_token={expired}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, expiry.StatusCode);
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, url);
        browserRequest.Headers.Add("Origin", "https://example.com");
        using var browser = await fresh.SendAsync(browserRequest);
        Assert.AreEqual(HttpStatusCode.Forbidden, browser.StatusCode);
        using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(url) { Query = "" }.Uri);
        fallbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        using var fallback = await fresh.SendAsync(fallbackRequest);
        Assert.AreEqual(HttpStatusCode.OK, fallback.StatusCode);
        foreach (var malformed in new[] { "", "not-a-cached-id", new string('g', 64) })
        {
            using var malformedRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/assets/{malformed}");
            malformedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
            using var malformedResponse = await fresh.SendAsync(malformedRequest);
            Assert.AreEqual(HttpStatusCode.NotFound, malformedResponse.StatusCode);
        }
    }

    [TestMethod]
    public async Task MilkySharedGrantsRemainAccountBoundAndRevalidateExpiryDeletionAndMembership()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        // The downloading account can leave while a different member owns the group.
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, role: GroupRole.Owner));
        await fixture.Store.SaveAsync(new GroupMember(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, role: GroupRole.Admin));
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var otherPort = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint("127.0.0.1", port);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var otherProtocol = new MilkyProtocol(ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port, TransportMode.MilkyService), fixture.Assets);
        await using var otherSession = new ProtocolSession(otherProtocol, fixture.Platform, Settings(otherPort, TransportMode.MilkyService), fixture.Assets);
        await session.StartAsync();
        await otherSession.StartAsync();
        using var authenticated = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var first = await UploadAsync(authenticated, port, "expire.bin");
        var firstUrl = await FileUrlAsync(authenticated, port, first);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await fresh.GetByteArrayAsync(firstUrl));
        Assert.DoesNotContain(Bearer, firstUrl.AbsoluteUri);
        using var crossAccount = await fresh.GetAsync(new UriBuilder(firstUrl) { Port = otherPort }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, crossAccount.StatusCode);
        using var unsigned = await fresh.GetAsync(new UriBuilder(firstUrl) { Query = "" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var file = (await fixture.Store.GetSharedFileAsync(first))!;
        using var crossKind = await fresh.GetAsync(new UriBuilder(firstUrl) { Path = $"/assets/{file.Asset.Id}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, crossKind.StatusCode);
        using var bypassApi = await fresh.PostAsync($"http://127.0.0.1:{port}/api/get_login_info?download_token={Token(firstUrl)}", JsonBody(new JsonObject()));
        Assert.AreEqual(HttpStatusCode.Unauthorized, bypassApi.StatusCode);
        await fixture.Store.SaveSharedFileAsync(file with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        using var expired = await fresh.GetAsync(firstUrl);
        Assert.AreEqual(HttpStatusCode.NotFound, expired.StatusCode);

        var second = await UploadAsync(authenticated, port, "delete.bin");
        var secondUrl = await FileUrlAsync(authenticated, port, second);
        using var crossFile = await fresh.GetAsync(new UriBuilder(firstUrl) { Path = $"/files/{second}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, crossFile.StatusCode);
        await CallMilkyAsync(authenticated, port, "delete_group_file", GroupParameters(second));
        using var deleted = await fresh.GetAsync(secondUrl);
        Assert.AreEqual(HttpStatusCode.NotFound, deleted.StatusCode);

        var third = await UploadAsync(authenticated, port, "depart.bin");
        var thirdUrl = await FileUrlAsync(authenticated, port, third);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await fresh.GetByteArrayAsync(thirdUrl));
        await fixture.Platform.RemoveMemberAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, ProtocolTestFixture.SelfId);
        using var departed = await fresh.GetAsync(thirdUrl);
        Assert.AreEqual(HttpStatusCode.NotFound, departed.StatusCode);
    }

    private static ConnectionSettings Settings(ushort port, TransportMode transport) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Transport = transport,
        AccessToken = Bearer,
    };

    private static string Token(Uri uri) =>
        Uri.UnescapeDataString(uri.Query[1..].Split('&').Single(part => part.StartsWith("download_token=", StringComparison.Ordinal))[15..]);

    private static async Task<string> UploadAsync(HttpClient http, ushort port, string name)
    {
        var parameters = GroupParameters();
        parameters["file_uri"] = "base64://AQID";
        parameters["file_name"] = name;
        return (await CallMilkyAsync(http, port, "upload_group_file", parameters))["file_id"]!.GetValue<string>();
    }

    private static async Task<Uri> FileUrlAsync(HttpClient http, ushort port, string fileId) =>
        new((await CallMilkyAsync(http, port, "get_group_file_download_url", GroupParameters(fileId)))["download_url"]!.GetValue<string>());

    private static JsonObject GroupParameters(string? fileId = null)
    {
        var result = new JsonObject { ["group_id"] = 500_000_001L };
        if (fileId is not null) result["file_id"] = fileId;
        return result;
    }

    private static StringContent JsonBody(JsonObject parameters) => new(parameters.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> CallMilkyAsync(HttpClient http, ushort port, string action, JsonObject parameters)
    {
        using var body = JsonBody(parameters);
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/api/{action}", body);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("ok", envelope["status"]!.GetValue<string>());
        return envelope["data"]!.AsObject();
    }

    private static async Task<JsonObject> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var bytes = new byte[16 * 1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes.AsMemory(), cancellationToken);
            Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            await stream.WriteAsync(bytes.AsMemory(0, result.Count), cancellationToken);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
}
