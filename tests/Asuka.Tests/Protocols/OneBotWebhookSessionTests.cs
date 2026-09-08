using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotWebhookSessionTests
{
    private const string AccessToken = "onebot-webhook-access";
    private const string Secret = "onebot-webhook-signing-secret";

    [TestMethod]
    public async Task AcknowledgedEnableStillGetsDisableAfterMalformedQuickOperationResponse()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(
            new ProbeResponse(HttpStatusCode.OK, "application/json", "{"u8.ToArray(), TimeSpan.Zero),
            ProbeResponse.Json(200, new JsonObject { ["reply"] = "must-not-run-during-stop" }));
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, Settings(probe.Endpoint), fixture.Assets);
        var failedResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutboundDeliveryFailed += (_, _) => failedResponse.TrySetResult();
        await session.StartAsync();
        Assert.AreEqual("enable", JsonNode.Parse((await probe.NextAsync()).Body)!["sub_type"]!.GetValue<string>());
        await failedResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("disable", JsonNode.Parse((await probe.NextAsync()).Body)!["sub_type"]!.GetValue<string>());
        Assert.IsEmpty(await fixture.Store.GetMessagesAsync(new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId)));
    }

    [TestMethod]
    public async Task FrameworkHeaderFailureDoesNotLogTheConfiguredCredential()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(ProbeResponse.Empty(204));
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var credential = "private-header-value\r\ninvalid";
        await using var session = new ProtocolSession(protocol, fixture.Platform,
            Settings(probe.Endpoint) with { AccessToken = credential }, fixture.Assets);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutboundDeliveryFailed += (_, error) => failed.TrySetResult(error);
        await session.StartAsync();
        var diagnostic = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("private-header-value", diagnostic.ToString());
        Assert.DoesNotContain(credential, diagnostic.ToString());
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private const int MaximumWebhookResponseBytes = 4 * 1024 * 1024;

    [TestMethod]
    public async Task V11WebhookUsesExactBodySignatureAndProcessesQuickOperations()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(
            ProbeResponse.Empty(204),
            ProbeResponse.Json(200, new JsonObject { ["reply"] = "quick reply" }));
        var settings = Settings(probe.Endpoint) with { OneBotWebhookSecret = Secret };
        var protocol = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        var enable = await probe.NextAsync();
        var enablePayload = JsonNode.Parse(enable.Body)!.AsObject();
        Assert.AreEqual("enable", enablePayload["sub_type"]!.GetValue<string>());

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("v11-webhook")]);
        var request = await probe.NextAsync();

        Assert.AreEqual("application/json", request.Headers["Content-Type"]);
        Assert.AreEqual(ProtocolTestFixture.SelfId, request.Headers["X-Self-ID"]);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
#pragma warning disable CA5350 // The assertion mirrors OneBot V11's required HMAC-SHA1 signature.
        var signature = HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), request.Body);
