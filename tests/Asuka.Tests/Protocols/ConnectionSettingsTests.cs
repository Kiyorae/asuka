using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ConnectionSettingsTests
{
    [TestMethod]
    public void OneBotWebhooksKeepTransportSecurityAndRejectInvalidTimeouts()
    {
        var settings = new ConnectionSettings { AccessToken = "fixture-token", OneBotWebhookUrls = ["http://192.0.2.25/event"] };
        Assert.ThrowsExactly<InvalidOperationException>(settings.Validate);
        (settings with { OneBotWebhookUrls = ["http://localhost:8010/events"] }).Validate();
        (settings with { OneBotWebhookUrls = ["https://example.test/events"] }).Validate();
        (settings with { AllowInsecureRemoteAccess = true }).Validate();
        Assert.ThrowsExactly<ArgumentException>((settings with { OneBotWebhookUrls = ["file:///tmp/events"] }).Validate);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>((settings with { OneBotWebhookTimeout = TimeSpan.FromMilliseconds(-1) }).Validate);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>((settings with { OneBotWebhookTimeout = TimeSpan.FromMilliseconds(uint.MaxValue) }).Validate);
    }

    [TestMethod]
    public void ServerListenerRequiresTokenAndRemoteClearTextOptIn()
    {
        var insecure = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = "0.0.0.0",
        };

        Assert.ThrowsExactly<InvalidOperationException>(insecure.Validate);
        Assert.ThrowsExactly<InvalidOperationException>(
            (insecure with { AccessToken = "a-high-entropy-test-token" }).Validate);

        (insecure with { AllowInsecureRemoteAccess = true }).Validate();
    }

    [TestMethod]
    public void LoopbackListenerStillRequiresTokenUnlessDangerouslyOptedIn()
    {
        var oneBot = new ConnectionSettings
        {
            Transport = TransportMode.WebSocketServer,
            Host = "127.0.0.1",
        };
        Assert.ThrowsExactly<InvalidOperationException>(oneBot.Validate);
        (oneBot with { AccessToken = "test-token" }).Validate();

        var milky = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = "::1",
        };
        Assert.ThrowsExactly<InvalidOperationException>(milky.Validate);
        (milky with { AllowInsecureRemoteAccess = true }).Validate();
    }

    [TestMethod]
    public void RemoteClearTextClientAndWebhookRequireDangerousOptIn()
    {
        var client = new ConnectionSettings
        {
            Transport = TransportMode.WebSocketClient,
            Host = "192.0.2.25",
        };
        Assert.ThrowsExactly<InvalidOperationException>(client.Validate);
        (client with { AllowInsecureRemoteAccess = true }).Validate();

        var milky = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = "127.0.0.1",
            AccessToken = "test-token",
            MilkyWebhookUrls = ["http://192.0.2.25/event"],
        };
        Assert.ThrowsExactly<InvalidOperationException>(milky.Validate);
        (milky with { MilkyWebhookUrls = ["https://example.test/event"] }).Validate();
        (milky with { AllowInsecureRemoteAccess = true }).Validate();
    }

    [TestMethod]
    public void AdvertisedHostCannotPublishRemoteClearTextAssetUrls()
    {
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.WebSocketServer,
            Host = "127.0.0.1",
            AccessToken = "test-token",
            AdvertisedHost = "198.51.100.10",
        };

        Assert.ThrowsExactly<InvalidOperationException>(settings.Validate);
        (settings with { AdvertisedHost = "localhost" }).Validate();
        (settings with { AdvertisedHost = "::1" }).Validate();
        (settings with { AllowInsecureRemoteAccess = true }).Validate();
    }
}
