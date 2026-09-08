using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

// Contracts: https://github.com/botuniverse/onebot-11/blob/master/api/public.md
// and https://milky.ntqqrev.org/api/system#get_cookies
[TestClass]
public sealed class AccountCredentialsProtocolTests
{
    [TestMethod]
    public async Task V11UsesTheOfficialDefaultScopeAndIntegerResponseFieldNames()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await ConfigureAsync(fixture);
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var cookies = await CallAsync(protocol, "get_cookies");
        Assert.IsTrue(cookies.IsSuccess, cookies.Message);
        Assert.AreEqual("session=default", cookies.Data!["cookies"]!.GetValue<string>());
        Assert.HasCount(1, cookies.Data!.AsObject());
        var token = await CallAsync(protocol, "get_csrf_token");
        Assert.IsTrue(token.IsSuccess, token.Message);
        Assert.AreEqual(123, token.Data!["token"]!.GetValue<int>());
        Assert.HasCount(1, token.Data!.AsObject());
        var combined = await CallAsync(protocol, "get_credentials", new JsonObject { ["domain"] = "QUN.QQ.COM." });
        Assert.IsTrue(combined.IsSuccess, combined.Message);
        Assert.AreEqual("session=group", combined.Data!["cookies"]!.GetValue<string>());
        Assert.AreEqual(123, combined.Data!["csrf_token"]!.GetValue<int>());
        Assert.HasCount(2, combined.Data!.AsObject());
    }

    [TestMethod]
    public async Task MilkyRequiresDomainAndPreservesTheLiteralStringCsrfToken()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await ConfigureAsync(fixture);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var cookies = await CallAsync(protocol, "get_cookies", new JsonObject { ["domain"] = "qun.qq.com" });
        Assert.IsTrue(cookies.IsSuccess, cookies.Message);
        Assert.AreEqual("session=group", cookies.Data!["cookies"]!.GetValue<string>());
        Assert.HasCount(1, cookies.Data!.AsObject());
        var token = await CallAsync(protocol, "get_csrf_token");
        Assert.IsTrue(token.IsSuccess, token.Message);
        Assert.AreEqual("000123", token.Data!["csrf_token"]!.GetValue<string>());
        Assert.HasCount(1, token.Data!.AsObject());
        foreach (var invalid in new[] { new JsonObject(), new JsonObject { ["domain"] = "" }, new JsonObject { ["domain"] = null },
            new JsonObject { ["domain"] = 123 }, new JsonObject { ["domain"] = new JsonObject() } })
            Assert.AreEqual(-400, (await CallAsync(protocol, "get_cookies", invalid)).RetCode);
    }

    [TestMethod]
    public async Task MissingScopesNeverFallBackToAnotherDomainAccountOrDefaultCredential()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await ConfigureAsync(fixture);
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var milky = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        foreach (var scope in new[] { "qq.com", "sub.qun.qq.com", "qun.qq.com.evil.example", "missing.qq.com" })
        {
            var parameters = new JsonObject { ["domain"] = scope };
            Assert.AreEqual(1000, (await CallAsync(v11, "get_cookies", parameters)).RetCode);
            Assert.AreEqual(-500, (await CallAsync(milky, "get_cookies", parameters)).RetCode);
        }
        using var other = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SenderId, fixture.Platform, fixture.Media);
        Assert.AreEqual(1000, (await CallAsync(other, "get_credentials")).RetCode);
        await fixture.Platform.ClearAccountCredentialsAsync(ProtocolTestFixture.SelfId);
        Assert.AreEqual(1000, (await CallAsync(v11, "get_credentials")).RetCode);
        Assert.AreEqual(-500, (await CallAsync(milky, "get_csrf_token")).RetCode);
    }

    [TestMethod]
    public async Task ConfiguredCookiesCannotInventAMissingCsrfToken()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.SetAccountCredentialsAsync(ProtocolTestFixture.SelfId,
            new(new Dictionary<string, string> { [""] = "session=only-cookie" }));
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var milky = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        Assert.IsTrue((await CallAsync(v11, "get_cookies")).IsSuccess);
        Assert.AreEqual(1000, (await CallAsync(v11, "get_csrf_token")).RetCode);
        var combined = await CallAsync(v11, "get_credentials");
        Assert.AreEqual(1000, combined.RetCode);
        Assert.IsNull(combined.Data, "The combined getter must not return partial credentials as success.");
        Assert.AreEqual(-500, (await CallAsync(milky, "get_csrf_token")).RetCode);
    }

    [TestMethod]
    [DataRow("2147483648")]
    [DataRow("-2147483649")]
    [DataRow("opaque-milky-token")]
    public async Task NonInt32TokensRemainValidMilkyStringsButCannotProduceV11Success(string csrfToken)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.SetAccountCredentialsAsync(ProtocolTestFixture.SelfId,
            new(new Dictionary<string, string> { [""] = "session=default" }, csrfToken));
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var milky = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var failed = await CallAsync(v11, "get_credentials");
        Assert.AreEqual(1000, failed.RetCode);
        Assert.IsNull(failed.Data);
        Assert.DoesNotContain(csrfToken, failed.Message!);
        Assert.AreEqual(1000, (await CallAsync(v11, "get_csrf_token")).RetCode);
        Assert.IsTrue((await CallAsync(v11, "get_cookies")).IsSuccess);
        Assert.AreEqual(csrfToken, (await CallAsync(milky, "get_csrf_token")).Data!["csrf_token"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("2147483647", int.MaxValue)]
    [DataRow("-2147483648", int.MinValue)]
    public async Task V11AcceptsTheFullSignedInt32Contract(string csrfToken, int expected)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await fixture.Platform.SetAccountCredentialsAsync(ProtocolTestFixture.SelfId, new(new Dictionary<string, string>(), csrfToken));
        using var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var result = await CallAsync(protocol, "get_csrf_token");
        Assert.IsTrue(result.IsSuccess, result.Message);
        Assert.AreEqual(expected, result.Data!["token"]!.GetValue<int>());
        Assert.AreEqual(1000, (await CallAsync(protocol, "get_credentials")).RetCode);
    }

    [TestMethod]
    public async Task CachedCredentialReadsRemainAvailableOfflineButAreAbsentFromV12StandardActions()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await ConfigureAsync(fixture);
        await fixture.Platform.SetBotPresenceAsync(ProtocolTestFixture.SelfId, false, "Local sign out");
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        using var v12 = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var milky = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        Assert.IsTrue((await CallAsync(v11, "get_credentials")).IsSuccess);
        Assert.IsTrue((await CallAsync(milky, "get_csrf_token")).IsSuccess);
        foreach (var action in new[] { "get_cookies", "get_csrf_token", "get_credentials" })
            Assert.AreEqual(10002, (await CallAsync(v12, action)).RetCode);
    }

    [TestMethod]
    public async Task InvalidDomainErrorsNeverEchoASecretBearingUrl()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var milky = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var parameters = new JsonObject { ["domain"] = "https://qq.com/?cookie=highly-secret-value" };
        foreach (var protocol in new IProtocolImplementation[] { v11, milky })
        {
            var response = await CallAsync(protocol, "get_cookies", parameters);
            Assert.IsFalse(response.IsSuccess);
            Assert.DoesNotContain("highly-secret-value", response.Message!);
            Assert.IsNull(response.Data);
        }
        Assert.AreEqual(1400, (await CallAsync(v11, "get_cookies", new JsonObject { ["domain"] = 123 })).RetCode);
        Assert.AreEqual(1400, (await CallAsync(v11, "get_credentials", new JsonObject { ["domain"] = null })).RetCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CredentialHttpRepliesRequireTheConfiguredTransportBearer(bool useMilky)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await ConfigureAsync(fixture);
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        IProtocolImplementation protocol = useMilky
            ? new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media) : v11;
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        await using var session = new ProtocolSession(protocol, fixture.Platform, new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            AccessToken = "transport-only-secret",
            Transport = useMilky ? TransportMode.MilkyService : TransportMode.OneBotHttpServer,
        }, fixture.Assets);
        await session.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var endpoint = $"http://127.0.0.1:{port}/{(useMilky ? "api/" : "")}get_cookies";
        using (var unauthenticatedBody = new StringContent("{\"domain\":\"qun.qq.com\"}", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync(endpoint, unauthenticatedBody))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("session=group", await response.Content.ReadAsStringAsync());
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "transport-only-secret");
        using var body = new StringContent("{\"domain\":\"qun.qq.com\"}", Encoding.UTF8, "application/json");
        using var authorized = await client.PostAsync(endpoint, body);
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
        var envelope = JsonNode.Parse(await authorized.Content.ReadAsStringAsync())!;
        Assert.AreEqual("session=group", envelope["data"]!["cookies"]!.GetValue<string>());
    }

    private static Task ConfigureAsync(ProtocolTestFixture fixture) =>
        fixture.Platform.SetAccountCredentialsAsync(ProtocolTestFixture.SelfId, new(new Dictionary<string, string>
        {
            [""] = "session=default",
            ["qun.qq.com"] = "session=group",
        }, "000123"));

    private static Task<ProtocolReply> CallAsync(IProtocolImplementation protocol, string action, JsonObject? parameters = null) =>
        protocol.HandleAsync(new ProtocolCall(action, parameters ?? new JsonObject()));
}