#pragma warning restore CA5350
        Assert.AreEqual($"sha1={Convert.ToHexString(signature).ToLowerInvariant()}", request.Headers["X-Signature"]);
        Assert.IsNotNull(JsonNode.Parse(request.Body));

        var chat = new Chat(ChatScene.Group, ProtocolTestFixture.GroupId, ProtocolTestFixture.SelfId);
        await WaitUntilAsync(async () => (await fixture.Store.GetMessagesAsync(chat)).Count == 2);
        var sent = (await fixture.Store.GetMessagesAsync(chat)).Single(item => item.SenderId == ProtocolTestFixture.SelfId);
        Assert.HasCount(1, sent.Content.OfType<MentionSegment>().Where(item => item.UserId == ProtocolTestFixture.SenderId));
        Assert.Contains("quick reply", sent.Content.TextPreview());

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var disable = await probe.NextAsync();
        var disablePayload = JsonNode.Parse(disable.Body)!.AsObject();
        Assert.AreEqual("disable", disablePayload["sub_type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V12WebhookSendsRequiredHeadersAndExecutesActionListInOrder()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(
            ProbeResponse.Empty(204),
            ProbeResponse.Json(200, new JsonArray
            {
                Action("set_group_name", "first-webhook-name"),
                Action("set_group_name", "second-webhook-name"),
            }));
        var settings = Settings(probe.Endpoint);
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        var startup = await probe.NextAsync();
        var startupPayload = JsonNode.Parse(startup.Body)!.AsObject();
        Assert.AreEqual("meta", startupPayload["type"]!.GetValue<string>());
        Assert.AreEqual("status_update", startupPayload["detail_type"]!.GetValue<string>());

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("v12-webhook")]);
        var request = await probe.NextAsync();

        Assert.AreEqual("application/json", request.Headers["Content-Type"]);
        Assert.IsTrue(request.Headers["User-Agent"].StartsWith("OneBot/12", StringComparison.Ordinal));
        Assert.AreEqual("12", request.Headers["X-OneBot-Version"]);
        Assert.AreEqual("asuka", request.Headers["X-Impl"]);
        Assert.AreEqual($"Bearer {AccessToken}", request.Headers["Authorization"]);
        Assert.AreEqual("message", JsonNode.Parse(request.Body)!["type"]!.GetValue<string>());

        await WaitUntilAsync(
            async () => (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))?.Name == "second-webhook-name");
    }

    [TestMethod]
    public async Task WebhookEndpointsHaveIndependentOrderedQueuesAndStopCancelsSlowDelivery()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var slow = new WebhookProbe(new ProbeResponse(HttpStatusCode.NoContent, null, [], TimeSpan.FromSeconds(5)));
        await using var fast = new WebhookProbe(ProbeResponse.Empty(204));
        var settings = Settings(slow.Endpoint, fast.Endpoint);
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();
        await slow.NextAsync();
        await fast.NextAsync();

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("ordered-one")]);
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("ordered-two")]);

        var fastRequests = await Task.WhenAll(fast.NextAsync(), fast.NextAsync());
        Assert.AreEqual("ordered-one", EventText(fastRequests[0]));
        Assert.AreEqual("ordered-two", EventText(fastRequests[1]));

        await slow.NextAsync();
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task V12WebhookInvalidActionListProducesSafeDiagnosticAndDoesNotReenter()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(
            ProbeResponse.Empty(204),
            ProbeResponse.Json(200, new JsonArray
            {
                new JsonObject
                {
                    ["action"] = "set_group_name",
                    ["params"] = new JsonObject
                    {
                        ["group_id"] = ProtocolTestFixture.GroupId,
                        ["group_name"] = "must-not-commit-self",
                    },
                    ["self"] = new JsonObject { ["platform"] = "qq", ["user_id"] = "unknown-bot" },
                },
                new JsonObject
                {
                    ["action"] = "set_group_name",
                    ["params"] = new JsonObject
                    {
                        ["group_id"] = ProtocolTestFixture.GroupId,
                        ["group_name"] = "must-not-commit-echo",
                    },
                    ["echo"] = new JsonObject { ["secret"] = "must-not-leak" },
                },
                new JsonObject
                {
                    ["action"] = "set_group_name",
                    ["params"] = new JsonObject
                    {
                        ["group_id"] = ProtocolTestFixture.GroupId,
                        ["group_name"] = "after-invalid-webhook-action",
                    },
                    ["echo"] = "valid-follow-up",
                },
            }));
        var diagnostic = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<TrafficEntry>();
        var settings = Settings(probe.Endpoint);
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        session.OutboundDeliveryFailed += (_, error) => diagnostic.TrySetResult(error);
        session.TrafficObserved += (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.InboundCall)
            {
                calls.Enqueue(entry);
            }
        };
        await session.StartAsync();
        await probe.NextAsync();

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("diagnostic")]);
        await probe.NextAsync();
        var error = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("must-not-leak", error.Message);
        await WaitUntilAsync(
            async () => (await fixture.Store.GetGroupAsync(ProtocolTestFixture.GroupId))?.Name == "after-invalid-webhook-action");
        Assert.HasCount(1, calls.Where(entry => entry.Summary == "set_group_name"));
    }

    [TestMethod]
    public async Task WebhookRejectsOversizedResponseWithoutLoggingItsBody()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        var oversizedBody = new byte[MaximumWebhookResponseBytes + 1];
        oversizedBody[0] = (byte)'[';
        await using var probe = new WebhookProbe(
            ProbeResponse.Empty(204),
            new ProbeResponse(HttpStatusCode.OK, "application/json", oversizedBody, TimeSpan.Zero));
        var diagnostic = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Settings(probe.Endpoint);
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        session.OutboundDeliveryFailed += (_, error) => diagnostic.TrySetResult(error);
        await session.StartAsync();
        await probe.NextAsync();

        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("oversized-response")]);
        await probe.NextAsync();
        var error = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("response exceeded", error.Message);
        Assert.DoesNotContain("must-not-leak", error.Message);
    }

    [TestMethod]
    public async Task WebhookTimeoutIsAppliedAndZeroTimeoutStillStopsPromptly()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var finiteProbe = new WebhookProbe(
            ProbeResponse.Empty(204),
            new ProbeResponse(HttpStatusCode.NoContent, null, [], TimeSpan.FromMilliseconds(500)));
        var finiteDiagnostic = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finiteSettings = Settings(finiteProbe.Endpoint) with
        {
            OneBotWebhookTimeout = TimeSpan.FromMilliseconds(100),
        };
        var finiteProtocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var finiteSession = new ProtocolSession(finiteProtocol, fixture.Platform, finiteSettings, fixture.Assets);
        finiteSession.OutboundDeliveryFailed += (_, error) => finiteDiagnostic.TrySetResult(error);
        await finiteSession.StartAsync();
        await finiteProbe.NextAsync();
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("finite-timeout")]);
        await finiteProbe.NextAsync();
        await finiteDiagnostic.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await finiteSession.StopAsync();

        await using var unlimitedProbe = new WebhookProbe(
            ProbeResponse.Empty(204),
            new ProbeResponse(HttpStatusCode.NoContent, null, [], TimeSpan.FromSeconds(2)));
        var unlimitedSettings = Settings(unlimitedProbe.Endpoint) with
        {
            OneBotWebhookTimeout = TimeSpan.Zero,
        };
        var unlimitedProtocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var unlimitedSession = new ProtocolSession(unlimitedProtocol, fixture.Platform, unlimitedSettings, fixture.Assets);
        await unlimitedSession.StartAsync();
        await unlimitedProbe.NextAsync();
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("zero-timeout")]);
        await unlimitedProbe.NextAsync();
        await unlimitedSession.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task WebhookRestartDropsEventsQueuedBeforeStop()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        await using var probe = new WebhookProbe(
            ProbeResponse.Empty(204),
            new ProbeResponse(HttpStatusCode.NoContent, null, [], TimeSpan.FromSeconds(2)));
        var settings = Settings(probe.Endpoint) with { OneBotWebhookTimeout = TimeSpan.Zero };
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        await using var session = new ProtocolSession(protocol, fixture.Platform, settings, fixture.Assets);
        await session.StartAsync();

        var emitted = WaitForOutboundEventsAsync(session, 2);
        await probe.NextAsync();
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("before-stop-one")]);
        await probe.NextAsync();
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("before-stop-two")]);
        await emitted;
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await session.StartAsync();
        await probe.NextAsync();
        await fixture.Platform.SendMessageAsync(
            ChatScene.Group,
            ProtocolTestFixture.GroupId,
            ProtocolTestFixture.SenderId,
            ProtocolTestFixture.SelfId,
            [new TextSegment("after-restart")]);
        var request = await probe.NextAsync();
        Assert.AreEqual("after-restart", EventText(request));
    }

    private static ConnectionSettings Settings(params Uri[] endpoints) => new()
    {
        Host = "127.0.0.1",
        Port = ProtocolTestFixture.ReserveEphemeralPort(),
        Transport = TransportMode.OneBotHttpServer,
        AccessToken = AccessToken,
        OneBotWebhookUrls = endpoints.Select(static endpoint => endpoint.AbsoluteUri).ToArray(),
        OneBotWebhookTimeout = TimeSpan.FromSeconds(2),
    };

    private static JsonObject Action(string action, string name) => new()
    {
        ["action"] = action,
        ["params"] = new JsonObject
        {
            ["group_id"] = ProtocolTestFixture.GroupId,
            ["group_name"] = name,
        },
        ["echo"] = name,
    };

    private static string EventText(CapturedRequest request) =>
        JsonNode.Parse(request.Body)!["message"]![0]!["data"]!["text"]!.GetValue<string>();

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

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

    private sealed record ProbeResponse(HttpStatusCode StatusCode, string? ContentType, byte[] Body, TimeSpan Delay)
    {
        internal static ProbeResponse Json(int statusCode, JsonNode payload) =>
            new((HttpStatusCode)statusCode, "application/json", Encoding.UTF8.GetBytes(payload.ToJsonString()), TimeSpan.Zero);

        internal static ProbeResponse Empty(int statusCode) =>
            new((HttpStatusCode)statusCode, null, [], TimeSpan.Zero);
    }

    private sealed record CapturedRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed class WebhookProbe : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<ProbeResponse> _responses;
        private readonly ProbeResponse _fallbackResponse;
        private readonly Channel<CapturedRequest> _requests = Channel.CreateUnbounded<CapturedRequest>();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private Task? _acceptLoop;

        internal WebhookProbe(params ProbeResponse[] responses)
        {
            if (responses.Length == 0) throw new ArgumentException("At least one response is required.", nameof(responses));
            _responses = new ConcurrentQueue<ProbeResponse>(responses);
            _fallbackResponse = responses[^1];
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/webhook");
            _acceptLoop = AcceptLoopAsync();
        }

        internal Uri Endpoint { get; }

        internal async Task<CapturedRequest> NextAsync() =>
            await _requests.Reader.ReadAsync(_lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    var handler = HandleAsync(client);
                    _handlers.Add(handler);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var (request, _) = await ReadRequestAsync(stream, _lifetime.Token);
                    _requests.Writer.TryWrite(request);
                    var response = _responses.TryDequeue(out var next) ? next : _fallbackResponse;
                    if (response.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(response.Delay, _lifetime.Token);
                    }

                    var reason = response.StatusCode == HttpStatusCode.NoContent ? "No Content" : "OK";
                    var contentType = response.ContentType is null ? string.Empty : $"Content-Type: {response.ContentType}\r\n";
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {(int)response.StatusCode} {reason}\r\n{contentType}Content-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _lifetime.Token);
                    if (response.Body.Length > 0)
                    {
                        await stream.WriteAsync(response.Body, _lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                }
            }
        }

        private static async Task<(CapturedRequest Request, int BodyOffset)> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) throw new EndOfStreamException();
                bytes.AddRange(buffer.AsSpan(0, count).ToArray());
                headerEnd = FindHeaderEnd(bytes);
                if (bytes.Count > 64 * 1024) throw new InvalidDataException("Webhook request headers too large.");
            }

            var headerText = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("Content-Length", out var rawLength)
                ? int.Parse(rawLength, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyOffset = headerEnd + 4;
            while (bytes.Count - bodyOffset < contentLength)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) throw new EndOfStreamException();
                bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            }

            return (new CapturedRequest(
                requestLine[0],
                requestLine.Length > 1 ? requestLine[1] : string.Empty,
                headers,
                bytes.Skip(bodyOffset).Take(contentLength).ToArray()), bodyOffset);
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var index = 3; index < bytes.Count; index++)
            {
                if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n'
                    && bytes[index - 1] == '\r' && bytes[index] == '\n')
                {
                    return index - 3;
                }
            }

            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            _requests.Writer.TryComplete();
            if (_acceptLoop is not null)
            {
                await _acceptLoop.ConfigureAwait(false);
            }

            await Task.WhenAll(_handlers.ToArray()).ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }
}
