using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Official contract: https://milky.ntqqrev.org/api/system#get_custom_face_url_list
[TestClass]
public sealed class MilkyCustomFaceTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRZsAAAAASUVORK5CYII=";
    private const string Gif = "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
    private const string Secret = "custom-face-api-secret";

    [TestMethod]
    public async Task OfficialGetterReturnsOnlyCurrentAccountsStoredImagesWithUsableSignedUrls()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var platform = new PlatformService(fixture.Store, fixture.Assets);
        var png = await fixture.Assets.StoreAsync(Convert.FromBase64String(Png), "face.png", "image/png");
        var gif = await fixture.Assets.StoreAsync(Convert.FromBase64String(Gif), "other.gif", "image/gif");
        await platform.AddCustomFaceAsync(ProtocolTestFixture.SelfId, png);
        await platform.AddCustomFaceAsync(ProtocolTestFixture.SenderId, gif);
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint("127.0.0.1", port);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, platform, new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            Transport = TransportMode.MilkyService,
            AccessToken = Secret,
        }, fixture.Assets);
        await session.StartAsync();
        using var api = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await api.PostAsync($"http://127.0.0.1:{port}/api/get_custom_face_url_list", body);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("ok", envelope["status"]!.GetValue<string>());
        var urls = envelope["data"]!["urls"]!.AsArray();
        Assert.HasCount(1, urls);
        var uri = new Uri(urls[0]!.GetValue<string>());
        Assert.AreEqual($"/assets/{png.Id}", uri.AbsolutePath);
        Assert.Contains("download_token=", uri.Query);
        Assert.DoesNotContain(Secret, uri.AbsoluteUri);
        using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        CollectionAssert.AreEqual(Convert.FromBase64String(Png), await fresh.GetByteArrayAsync(uri));
        using var unsigned = await fresh.GetAsync(new UriBuilder(uri) { Query = string.Empty }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var differentImage = await fresh.GetAsync(new UriBuilder(uri) { Path = $"/assets/{gif.Id}" }.Uri);
        Assert.AreEqual(HttpStatusCode.Unauthorized, differentImage.StatusCode);

        await platform.RemoveCustomFaceAsync(ProtocolTestFixture.SelfId, png.Id);
        var removed = await protocol.HandleAsync(new ProtocolCall("get_custom_face_url_list", new JsonObject()));
        Assert.IsTrue(removed.IsSuccess, removed.Message);
        Assert.IsEmpty(removed.Data!["urls"]!.AsArray());
        CollectionAssert.AreEqual(Convert.FromBase64String(Png), await fresh.GetByteArrayAsync(uri),
            "Removing the collection entry does not delete a shared asset or revoke an already issued asset grant.");
        var other = new MilkyProtocol(ProtocolTestFixture.SenderId, platform, fixture.Media);
        Assert.HasCount(1, (await other.HandleAsync(new ProtocolCall("get_custom_face_url_list", new JsonObject()))).Data!["urls"]!.AsArray());
    }

    [TestMethod]
    public async Task MissingCachedImageIsOmittedAndUnknownAccountFails()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var platform = new PlatformService(fixture.Store, fixture.Assets);
        var png = await fixture.Assets.StoreAsync(Convert.FromBase64String(Png), "face.png", "image/png");
        await platform.AddCustomFaceAsync(ProtocolTestFixture.SelfId, png);
        File.Delete(fixture.Assets.LocationOf(png.Id));
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, platform, fixture.Media);
        var response = await protocol.HandleAsync(new ProtocolCall("get_custom_face_url_list", new JsonObject()));
        Assert.IsTrue(response.IsSuccess, response.Message);
        Assert.IsEmpty(response.Data!["urls"]!.AsArray());
        Assert.HasCount(1, await platform.GetCustomFacesAsync(ProtocolTestFixture.SelfId),
            "A missing cache entry remains manageable in the local collection.");
        var missing = new MilkyProtocol("999999", platform, fixture.Media);
        Assert.IsFalse((await missing.HandleAsync(new ProtocolCall("get_custom_face_url_list", new JsonObject()))).IsSuccess);
    }
}
