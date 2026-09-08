using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class ProtocolSession
{
    private const int OneBotWebhookQueueCapacity = 256;
    private const int OneBotWebhookActionLimit = 64;
    private const long OneBotWebhookQueueByteBudget = 16L * 1024 * 1024;
    private const int OneBotWebhookMaxResponseBytes = 4 * 1024 * 1024;
    private const string OneBotWebhookImplementationName = "asuka";
    private readonly object _oneBotWebhookSync = new();
    private List<OneBotWebhookTarget> _oneBotWebhookTargets = [];
    private CancellationTokenSource? _oneBotWebhookLifetime;
    private HttpClient? _oneBotWebhookClient;

    /// <summary>Creates one independent FIFO worker for every configured endpoint.</summary>
    private void ConfigureOneBotWebhooks(CancellationToken sessionToken)
    {
        if (_implementation is not OneBotProtocol oneBot
            || _settings.OneBotWebhookUrls.Count == 0)
        {
            return;
        }

        var endpoints = _settings.GetOneBotWebhookEndpoints();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var targets = endpoints
            .Select(endpoint => new OneBotWebhookTarget(endpoint, OneBotWebhookQueueCapacity))
            .ToList();
        lock (_oneBotWebhookSync)
        {
            _oneBotWebhookLifetime = lifetime;
            _oneBotWebhookClient = client;
            _oneBotWebhookTargets = targets;
            foreach (var target in targets)
            {
                target.Worker = RunOneBotWebhookAsync(target, oneBot, client, lifetime.Token);
            }
        }
    }

    /// <summary>
    /// Publishes the transport startup lifecycle event.  The task completes
    /// after each event has been queued; per-endpoint completion sources retain
    /// whether the HTTP delivery itself succeeded for the later V11 disable.
    /// </summary>
    private async Task PublishOneBotWebhookStartupAsync(CancellationToken cancellationToken)
    {
        if (_implementation is not OneBotProtocol oneBot)
        {
            return;
        }

        OneBotWebhookTarget[] targets;
        lock (_oneBotWebhookSync)
        {
            targets = _oneBotWebhookTargets.ToArray();
        }

        if (targets.Length == 0)
        {
            return;
        }

        JsonObject payload;
        if (oneBot.Version == OneBotVersion.V11)
        {
            payload = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["self_id"] = JsonExtensions.NumericId(oneBot.SelfId),
                ["post_type"] = "meta_event",
                ["meta_event_type"] = "lifecycle",
                ["sub_type"] = "enable",
            };
        }
        else
        {
            var handshake = await oneBot.GetHandshakeFramesAsync(cancellationToken).ConfigureAwait(false);
            payload = handshake
                .Select(static frame => frame.Payload)
                .FirstOrDefault(static candidate =>
                    candidate["type"]?.GetValue<string>() == "meta"
                    && candidate["detail_type"]?.GetValue<string>() == "status_update")
                ?.DeepClone().AsObject()
                ?? throw new InvalidOperationException("OneBot V12 status startup event is unavailable.");
        }

        byte[] body;
        try
        {
            body = Encoding.UTF8.GetBytes(payload.ToCompactJson());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach (var target in targets)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var packet = new OneBotWebhookPacket(body, completion);
            var queued = false;
            var dropped = false;
            lock (target.Sync)
            {
                while (target.PendingCount >= OneBotWebhookQueueCapacity
                    || target.PendingBytes + body.Length > OneBotWebhookQueueByteBudget)
                {
                    if (!target.Events.Reader.TryRead(out var evicted))
                    {
                        break;
                    }

                    target.PendingCount--;
                    target.PendingBytes -= evicted.Body.Length;
                    evicted.Completion?.TrySetResult(false);
                    dropped = true;
                }

                if (target.Events.Writer.TryWrite(packet))
                {
                    target.PendingCount++;
                    target.PendingBytes += body.Length;
                    target.StartupResult = completion;
                    queued = true;
                }
            }

            if (!queued)
            {
                completion.TrySetResult(false);
                dropped = true;
            }

            if (dropped)
            {
                ReportOneBotWebhookFailure(new InvalidOperationException(
                    "OneBot WebHook startup queue limit reached; oldest events were discarded."));
            }
        }
    }

    /// <summary>Enqueues an encoded event without allowing a slow endpoint to stall the pump.</summary>
    private void BufferOneBotWebhookEvent(OutboundFrame frame)
    {
        OneBotWebhookTarget[] targets;
        lock (_oneBotWebhookSync)
        {
            if (_oneBotWebhookTargets.Count == 0) return;
            targets = _oneBotWebhookTargets.ToArray();
        }

        byte[] body;
        try
        {
            body = Encoding.UTF8.GetBytes(frame.Payload.ToCompactJson());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReportOneBotWebhookFailure(new InvalidOperationException("OneBot WebHook event serialization failed."));
            return;
        }

        foreach (var target in targets)
        {
            var dropped = false;
            lock (target.Sync)
            {
                if (body.Length > OneBotWebhookQueueByteBudget)
                {
                    dropped = true;
                }
                else
                {
                    while (target.PendingCount >= OneBotWebhookQueueCapacity
                        || target.PendingBytes + body.Length > OneBotWebhookQueueByteBudget)
                    {
                        if (!target.Events.Reader.TryRead(out var evicted))
                        {
                            break;
                        }

                        target.PendingCount--;
                        target.PendingBytes -= evicted.Body.Length;
                        evicted.Completion?.TrySetResult(false);
                        dropped = true;
                    }

                    var packet = new OneBotWebhookPacket(body);
                    if (target.Events.Writer.TryWrite(packet))
                    {
                        target.PendingCount++;
                        target.PendingBytes += body.Length;
                    }
                    else
                    {
                        dropped = true;
                    }
                }
            }

            if (dropped)
            {
                ReportOneBotWebhookFailure(new InvalidOperationException(
                    "OneBot WebHook event queue limit reached; oldest events were discarded."));
            }
        }
    }

    /// <summary>Cancels workers, waits for in-flight sends, then drains each queue.</summary>
    private async Task StopOneBotWebhooksAsync()
    {
        OneBotWebhookTarget[] targets;
        CancellationTokenSource? lifetime;
        HttpClient? client;
        var oneBot = _implementation as OneBotProtocol;
        lock (_oneBotWebhookSync)
        {
            targets = _oneBotWebhookTargets.ToArray();
            _oneBotWebhookTargets = [];
            lifetime = _oneBotWebhookLifetime;
            _oneBotWebhookLifetime = null;
            client = _oneBotWebhookClient;
            _oneBotWebhookClient = null;
        }

        lifetime?.Cancel();
        foreach (var target in targets)
        {
            target.Events.Writer.TryComplete();
        }

        var workers = targets
            .Select(static target => target.Worker)
            .Where(static worker => worker is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                foreach (var target in targets)
                {
                    lock (target.Sync)
                    {
                        while (target.Events.Reader.TryRead(out var pending))
                        {
                            pending.Completion?.TrySetResult(false);
                        }

                        target.PendingCount = 0;
                        target.PendingBytes = 0;
                    }
                }

                if (oneBot?.Version == OneBotVersion.V11 && client is not null)
                {
                    var disableTargets = targets
                        .Where(static target => target.StartupResult?.Task is { IsCompletedSuccessfully: true })
                        .Where(static target => target.StartupResult!.Task.Result)
                        .ToArray();
                    if (disableTargets.Length > 0)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var disableBody = Encoding.UTF8.GetBytes(new JsonObject
                        {
                            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["self_id"] = JsonExtensions.NumericId(oneBot.SelfId),
                            ["post_type"] = "meta_event",
                            ["meta_event_type"] = "lifecycle",
                            ["sub_type"] = "disable",
                        }.ToCompactJson());
                        var disables = disableTargets.Select(target => SendOneBotWebhookAsync(
                            target.Endpoint,
                            new OneBotWebhookPacket(disableBody),
                            oneBot,
                            deadline.Token,
                            clientOverride: client,
                            timeoutOverride: TimeSpan.FromSeconds(2),
                            processResponse: false));
                        try
                        {
                            await Task.WhenAll(disables).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                        }
                    }
                }

            }
            finally
            {
                lifetime?.Dispose();
                client?.Dispose();
            }
        }
    }

    private async Task RunOneBotWebhookAsync(
        OneBotWebhookTarget target,
        OneBotProtocol oneBot,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await target.Events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (TryDequeueOneBotWebhook(target, out var packet))
                {
                    var delivered = false;
                    try
                    {
                        await SendOneBotWebhookAsync(target.Endpoint, packet, oneBot, cancellationToken, clientOverride: client).ConfigureAwait(false);
                        delivered = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OneBotWebhookDeliveryException error)
                    {
                        ReportOneBotWebhookFailure(error);
                    }
                    catch (OperationCanceledException)
                    {
                        ReportOneBotWebhookFailure(new TimeoutException("OneBot WebHook delivery timed out."));
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        // Framework exceptions can include a URI, header value or
                        // remote JSON. Only application-owned diagnostics reach logs.
                        ReportOneBotWebhookFailure(new InvalidOperationException("OneBot WebHook delivery or response processing failed."));
                    }
                    finally
                    {
                        packet.Completion?.TrySetResult(delivered);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (target.Sync)
            {
                while (target.Events.Reader.TryRead(out var pending))
                {
                    pending.Completion?.TrySetResult(false);
                }

                target.PendingCount = 0;
                target.PendingBytes = 0;
            }
        }
    }

    private static bool TryDequeueOneBotWebhook(
        OneBotWebhookTarget target,
        out OneBotWebhookPacket packet)
    {
        lock (target.Sync)
        {
            if (!target.Events.Reader.TryRead(out var candidate) || candidate is null)
            {
                packet = null!;
                return false;
            }

            packet = candidate;
            target.PendingCount--;
            target.PendingBytes -= packet.Body.Length;
            return true;
        }
    }

    private async Task SendOneBotWebhookAsync(
        Uri endpoint,
        OneBotWebhookPacket packet,
        OneBotProtocol oneBot,
        CancellationToken cancellationToken,
        HttpClient? clientOverride = null,
        TimeSpan? timeoutOverride = null,
        bool processResponse = true)
    {
        var body = packet.Body;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (oneBot.Version == OneBotVersion.V11)
        {
            request.Headers.TryAddWithoutValidation("X-Self-ID", oneBot.SelfId);
            if (!string.IsNullOrEmpty(_settings.OneBotWebhookSecret))
            {
#pragma warning disable CA5350 // OneBot V11's official HTTP POST contract requires HMAC-SHA1.
                var signature = HMACSHA1.HashData(
                    Encoding.UTF8.GetBytes(_settings.OneBotWebhookSecret),
                    body);
#pragma warning restore CA5350
                request.Headers.TryAddWithoutValidation(
                    "X-Signature",
                    $"sha1={Convert.ToHexString(signature).ToLowerInvariant()}");
            }
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", $"OneBot/12 ({OneBotWebhookImplementationName})");
            request.Headers.TryAddWithoutValidation("X-OneBot-Version", "12");
            request.Headers.TryAddWithoutValidation("X-Impl", OneBotWebhookImplementationName);
            if (!string.IsNullOrEmpty(_settings.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var configuredTimeout = timeoutOverride ?? _settings.OneBotWebhookTimeout;
        if (configuredTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(configuredTimeout);
        }

        var client = clientOverride ?? _oneBotWebhookClient
            ?? throw new OneBotWebhookDeliveryException("The OneBot WebHook client is not configured.");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            packet.Completion?.TrySetResult(true);
            return;
        }

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new OneBotWebhookDeliveryException($"OneBot WebHook returned HTTP {(int)response.StatusCode}.");
        }
        packet.Completion?.TrySetResult(true);

        if (!processResponse)
        {
            return;
        }

        var responseBody = await ReadOneBotWebhookResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (oneBot.Version == OneBotVersion.V11)
        {
            if (responseBody.Length != 0)
            {
                await ProcessV11QuickOperationAsync(oneBot, body, responseBody, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await ProcessV12WebhookActionsAsync(oneBot, response, responseBody, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadOneBotWebhookResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > OneBotWebhookMaxResponseBytes)
        {
            throw new OneBotWebhookDeliveryException("OneBot WebHook response exceeded the response size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (result.Length > OneBotWebhookMaxResponseBytes - read)
            {
                throw new OneBotWebhookDeliveryException("OneBot WebHook response exceeded the response size limit.");
            }

            await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return result.ToArray();
    }

    private async Task ProcessV11QuickOperationAsync(
        OneBotProtocol oneBot,
        byte[] eventBody,
        byte[] responseBody,
        CancellationToken cancellationToken)
    {
        JsonObject eventPayload;
        JsonObject operation;
        try
        {
            eventPayload = JsonNode.Parse(eventBody) as JsonObject
                ?? throw new JsonException("Expected an event object.");
            operation = JsonNode.Parse(responseBody) as JsonObject
                ?? throw new JsonException("Expected a quick-operation object.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new OneBotWebhookDeliveryException("OneBot V11 WebHook returned invalid JSON quick operations.");
        }

        var results = await oneBot.HandleQuickOperationAsync(
            (JsonObject)eventPayload.DeepClone(),
            (JsonObject)operation.DeepClone(),
            cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.RetCode != 0)
            {
                ReportOneBotWebhookFailure(
                    new InvalidOperationException(
                        $"OneBot quick operation {result.Action} failed with code {result.RetCode}."));
            }
        }
    }

    private async Task ProcessV12WebhookActionsAsync(
        OneBotProtocol oneBot,
        HttpResponseMessage response,
        byte[] responseBody,
        CancellationToken cancellationToken)
    {
        var mediaType = RequestMediaType(response.Content.Headers.ContentType?.ToString());
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new OneBotWebhookDeliveryException("OneBot V12 WebHook returned an unsupported Content-Type.");
        }

        JsonArray actions;
        try
        {
            actions = JsonNode.Parse(responseBody) as JsonArray
                ?? throw new JsonException("Expected an action request list.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new OneBotWebhookDeliveryException("OneBot V12 WebHook returned invalid JSON actions.");
        }

        if (actions.Count > OneBotWebhookActionLimit)
        {
            throw new OneBotWebhookDeliveryException("OneBot V12 WebHook returned too many actions.");
        }

        foreach (var node in actions)
        {
            if (node is not JsonObject actionRequest)
            {
                ReportOneBotWebhookFailure(new InvalidOperationException("OneBot V12 WebHook returned a non-object action."));
                continue;
            }

            if (OneBotV12ActionRequest.Validate(actionRequest, out var call) is { } invalidRequest)
            {
                ReportOneBotWebhookActionFailure("invalid", invalidRequest.RetCode);
                continue;
            }

            var action = call!.Name;
            if (ValidateV12Self(actionRequest) is { } selfFailure)
            {
                ReportOneBotWebhookActionFailure(action, selfFailure.RetCode);
                continue;
            }

            ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, actionRequest));
            var reply = await oneBot.HandleAsync(
                call,
                cancellationToken).ConfigureAwait(false);
            ObserveTraffic(new TrafficEntry(
                TrafficDirection.Reply,
                $"{action} → {reply.RetCode}",
                oneBot.CreateEnvelope(reply, call.Echo)));
            if (reply.RetCode is not (0 or 1))
            {
                ReportOneBotWebhookActionFailure(action, reply.RetCode);
            }
        }
    }

    private void ReportOneBotWebhookActionFailure(string action, int retCode)
    {
        var label = _implementation is OneBotProtocol oneBot && oneBot.IsSupportedAction(action) ? action : "unsupported";
        ReportOneBotWebhookFailure(new InvalidOperationException(
            $"OneBot WebHook action {label} failed with code {retCode}."));
    }

    private void ReportOneBotWebhookFailure(Exception error)
    {
        if (_oneBotWebhookLifetime?.IsCancellationRequested != true)
        {
            try
            {
                OutboundDeliveryFailed?.Invoke(this, error);
            }
            catch (Exception observerError) when (observerError is not OutOfMemoryException)
            {
            }
        }
    }

    private sealed class OneBotWebhookTarget
    {
        internal OneBotWebhookTarget(Uri endpoint, int capacity)
        {
            Endpoint = endpoint;
            Events = Channel.CreateBounded<OneBotWebhookPacket>(new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        }

        internal Uri Endpoint { get; }

        internal Channel<OneBotWebhookPacket> Events { get; }

        internal object Sync { get; } = new();

        internal int PendingCount { get; set; }

        internal long PendingBytes { get; set; }

        internal Task? Worker { get; set; }

        internal TaskCompletionSource<bool>? StartupResult { get; set; }
    }

    private sealed record OneBotWebhookPacket(
        byte[] Body,
        TaskCompletionSource<bool>? Completion = null);
    private sealed class OneBotWebhookDeliveryException(string message) : Exception(message) { }
}
