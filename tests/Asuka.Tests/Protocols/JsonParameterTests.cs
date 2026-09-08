using System.Text.Json.Nodes;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class JsonParameterTests
{
    [TestMethod]
    public void ClrAndParsedIntegralParametersBehaveIdentically()
    {
        foreach (var scalar in new JsonNode?[] { JsonValue.Create(3), JsonValue.Create(3L), JsonValue.Create(3U), JsonNode.Parse("3"), JsonValue.Create("3") })
        {
            var call = new ProtocolCall("test", new JsonObject { ["value"] = scalar });
            Assert.AreEqual(3L, call.GetLong("value"));
            Assert.AreEqual("3", call.GetId("value"));
        }
    }

    [TestMethod]
    public void IntegerParametersRejectBooleansAndFractions()
    {
        foreach (var scalar in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create(false), JsonValue.Create(3.5), JsonNode.Parse("true"), JsonNode.Parse("3.5") })
        {
            Assert.IsNull(new ProtocolCall("test", new JsonObject { ["value"] = scalar }).GetLong("value"));
        }
    }
}
