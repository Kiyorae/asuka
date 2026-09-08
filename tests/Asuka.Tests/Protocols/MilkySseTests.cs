using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

// Wire contracts: https://milky.ntqqrev.org/guide/communication#sse-连接
[TestClass]
public sealed class MilkySseTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EventGetStreamsNamedUtf8JsonEventsWithBearerOrQueryAuthentication(bool useQuery)
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using var request = EventRequest(settings, useQuery);
        // The Milky GET example does not require Accept: text/event-stream.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.IsTrue(response.Headers.CacheControl?.NoCache);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);

        const string content = "你好 👋\nsecond line\r\nevent: injected\ndata: invalid";
        await SendAsync(fixture, content);
        var (name, payload) = await ReadEventAsync(reader);
        Assert.AreEqual("milky_event", name);
        Assert.AreEqual("message_receive", payload["event_type"]!.GetValue<string>());
        Assert.AreEqual(1_000_000_001L, payload["self_id"]!.GetValue<long>());
        Assert.AreEqual("group", payload["data"]!["message_scene"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, payload["data"]!["peer_id"]!.GetValue<long>());
        Assert.AreEqual(content, payload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());

        await SendAsync(fixture, "next event");
        var next = await ReadEventAsync(reader);
        Assert.AreEqual("milky_event", next.Name);
        Assert.AreEqual("next event", next.Payload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());
        Assert.AreEqual(SessionStateKind.Ready, session.State.Kind);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("wrong", null)]
    [DataRow(null, "wrong")]
    [DataRow("wrong", "milky-secret")]
    public async Task EventGetRejectsMissingOrInvalidAuthentication(string? bearer, string? query)
    {
        await using var fixture = new ProtocolTestFixture();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, EventUrl(settings) + (query is null ? "" : $"?access_token={query}"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var failure = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("failed", failure["status"]!.GetValue<string>());
        Assert.AreEqual(-403, failure["retcode"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task EventGetPreservesBrowserOriginRejectionAndMethodRestriction()
    {
        await using var fixture = new ProtocolTestFixture();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using var browserRequest = EventRequest(settings);
        browserRequest.Headers.Add("Origin", "https://example.com");
        using var browserResponse = await http.SendAsync(browserRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.Forbidden, browserResponse.StatusCode);
        using var postRequest = EventRequest(settings);
        postRequest.Method = HttpMethod.Post;
        using var postResponse = await http.SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
        using var invalidUpgradeRequest = EventRequest(settings);
        invalidUpgradeRequest.Headers.Add("Upgrade", "websocket");
        using var invalidUpgradeResponse = await http.SendAsync(invalidUpgradeRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidUpgradeResponse.StatusCode);
    }

    [TestMethod]
    public async Task SseAndWebSocketSubscribersReceiveTheSameOrderedEvents()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using var firstRequest = EventRequest(settings);
        using var firstResponse = await http.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstReader = new StreamReader(await firstResponse.Content.ReadAsStreamAsync(), Encoding.UTF8);
        using var secondRequest = EventRequest(settings, useQuery: true);
        using var secondResponse = await http.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        using var secondReader = new StreamReader(await secondResponse.Content.ReadAsStreamAsync(), Encoding.UTF8);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer milky-secret");
        // WebSocket upgrade has priority even with an SSE Accept header.
        socket.Options.SetRequestHeader("Accept", "text/event-stream");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.Port}/event"), timeout.Token);

        for (var index = 0; index < 3; index++)
        {
            await SendAsync(fixture, $"event {index}");
            var first = await ReadEventAsync(firstReader);
            var second = await ReadEventAsync(secondReader);
            var bytes = new byte[16 * 1024];
            var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.IsTrue(received.EndOfMessage);
            var websocketPayload = JsonNode.Parse(bytes.AsSpan(0, received.Count))!;
            Assert.IsTrue(JsonNode.DeepEquals(first.Payload, second.Payload));
            Assert.IsTrue(JsonNode.DeepEquals(first.Payload, websocketPayload));
        }
    }

    [TestMethod]
    public async Task DisconnectAndStopReleaseStreamsAndAllowRestart()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using (var request = EventRequest(settings))
        using (var disconnectedResponse = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.AreEqual(HttpStatusCode.OK, disconnectedResponse.StatusCode);
        }
        using var liveRequest = EventRequest(settings);
        using var liveResponse = await http.SendAsync(liveRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, liveResponse.StatusCode);
        using var reader = new StreamReader(await liveResponse.Content.ReadAsStreamAsync(), Encoding.UTF8);
        await SendAsync(fixture, "still connected");
        _ = await ReadEventAsync(reader);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionStateKind.Idle, session.State.Kind);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.IsNull(await reader.ReadLineAsync(timeout.Token));

        await session.StartAsync();
        using var restartedRequest = EventRequest(settings);
        using var restartedResponse = await http.SendAsync(restartedRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, restartedResponse.StatusCode);
        using var restartedReader = new StreamReader(await restartedResponse.Content.ReadAsStreamAsync(), Encoding.UTF8);
        await SendAsync(fixture, "after restart");
        var restarted = await ReadEventAsync(restartedReader);
        Assert.AreEqual("after restart", restarted.Payload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task IdleSseConnectionSendsACommentWithoutInventingAProtocolEvent()
    {
        await using var fixture = new ProtocolTestFixture();
        var settings = Settings() with { AccessToken = "", AllowInsecureRemoteAccess = true };
        await using var session = Session(fixture, settings);
        await session.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync(EventUrl(settings), HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Assert.StartsWith(":", await reader.ReadLineAsync(timeout.Token));
        Assert.AreEqual("", await reader.ReadLineAsync(timeout.Token));
    }

    [TestMethod]
    public async Task StalledSseSubscriberIsDisconnectedWithoutBlockingAnActiveSubscriber()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var settings = Settings();
        await using var session = Session(fixture, settings);
        var failedDelivery = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutboundDeliveryFailed += (_, error) => failedDelivery.TrySetResult(error);
        await session.StartAsync();
        using var stalledHttp = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 1024 };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
        using var stalledRequest = EventRequest(settings);
        using var stalledResponse = await stalledHttp.SendAsync(stalledRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, stalledResponse.StatusCode);
        using var activeHttp = new HttpClient();
        using var activeRequest = EventRequest(settings);
        using var activeResponse = await activeHttp.SendAsync(activeRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, activeResponse.StatusCode);
        using var activeReader = new StreamReader(await activeResponse.Content.ReadAsStreamAsync(), Encoding.UTF8);

        var largeMessage = new string('x', 256 * 1024);
        // Never read stalledResponse. Its small receive window lets the queue limit
        // be reached without depending on the operating system's default buffers.
        for (var index = 0; index < 96 && !failedDelivery.Task.IsCompleted; index++)
        {
            await SendAsync(fixture, largeMessage);
            var current = await ReadEventAsync(activeReader);
            Assert.AreEqual("milky_event", current.Name);
            Assert.AreEqual(largeMessage.Length, current.Payload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>().Length);
        }
        var failure = await failedDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<IOException>(failure);
        Assert.Contains("could not keep up", failure.Message);
        await SendAsync(fixture, "after slow subscriber was disconnected");
        var final = await ReadEventAsync(activeReader);
        Assert.AreEqual("after slow subscriber was disconnected", final.Payload["data"]!["segments"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    private static ConnectionSettings Settings() => new()
    {
        Transport = TransportMode.MilkyService,
        Host = IPAddress.Loopback.ToString(),
        Port = ProtocolTestFixture.ReserveEphemeralPort(),
        AccessToken = "milky-secret",
    };

    private static ProtocolSession Session(ProtocolTestFixture fixture, ConnectionSettings settings) => new(
        new MilkyProtocol(ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media),
        fixture.Platform,
        settings,
        fixture.Assets);

    private static string EventUrl(ConnectionSettings settings) => $"http://127.0.0.1:{settings.Port}/event";

    private static HttpRequestMessage EventRequest(ConnectionSettings settings, bool useQuery = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EventUrl(settings) + (useQuery ? "?access_token=milky-secret" : ""));
        if (!useQuery)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "milky-secret");
        }
        return request;
    }

    private static Task<Message> SendAsync(ProtocolTestFixture fixture, string text) => fixture.Platform.SendMessageAsync(
        ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, [new TextSegment(text)]);

    private static async Task<(string Name, JsonObject Payload)> ReadEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    continue; // Keepalive comments are not protocol events.
                }
                Assert.IsNotNull(name);
                return (name, JsonNode.Parse(data.ToString())!.AsObject());
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.AppendLine(line[6..]);
            }
            else
            {
                Assert.StartsWith(":", line);
            }
        }
        Assert.Fail("The SSE connection ended before a complete event arrived.");
        return default;
    }
}
