using System.Text.Json.Nodes;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotV12ActionRequestTests
{
    [TestMethod]
    public void ValidEnvelopeCreatesAnIsolatedCallAndPreservesStringEcho()
    {
        var payload = Parse("""
            {"action":"send_message","params":{"detail_type":"private","message":[{"type":"text"}]},"echo":""}
            """);

        var failure = OneBotV12ActionRequest.Validate(payload, out var call);

        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual("send_message", call.Name);
        Assert.AreEqual("private", call.Parameters["detail_type"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, call.Echo!.GetValue<string>());

        payload["params"]!["detail_type"] = "group";
        payload["echo"] = "changed";
        Assert.AreEqual("private", call.Parameters["detail_type"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, call.Echo!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("{\"action\":\"send_message\"}")]
    [DataRow("{\"action\":null,\"params\":{}}")]
    [DataRow("{\"action\":\"\",\"params\":{}}")]
    [DataRow("{\"action\":1,\"params\":{}}")]
    [DataRow("{\"action\":\"send_message\",\"params\":null}")]
    [DataRow("{\"action\":\"send_message\",\"params\":[]}")]
    [DataRow("{\"action\":\"send_message\",\"params\":\"x\"}")]
    [DataRow("{\"action\":\"send_message\",\"params\":{},\"echo\":null}")]
    [DataRow("{\"action\":\"send_message\",\"params\":{},\"echo\":1}")]
    [DataRow("{\"action\":\"send_message\",\"params\":{},\"echo\":{}}")]
    [DataRow("{\"action\":\"send_message\",\"params\":{},\"echo\":[]}")]
    public void InvalidEnvelopeReturnsBadRequestWithoutCall(string json)
    {
        var failure = OneBotV12ActionRequest.Validate(Parse(json), out var call);

        Assert.IsNotNull(failure);
        Assert.AreEqual(10001, failure.RetCode);
        Assert.AreEqual(OneBotV12ActionRequest.BadRequestMessage, failure.Message);
        Assert.IsNull(call);
    }

    [TestMethod]
    public void AdditionalFieldsArePermitted()
    {
        var failure = OneBotV12ActionRequest.Validate(
            Parse("{\"action\":\"get_status\",\"params\":{},\"extension\":true}"),
            out var call);

        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual("get_status", call.Name);
    }

    [TestMethod]
    [DataRow("{\"action\":\"one\",\"action\":\"two\",\"params\":{}}")]
    [DataRow("{\"action\":\"get_status\",\"params\":{\"key\":1,\"key\":2}}")]
    [DataRow("{\"action\":\"get_status\",\"params\":{},\"self\":{\"platform\":\"qq\",\"platform\":\"qq\"}}")]
    [DataRow("{\"action\":\"get_status\",\"params\":{},\"extension\":{\"key\":1,\"key\":2}}")]
    public void DuplicatePropertiesAreBadRequests(string json)
    {
        var failure = OneBotV12ActionRequest.Validate(Parse(json), out var call);

        Assert.IsNotNull(failure);
        Assert.AreEqual(10001, failure.RetCode);
        Assert.IsNull(call);
    }

    [TestMethod]
    public void InProcessEnvelopeOverTheMaximumDepthIsBadRequest()
    {
        var payload = new JsonObject
        {
            ["action"] = "get_status",
            ["params"] = new JsonObject(),
        };
        JsonObject nested = payload;
        for (var index = 0; index < 64; index++)
        {
            var next = new JsonObject();
            nested["extension"] = next;
            nested = next;
        }

        var failure = OneBotV12ActionRequest.Validate(payload, out var call);

        Assert.IsNotNull(failure);
        Assert.AreEqual(10001, failure.RetCode);
        Assert.IsNull(call);
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
