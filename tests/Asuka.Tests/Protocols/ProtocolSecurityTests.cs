using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ProtocolSecurityTests
{
    [TestMethod]
    public async Task MilkyApiAndAssetsRejectBrowserOriginBeforeAuthentication()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            AccessToken = "native-client-token",
        };
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();

        using var http = new HttpClient();
        using var api = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/get_login_info")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "native-client-token");
        api.Headers.TryAddWithoutValidation("Origin", "https://untrusted.example");
        using var apiResponse = await http.SendAsync(api);
        Assert.AreEqual(HttpStatusCode.Forbidden, apiResponse.StatusCode);

        using var asset = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/assets/not-present");
        asset.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "native-client-token");
        asset.Headers.TryAddWithoutValidation("Origin", "https://untrusted.example");
        using var assetResponse = await http.SendAsync(asset);
        Assert.AreEqual(HttpStatusCode.Forbidden, assetResponse.StatusCode);
    }

    [TestMethod]
    public async Task NativeMilkyClientWithoutOriginRemainsCompatible()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            AccessToken = "native-client-token",
        };
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/get_login_info")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "native-client-token");
        using var response = await new HttpClient().SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task OneBotWebSocketRejectsBrowserOrigin()
    {
        await using var fixture = new ProtocolTestFixture();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(
            OneBotVersion.V11,
            ProtocolTestFixture.SelfId,
            fixture.Platform,
            new StubProtocolAssetResolver());
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.WebSocketServer,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            AccessToken = "native-client-token",
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings);
        await session.StartAsync();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://untrusted.example");
        socket.Options.SetRequestHeader("Authorization", "Bearer native-client-token");
        await Assert.ThrowsExactlyAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None));
    }
}
