using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotHttpSessionTests
{
    private const string AccessToken = "onebot-http-session-test";

    [TestMethod]
    public async Task V11HttpSupportsBearerQueryGetFormPostAndProtocolErrors()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var missing = await http.GetAsync(BaseUri(port) + "get_login_info");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, BaseUri(port) + "get_login_info");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrongResponse = await http.SendAsync(wrong);
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongResponse.StatusCode);

        var queryReply = await SendAsync(
            http,
            new HttpRequestMessage(HttpMethod.Get, BaseUri(port) + "get_login_info?access_token=" + AccessToken));
        Assert.AreEqual("ok", queryReply["status"]!.GetValue<string>());
        Assert.AreEqual(0, queryReply["retcode"]!.GetValue<int>());
        Assert.AreEqual(1_000_000_001L, queryReply["data"]!["user_id"]!.GetValue<long>());

        using var formRequest = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + "get_group_info")
        {
            Content = new FormUrlEncodedContent(
                [new KeyValuePair<string, string>("group_id", ProtocolTestFixture.GroupId)]),
        };
        formRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        var formReply = await SendAsync(http, formRequest);
        Assert.AreEqual("ok", formReply["status"]!.GetValue<string>());
        Assert.AreEqual(500_000_001L, formReply["data"]!["group_id"]!.GetValue<long>());

        using var unsupported = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + "get_login_info")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        unsupported.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var unsupportedResponse = await http.SendAsync(unsupported);
        Assert.AreEqual(HttpStatusCode.NotAcceptable, unsupportedResponse.StatusCode);

        using var malformed = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + "get_login_info")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var malformedResponse = await http.SendAsync(malformed);
        Assert.AreEqual(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        using var unknown = await http.GetAsync(BaseUri(port) + "does_not_exist?access_token=" + AccessToken);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [TestMethod]
    public async Task V11DeferredActionSurvivesHTTPRequestDisconnect()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media)
        {
            RateLimitInterval = TimeSpan.FromSeconds(1),
        };
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var first = await SendV11Async(http, port, "set_group_name_rate_limited", "first-name");
        Assert.AreEqual(1, first["retcode"]!.GetValue<int>());

        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + "set_group_name_rate_limited")
        {
            Content = JsonContent(new JsonObject { ["group_id"] = ProtocolTestFixture.GroupId, ["group_name"] = "after-disconnect" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        cancellation.Cancel();
        await protocol.WaitForScheduledActionsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(
            "after-disconnect",
            (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))!.Name);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task V12HttpUsesActionEnvelopeEchoAndHTTPBoundaryErrors()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var missing = new HttpRequestMessage(HttpMethod.Post, BaseUri(port))
        {
            Content = JsonContent(new JsonObject
            {
                ["action"] = "get_self_info",
                ["params"] = new JsonObject(),
            }),
        };
        using var missingResponse = await http.SendAsync(missing);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingResponse.StatusCode);

        const string echo = "http-v12";
        var reply = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_self_info",
            ["params"] = new JsonObject(),
            ["echo"] = echo,
        });
        Assert.AreEqual("ok", reply["status"]!.GetValue<string>());
        Assert.AreEqual(0, reply["retcode"]!.GetValue<int>());
        Assert.AreEqual(echo, reply["echo"]!.GetValue<string>());

        var validSelf = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_self_info",
            ["params"] = new JsonObject(),
            ["self"] = new JsonObject { ["platform"] = "qq", ["user_id"] = ProtocolTestFixture.SelfId },
        });
        Assert.AreEqual(0, validSelf["retcode"]!.GetValue<int>());

        var invalidSelf = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_self_info",
            ["params"] = new JsonObject(),
            ["self"] = new JsonObject { ["platform"] = "qq", ["user_id"] = "unknown-bot" },
        });
        Assert.AreEqual(10102, invalidSelf["retcode"]!.GetValue<int>());

        var invalidEcho = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_self_info",
            ["params"] = new JsonObject(),
            ["echo"] = new JsonObject { ["request_id"] = "object-is-not-a-standard-echo" },
        });
        Assert.AreEqual(10001, invalidEcho["retcode"]!.GetValue<int>());

        var malformedLimit = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["limit"] = new JsonObject { ["nested"] = 1 } },
        });
        Assert.AreEqual(10003, malformedLimit["retcode"]!.GetValue<int>());

        var nullTimeoutParameters = new JsonObject();
        nullTimeoutParameters["timeout"] = null;
        var nullTimeout = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = nullTimeoutParameters,
        });
        Assert.AreEqual(10003, nullTimeout["retcode"]!.GetValue<int>());

        using var wrongPath = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + "action")
        {
            Content = JsonContent(new JsonObject()),
        };
        wrongPath.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var wrongPathResponse = await http.SendAsync(wrongPath);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongPathResponse.StatusCode);

        using var wrongMethod = new HttpRequestMessage(HttpMethod.Get, BaseUri(port));
        wrongMethod.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var wrongMethodResponse = await http.SendAsync(wrongMethod);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, wrongMethodResponse.StatusCode);

        using var wrongContentType = new HttpRequestMessage(HttpMethod.Post, BaseUri(port))
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        wrongContentType.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var wrongContentTypeResponse = await http.SendAsync(wrongContentType);
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, wrongContentTypeResponse.StatusCode);

        using var malformed = new HttpRequestMessage(HttpMethod.Post, BaseUri(port))
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var malformedResponse = await http.SendAsync(malformed);
        Assert.AreEqual(HttpStatusCode.OK, malformedResponse.StatusCode);
        var malformedEnvelope = await ParseAsync(malformedResponse);
        Assert.AreEqual(10001, malformedEnvelope["retcode"]!.GetValue<int>());

        var unsupported = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "does_not_exist",
            ["params"] = new JsonObject(),
        });
        Assert.AreEqual(10002, unsupported["retcode"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task V12LatestEventsPreservesOrderAndWaitsForNewEvent()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var firstEvents = WaitForOutboundEventsAsync(session, 2);

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("first")]);
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("second")]);
        await firstEvents;

        var ordered = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["limit"] = 0, ["timeout"] = 0 },
        });
        var events = ordered["data"]!.AsArray();
        Assert.IsGreaterThanOrEqualTo(2, events.Count);
        Assert.AreEqual("first", events[0]!["message"]![0]!["data"]!["text"]!.GetValue<string>());
        Assert.AreEqual("second", events[1]!["message"]![0]!["data"]!["text"]!.GetValue<string>());

        var waiting = SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["limit"] = 1, ["timeout"] = 3 },
        });
        var waitedEvent = WaitForOutboundEventsAsync(session, 1);
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("after-wait")]);
        await waitedEvent;
        var waited = await waiting;
        Assert.AreEqual("after-wait", waited["data"]![0]!["message"]![0]!["data"]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task StopCancelsAnActiveV12LongPoll()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var observed = WaitForInboundActionAsync(session, "get_latest_events");
        var pending = SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["timeout"] = 30 },
            ["echo"] = "stop-poll",
        });
        await observed;
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var reply = await pending;
        Assert.AreEqual("failed", reply["status"]!.GetValue<string>());
        Assert.AreEqual(20002, reply["retcode"]!.GetValue<int>());
        Assert.AreEqual("stop-poll", reply["echo"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V12LatestEventsCancellationAndConcurrentPollAreBounded()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var pending = SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["timeout"] = 30 },
        }, cancellation.Token);
        var canceled = false;
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert.IsTrue(canceled, "A canceled long poll must cancel its HTTP request.");

        var polls = Enumerable.Range(0, 4).Select(_ => SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["timeout"] = 1 },
        })).ToArray();
        await Task.Delay(200);
        var saturated = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject { ["timeout"] = 0 },
        });
        Assert.AreEqual(10004, saturated["retcode"]!.GetValue<int>());
        await Task.WhenAll(polls);

        var bufferedEvents = WaitForOutboundEventsAsync(session, OneBotEventBuffer.DefaultCapacity + 32);
        for (var index = 0; index < OneBotEventBuffer.DefaultCapacity + 32; index++)
        {
            await fixture.Platform.SendMessageAsync(
                ChatScene.Group,
                ProtocolTestFixture.GroupId,
                ProtocolTestFixture.SenderId,
                ProtocolTestFixture.SelfId,
                [new TextSegment($"bounded-{index}")]);
        }
        await bufferedEvents;
        var bounded = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject(),
        });
        Assert.IsLessThanOrEqualTo(OneBotEventBuffer.DefaultCapacity, bounded["data"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task StopAndRestartClearsPendingV12HttpEvents()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var port = ProtocolTestFixture.ReserveEphemeralPort();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(port), fixture.Assets);
        await session.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var pendingEvent = WaitForOutboundEventsAsync(session, 1);

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("discard-on-stop")]);
        await pendingEvent;
        await session.StopAsync();
        await session.StartAsync();

        var afterRestart = await SendV12Async(http, port, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject(),
        });
        Assert.IsEmpty(afterRestart["data"]!.AsArray());
    }

    [TestMethod]
    public async Task V12EventPollingHonorsEnableFlagAndConfiguredBufferSemantics()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var disabledPort = ProtocolTestFixture.ReserveEphemeralPort();
        var disabledProtocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var disabled = new ProtocolSession(
            disabledProtocol,
            fixture.Platform,
            Settings(disabledPort) with { OneBotHttpEventsEnabled = false },
            fixture.Assets);
        await disabled.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var disabledPoll = await SendV12Async(http, disabledPort, new JsonObject
        {
            ["action"] = "get_latest_events",
            ["params"] = new JsonObject(),
        });
        Assert.AreEqual(10002, disabledPoll["retcode"]!.GetValue<int>());

        var bounded = new OneBotEventBuffer(2);
        bounded.Enqueue(new JsonObject { ["id"] = 1 });
        bounded.Enqueue(new JsonObject { ["id"] = 2 });
        bounded.Enqueue(new JsonObject { ["id"] = 3 });
        var boundedEvents = await bounded.ReadAsync(0, 0);
        Assert.HasCount(2, boundedEvents);
        Assert.AreEqual(2, boundedEvents[0]["id"]!.GetValue<int>());
        Assert.AreEqual(3, boundedEvents[1]["id"]!.GetValue<int>());

        var unbounded = new OneBotEventBuffer(0);
        for (var index = 0; index < OneBotEventBuffer.DefaultCapacity + 32; index++)
        {
            unbounded.Enqueue(new JsonObject { ["id"] = index });
        }

        Assert.HasCount(OneBotEventBuffer.DefaultCapacity + 32, await unbounded.ReadAsync(0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ConnectionSettings { OneBotHttpEventBufferSize = -1 }.Validate());
    }

    private static ConnectionSettings Settings(ushort port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Transport = TransportMode.OneBotHttpServer,
        AccessToken = AccessToken,
        PostSelfEvents = false,
    };

    private static string BaseUri(ushort port) => $"http://127.0.0.1:{port}/";

    private static StringContent JsonContent(JsonObject payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> SendV12Async(
        HttpClient http,
        ushort port,
        JsonObject payload,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUri(port))
        {
            Content = JsonContent(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await ParseAsync(response);
    }

    private static async Task<JsonObject> SendAsync(HttpClient http, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return await ParseAsync(response);
        }
    }

    private static Task<JsonObject> SendV11Async(
        HttpClient http,
        ushort port,
        string action,
        string groupName) => SendAsync(
            http,
            CreateV11Request(port, action, groupName));

    private static HttpRequestMessage CreateV11Request(ushort port, string action, string groupName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUri(port) + action)
        {
            Content = JsonContent(new JsonObject
            {
                ["group_id"] = ProtocolTestFixture.GroupId,
                ["group_name"] = groupName,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return request;
    }

    private static async Task<JsonObject> ParseAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static async Task WaitForOutboundEventsAsync(ProtocolSession session, int count)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        EventHandler<TrafficEntry>? handler = null;
        handler = (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.OutboundEvent
                && Interlocked.Increment(ref seen) >= count)
            {
                completed.TrySetResult();
            }
        };
        session.TrafficObserved += handler;
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            session.TrafficObserved -= handler;
        }
    }

    private static async Task WaitForInboundActionAsync(ProtocolSession session, string action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TrafficEntry>? handler = null;
        handler = (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.InboundCall && entry.Summary == action)
            {
                completed.TrySetResult();
            }
        };
        session.TrafficObserved += handler;
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.TrafficObserved -= handler;
        }
    }
}
