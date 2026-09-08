using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class SignedDownloadSessionTests
{
    private const string Bearer = "signed-download-session-secret";

    // DownloadGrantHttpTests covers OneBot query authentication plus shared-file
    // deletion, expiry and membership. These exercise the other HTTP route branches.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MilkyDownloadRoutesRejectRepeatedTokenParametersInEveryOrder(bool sharedFile)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var session = CreateSession(fixture, "milky");
        await session.StartAsync();
        var url = await DownloadUrlAsync(fixture, sharedFile, [1, 2, 3]);
        var token = Uri.UnescapeDataString(url.Query[(url.Query.IndexOf('=') + 1)..]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var values in new string[][] { [token, token], ["invalid", token], [token, "invalid"] })
        {
            var duplicated = new UriBuilder(url)
            {
                Query = string.Join('&', values.Select(value => $"download_token={Uri.EscapeDataString(value)}")),
            }.Uri;
            using var response = await http.GetAsync(duplicated, HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(url));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task MilkyDownloadOriginIsRejectedEvenWithValidSignatureAndBearer(bool sharedFile, bool useBearer)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var session = CreateSession(fixture, "milky");
        await session.StartAsync();
        var url = await DownloadUrlAsync(fixture, sharedFile, [4, 5, 6]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://example.test");
        if (useBearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await http.GetByteArrayAsync(url));
    }

    [TestMethod]
    [DataRow("onebot11")]
    [DataRow("onebot12")]
    [DataRow("onebot11-http")]
    [DataRow("onebot12-http")]
    [DataRow("milky")]
    public async Task AuthorizedMalformedAssetIdentifiersReturnNotFoundInsteadOfServerError(string protocol)
    {
        await using var fixture = new ProtocolTestFixture();
        await using var session = CreateSession(fixture, protocol);
        await session.StartAsync();
        // Start with a valid URL solely to retain this fixture's bound listener;
        // the malformed request deliberately uses Bearer rather than a signature.
        var valid = await DownloadUrlAsync(fixture, false, [7, 8, 9]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        foreach (var id in new[] { "not-a-hash", new string('g', 64), new string('a', 63) })
        {
            var invalid = new UriBuilder(valid) { Path = $"/assets/{id}", Query = string.Empty }.Uri;
            using var response = await http.GetAsync(invalid, HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{protocol}: {id}");
        }

        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, await http.GetByteArrayAsync(valid));
    }

    [TestMethod]
    [DataRow("onebot11", false)]
    [DataRow("onebot12", false)]
    [DataRow("milky", false)]
    [DataRow("milky", true)]
    [DataRow("onebot11-http", false)]
    [DataRow("onebot12-http", false)]
    [DataRow("onebot11-http", true)]
    [DataRow("onebot12-http", true)]
    public async Task SignedLargeDownloadsReturnCompleteBinaryContentAndLength(string protocol, bool sharedFile)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var session = CreateSession(fixture, protocol);
        await session.StartAsync();
        var expected = new byte[2 * 1024 * 1024 + 137];
        for (var index = 0; index < expected.Length; index++) expected[index] = (byte)(index % 251);
        var url = await DownloadUrlAsync(fixture, sharedFile, expected);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Assert.IsNull(http.DefaultRequestHeaders.Authorization);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected.LongLength, response.Content.Headers.ContentLength);
        Assert.AreEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[31 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0)
        {
            actualHash.AppendData(buffer, 0, read);
            total += read;
        }

        Assert.AreEqual(expected.LongLength, total);
        CollectionAssert.AreEqual(SHA256.HashData(expected), actualHash.GetHashAndReset());
    }

    [TestMethod]
    [DataRow("onebot11-http", false)]
    [DataRow("onebot12-http", false)]
    [DataRow("onebot11-http", true)]
    [DataRow("onebot12-http", true)]
    public async Task HttpDownloadsRequireBearerOrScopedGrantAndRejectBrowserOrigins(string protocol, bool sharedFile)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var session = CreateSession(fixture, protocol);
        await session.StartAsync();
        var url = await DownloadUrlAsync(fixture, sharedFile, [1, 2, 3]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var rootToken = new UriBuilder(url) { Query = "access_token=" + Bearer }.Uri;
        using var rejected = await http.GetAsync(rootToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var browser = new HttpRequestMessage(HttpMethod.Get, url);
        browser.Headers.Add("Origin", "https://example.test");
        using var originRejected = await http.SendAsync(browser);
        Assert.AreEqual(HttpStatusCode.Forbidden, originRejected.StatusCode);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(url));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await http.GetByteArrayAsync(new UriBuilder(url) { Query = string.Empty }.Uri));
    }

    private static ProtocolSession CreateSession(ProtocolTestFixture fixture, string kind)
    {
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint(IPAddress.Loopback.ToString(), port);
        IProtocolImplementation implementation = kind switch
        {
            "onebot11" or "onebot11-http" => new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media),
            "onebot12" or "onebot12-http" => new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media),
            _ => new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media),
        };
        var settings = new ConnectionSettings
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Transport = kind == "milky" ? TransportMode.MilkyService
                : kind.EndsWith("-http", StringComparison.Ordinal) ? TransportMode.OneBotHttpServer : TransportMode.WebSocketServer,
            AccessToken = Bearer,
        };
        return new ProtocolSession(implementation, fixture.Platform, settings, fixture.Assets);
    }

    private static async Task<Uri> DownloadUrlAsync(ProtocolTestFixture fixture, bool sharedFile, byte[] bytes)
    {
        var asset = await fixture.Assets.StoreAsync(bytes, "signed-session.bin");
        await fixture.Store.SaveAsync(asset);
        if (!sharedFile) return fixture.Media.GetUrl(asset.Id);
        var file = await fixture.Platform.ShareGroupFileAsync(ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId, asset);
        return fixture.Media.GetSharedFileUrl(file.Id, ProtocolTestFixture.SelfId);
    }
}
