using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class ProtocolSessionTests
{
    [TestMethod]
    public async Task MilkyServiceUsesOnePortForBearerApiAndQueryAuthenticatedEventStream()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        fixture.Media.SetEndpoint(IPAddress.Loopback.ToString(), port);
        var protocol = new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.MilkyService,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            AccessToken = "milky-secret",
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
        Assert.AreEqual(port, session.State.Port);
        Assert.AreEqual(RoundTripTimeKind.Unsupported, session.RoundTripTime.Kind);

        using var http = new HttpClient();
        using var queryOnlyResponse = await http.PostAsync(
            $"http://127.0.0.1:{port}/api/get_login_info?access_token=milky-secret",
            JsonContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, queryOnlyResponse.StatusCode);
        var queryOnlyFailure = await ParseResponseAsync(queryOnlyResponse);
        Assert.AreEqual(-403, queryOnlyFailure["retcode"]!.GetValue<int>());

        using var eventSocket = new ClientWebSocket();
        await eventSocket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/event?access_token=milky-secret"),
            CancellationToken.None);
        Assert.AreEqual(WebSocketState.Open, eventSocket.State);

        using var loginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/api/get_login_info")
        {
            Content = JsonContent("{}"),
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "milky-secret");
        using var loginResponse = await http.SendAsync(loginRequest);
        Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await ParseResponseAsync(loginResponse);
        Assert.AreEqual("ok", login["status"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_001L, login["data"]!["uin"]!.GetValue<long>());

        using var malformedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/api/get_login_info")
        {
            Content = JsonContent("{"),
        };
        malformedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "milky-secret");
        using var malformedResponse = await http.SendAsync(malformedRequest);
        Assert.AreEqual(HttpStatusCode.OK, malformedResponse.StatusCode);
        var malformed = await ParseResponseAsync(malformedResponse);
        Assert.AreEqual(-400, malformed["retcode"]!.GetValue<int>());

        var receiveEvent = ReceiveJsonAsync(eventSocket);
        _ = await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("pushed over /event")]);
        var eventPayload = await receiveEvent;
        Assert.AreEqual("message_receive", eventPayload["event_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_001L, eventPayload["self_id"]!.GetValue<long>());
        Assert.AreEqual("pushed over /event", eventPayload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OneBotV12WebSocketSendsHandshakeBeforeServingActions()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(
            OneBotVersion.V12,
            ProtocolTestFixture.SelfId,
            fixture.Platform,
            new StubProtocolAssetResolver());
        var settings = new ConnectionSettings
        {
            Transport = TransportMode.WebSocketServer,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            AccessToken = "onebot-secret",
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings);
        await session.StartAsync();
        Assert.AreEqual(SessionStateKind.Listening, session.State.Kind);

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("12.asuka");
        socket.Options.AddSubProtocol("token.onebot-secret");
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/onebot/v12/ws"),
            CancellationToken.None);
        Assert.AreEqual("12.asuka", socket.SubProtocol);

        var connect = await ReceiveJsonAsync(socket);
        var status = await ReceiveJsonAsync(socket);
        Assert.AreEqual("meta", connect["type"]!.GetValue<string>());
        Assert.AreEqual("connect", connect["detail_type"]!.GetValue<string>());
        Assert.AreEqual(ProtocolTestFixture.SelfId, connect["self"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("status_update", status["detail_type"]!.GetValue<string>());
        Assert.IsTrue(status["status"]!["good"]!.GetValue<bool>());
        Assert.AreEqual(SessionStateKind.Connected, session.State.Kind);

        var malformedJson = await SendAndReceiveJsonAsync(socket, "{");
        Assert.AreEqual(10001, malformedJson["retcode"]!.GetValue<int>());

        var arrayRoot = await SendAndReceiveJsonAsync(socket, "[]");
        Assert.AreEqual(10001, arrayRoot["retcode"]!.GetValue<int>());

        var missingParameters = await SendAndReceiveJsonAsync(
            socket,
            "{\"action\":\"set_group_name\",\"echo\":\"missing-params\"}");
        Assert.AreEqual(10001, missingParameters["retcode"]!.GetValue<int>());
        Assert.AreEqual("missing-params", missingParameters["echo"]!.GetValue<string>());

        var wrongParameters = await SendAndReceiveJsonAsync(
            socket,
            "{\"action\":\"set_group_name\",\"params\":[],\"echo\":\"wrong-params\"}");
        Assert.AreEqual(10001, wrongParameters["retcode"]!.GetValue<int>());
        Assert.AreEqual("wrong-params", wrongParameters["echo"]!.GetValue<string>());

        var objectEcho = await SendAndReceiveJsonAsync(
            socket,
            "{\"action\":\"set_group_name\",\"params\":{},\"echo\":{}}");
        Assert.AreEqual(10001, objectEcho["retcode"]!.GetValue<int>());
        Assert.IsFalse(objectEcho.ContainsKey("echo"));

        var nullEcho = await SendAndReceiveJsonAsync(
            socket,
            "{\"action\":\"set_group_name\",\"params\":{},\"echo\":null}");
        Assert.AreEqual(10001, nullEcho["retcode"]!.GetValue<int>());
        Assert.IsFalse(nullEcho.ContainsKey("echo"));

        var nestedDuplicate = await SendAndReceiveJsonAsync(
            socket,
            "{\"action\":\"set_group_name\",\"params\":{},\"extension\":{\"key\":1,\"key\":2},\"echo\":\"nested-duplicate\"}");
        Assert.AreEqual(10001, nestedDuplicate["retcode"]!.GetValue<int>());
        Assert.AreEqual("nested-duplicate", nestedDuplicate["echo"]!.GetValue<string>());

        await SendJsonAsync(socket, new JsonObject
        {
            ["action"] = "set_group_name",
            ["params"] = new JsonObject { ["group_id"] = ProtocolTestFixture.GroupId, ["group_name"] = "Wrong account" },
            ["self"] = new JsonObject { ["platform"] = "qq", ["user_id"] = "unknown-bot" },
            ["echo"] = "foreign-self",
        });
        var rejected = await ReceiveJsonAsync(socket);
        Assert.AreEqual(10102, rejected["retcode"]!.GetValue<int>());
        Assert.AreEqual("foreign-self", rejected["echo"]!.GetValue<string>());
        Assert.AreEqual("Protocol Test Group", (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);

        await SendJsonAsync(socket, new JsonObject
        {
            ["action"] = "get_supported_actions",
            ["params"] = new JsonObject(),
            ["echo"] = "wire-1",
        });
        var reply = await ReceiveJsonAsync(socket);
        Assert.AreEqual("ok", reply["status"]!.GetValue<string>());
        Assert.AreEqual(0, reply["retcode"]!.GetValue<int>());
        Assert.AreEqual("wire-1", reply["echo"]!.GetValue<string>());
        var actions = (JsonArray)reply["data"]!;
        Assert.IsTrue(actions.Any(
            static action => action!.GetValue<string>() == "send_message"));
        Assert.IsTrue(actions.Any(
            static action => action!.GetValue<string>() == "get_supported_actions"));

    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonObject> ParseResponseAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException($"Expected a JSON object, received: {text}");
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, JsonObject payload)
    {
        await SendTextAsync(socket, payload.ToJsonString());
    }

    private static async Task<JsonObject> SendAndReceiveJsonAsync(ClientWebSocket socket, string payload)
    {
        await SendTextAsync(socket, payload);
        return await ReceiveJsonAsync(socket);
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);
    }

    private static async Task<JsonObject> ReceiveJsonAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The socket closed before a JSON frame was received.");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            await content.WriteAsync(buffer.AsMemory(0, result.Count), timeout.Token);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var text = Encoding.UTF8.GetString(content.ToArray());
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException($"Expected a JSON object, received: {text}");
    }
}
