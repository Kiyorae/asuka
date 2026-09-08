using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.App;
using Asuka.Protocols;

namespace Asuka.Tests.App;

[TestClass]
public sealed class AppPreferencesTests
{
    [TestMethod]
    public void PersistedPreferencesNeverContainEndpointOrWebhookCredentials()
    {
        var preferences = new AppPreferences
        {
            Protocol = ProtocolKind.OneBotV11,
            AccessToken = "endpoint-secret-fixture",
            OneBotWebhookSecret = "signing-secret-fixture",
            OneBotWebhookUrls = "http://127.0.0.1:8010/events",
            OneBotWebhookTimeoutMilliseconds = 1250,
        };
        var json = JsonSerializer.Serialize(preferences, AppPreferences.SerializerOptions);
        var fields = JsonNode.Parse(json)!.AsObject();
        Assert.IsFalse(fields.ContainsKey(nameof(AppPreferences.AccessToken)));
        Assert.IsFalse(fields.ContainsKey(nameof(AppPreferences.OneBotWebhookSecret)));
        Assert.DoesNotContain(preferences.AccessToken, json);
        Assert.DoesNotContain(preferences.OneBotWebhookSecret, json);
        var reloaded = JsonSerializer.Deserialize<AppPreferences>(json, AppPreferences.SerializerOptions)!;
        Assert.AreEqual(preferences.OneBotWebhookUrls, reloaded.OneBotWebhookUrls);
        Assert.AreEqual(1250L, reloaded.OneBotWebhookTimeoutMilliseconds);
        Assert.AreEqual(string.Empty, reloaded.AccessToken);
        Assert.AreEqual(string.Empty, reloaded.OneBotWebhookSecret);
        var injected = JsonSerializer.Deserialize<AppPreferences>("""
            {"AccessToken":"plaintext-token","OneBotWebhookSecret":"plaintext-secret"}
            """, AppPreferences.SerializerOptions)!;
        Assert.AreEqual(string.Empty, injected.AccessToken);
        Assert.AreEqual(string.Empty, injected.OneBotWebhookSecret);
    }

    [TestMethod]
    public void WebhookSettingsPreserveExactSecretTimeoutAndSeparateProtocolEndpoints()
    {
        var preferences = new AppPreferences
        {
            Protocol = ProtocolKind.OneBotV12,
            Transport = TransportMode.OneBotHttpServer,
            AccessToken = "endpoint-token",
            OneBotWebhookSecret = "  exact signing key  ",
            OneBotWebhookUrls = "  http://127.0.0.1:8010/events;a\r\nhttps://example.test/events\n",
            WebhookUrls = "https://milky.example.test/events",
            OneBotWebhookTimeoutMilliseconds = 1250,
        };
        var settings = preferences.ToConnectionSettings();
        settings.Validate();
        Assert.AreEqual(TransportMode.OneBotHttpServer, settings.Transport);
        Assert.HasCount(2, settings.GetOneBotWebhookEndpoints());
        Assert.AreEqual("/events;a", settings.GetOneBotWebhookEndpoints()[0].AbsolutePath);
        Assert.AreEqual("https://milky.example.test/events", settings.GetWebhookEndpoints()[0].AbsoluteUri);
        Assert.AreEqual(preferences.OneBotWebhookSecret, settings.OneBotWebhookSecret);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1250), settings.OneBotWebhookTimeout);
        Assert.AreEqual(TimeSpan.Zero, new AppPreferences().ToConnectionSettings().OneBotWebhookTimeout);
    }
}
