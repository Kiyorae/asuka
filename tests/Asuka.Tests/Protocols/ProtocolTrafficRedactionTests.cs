using System.Text.Json.Nodes;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ProtocolTrafficRedactionTests
{
    [TestMethod]
    public void AccountCredentialRepliesAreRedactedWithoutAlteringProtocolPayloads()
    {
        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["data"] = new JsonObject
            {
                ["cookies"] = "uin=o123; skey=private-cookie",
                ["csrf_token"] = "private-milky-csrf",
                ["token"] = 123456789,
                ["domain"] = "qun.qq.com",
            },
        };
        var original = payload.ToJsonString();
        var observed = ProtocolTrafficRedaction.Redact(new TrafficEntry(
            TrafficDirection.Reply, "get_credentials", payload));
        Assert.AreEqual(original, payload.ToJsonString());
        var data = observed.Payload["data"]!;
        Assert.AreEqual("[redacted]", data["cookies"]!.GetValue<string>());
        Assert.AreEqual("[redacted]", data["csrf_token"]!.GetValue<string>());
        Assert.AreEqual("[redacted]", data["token"]!.GetValue<string>());
        Assert.AreEqual("qun.qq.com", data["domain"]!.GetValue<string>());
        Assert.AreEqual("ok", observed.Payload["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DiagnosticCloneRedactsGrantAndBearerCredentialsWithoutChangingWireData()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(new byte[] { 1, 2, 3 }, "data.bin");
        var grant = fixture.Assets.CreateAssetDownloadToken(asset.Id);
        var url = $"https://example.com/assets/{asset.Id}?download_token={grant}&view=inline";
        var payload = new JsonObject
        {
            ["url"] = url,
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer root-secret", ["Accept"] = "application/octet-stream" },
            ["nested"] = new JsonArray(new JsonObject { ["access_token"] = "root-secret", ["download_token"] = grant }),
        };
        var original = payload.ToJsonString();
        var entry = new TrafficEntry(Guid.NewGuid(), TrafficDirection.Reply, DateTimeOffset.UtcNow, "get_file", payload);
        var observed = ProtocolTrafficRedaction.Redact(entry);
        Assert.AreEqual(original, payload.ToJsonString());
        Assert.AreEqual(original, entry.Payload.ToJsonString());
        Assert.AreEqual(entry.Id, observed.Id);
        Assert.DoesNotContain(grant, observed.Payload.ToJsonString());
        Assert.DoesNotContain("root-secret", observed.Payload.ToJsonString());
        Assert.Contains(asset.Id, observed.Payload["url"]!.GetValue<string>());
        Assert.Contains(grant.Split('.')[1], observed.Payload["url"]!.GetValue<string>());
        Assert.AreEqual("application/octet-stream", observed.Payload["headers"]!["Accept"]!.GetValue<string>());
        Assert.IsTrue(fixture.Assets.ValidateAssetDownloadToken(asset.Id, grant));
    }

    [TestMethod]
    public void EncodedQueryKeysAndMalformedRawFramesCannotLeakReusableCredentials()
    {
        var entry = new TrafficEntry(TrafficDirection.InboundCall, "call", new JsonObject
        {
            ["url"] = "https://example.com/a?%61ccess_token=hidden-secret&%64ownload_token=v1.1800000000.signature&x=1",
        });
        var observed = ProtocolTrafficRedaction.Redact(entry).Payload.ToJsonString();
        Assert.DoesNotContain("hidden-secret", observed);
        Assert.DoesNotContain("signature", observed);
        Assert.Contains("1800000000", observed);
        var malformed = new TrafficEntry(TrafficDirection.InboundCall, "Unparseable frame", "{\"access_token\":\"unfinished-secret");
        Assert.DoesNotContain("unfinished-secret", ProtocolTrafficRedaction.Redact(malformed).Payload.ToJsonString());
    }
}
