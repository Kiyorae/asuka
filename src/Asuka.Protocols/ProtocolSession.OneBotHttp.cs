using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using Microsoft.AspNetCore.Http;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class ProtocolSession
{
    private OneBotEventBuffer _oneBotHttpEvents = new();
    private readonly SemaphoreSlim _oneBotHttpPollGate = new(4, 4);
    private bool _oneBotHttpEventsEnabled = true;

    /// <summary>Applies the V12 HTTP polling settings before the listener starts.</summary>
    private void ConfigureOneBotHttpEvents(bool enabled, int bufferSize)
    {
        _oneBotHttpEventsEnabled = enabled;
        _oneBotHttpEvents = new OneBotEventBuffer(bufferSize);
    }

    /// <summary>
    /// Handles the OneBot HTTP communication mode.  V11 uses the legacy
    /// action-in-path wire format; V12 uses the Connect action envelope.
    /// </summary>
    private async Task HandleOneBotHttpRequestAsync(HttpContext context, CancellationToken sessionToken)
    {
        if (_implementation is not OneBotProtocol oneBot)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (RejectBrowserOrigin(context))
        {
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken)
                && !IsScopedDownloadAuthorized(context.Request, path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await HandleAssetRequestAsync(context, path[8..], sessionToken).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/files/", StringComparison.Ordinal))
        {
            if (!TokenAuthentication.IsBearerAuthorized(context.Request, _settings.AccessToken)
                && !IsScopedDownloadAuthorized(context.Request, path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await HandleSharedFileRequestAsync(context, path[7..], sessionToken).ConfigureAwait(false);
            return;
        }

        if (oneBot.Version == OneBotVersion.V11)
        {
            await HandleOneBotV11HttpRequestAsync(context, sessionToken).ConfigureAwait(false);
            return;
        }

        await HandleOneBotV12HttpRequestAsync(context, sessionToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the event pump for every encoded event.  The V12 HTTP queue
    /// is deliberately independent from WebSocket and WebHook delivery.
    /// </summary>
    private void BufferOneBotHttpEvent(OutboundFrame frame)
    {
        if (!_oneBotHttpEventsEnabled
            || _implementation is not OneBotProtocol { Version: OneBotVersion.V12 }
            || frame.Payload["type"]?.GetValue<string>() is "meta")
        {
            return;
        }

        _oneBotHttpEvents.Enqueue(frame.Payload);
    }

    /// <summary>Clears unconsumed V12 HTTP polling events between sessions.</summary>
    private void ResetOneBotHttpEvents() => _oneBotHttpEvents.Reset();

    private async Task HandleOneBotV11HttpRequestAsync(
        HttpContext context,
        CancellationToken sessionToken)
    {
        var authorizationStatus = OneBotHttpAuthorizationStatus(context.Request);
        if (authorizationStatus != StatusCodes.Status200OK)
        {
            context.Response.StatusCode = authorizationStatus;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var path = (context.Request.Path.Value ?? "/").TrimEnd('/');
        if (path.Length <= 1 || path[1..].Contains('/', StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var action = path[1..];
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        var requestToken = requestCancellation.Token;
        JsonObject parameters;
        try
        {
            parameters = HttpMethods.IsGet(context.Request.Method)
                ? QueryParameters(context.Request.Query)
                : await ReadV11ParametersAsync(context, requestToken).ConfigureAwait(false);
        }
        catch (UnsupportedV11ContentTypeException)
        {
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            return;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, parameters));
        using var restartResponse = (_implementation as OneBotProtocol)?.BeginRestartResponse();
        ProtocolReply reply;
        try
        {
            requestToken.ThrowIfCancellationRequested();
            reply = await _implementation.HandleAsync(
                new ProtocolCall(action, parameters),
                IsV11DeferredAction(action) ? sessionToken : requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            reply = new ProtocolReply(1000, Message: "The protocol session is stopping.");
        }

        // V11 maps an unknown API path to HTTP 404.  A valid action whose
        // target resource is missing remains an HTTP 200 action failure.
        if (reply.RetCode == 1404
            && reply.Message.StartsWith("Unsupported action:", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await WriteOneBotJsonReplyAsync(context, reply, action: action).ConfigureAwait(false);
        // Release the restart only after the async acknowledgement has reached
        // the response transport (or this request has failed/disconnected).
        if (reply.RetCode == 1 && action.StartsWith("set_restart", StringComparison.Ordinal))
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleOneBotV12HttpRequestAsync(
        HttpContext context,
        CancellationToken sessionToken)
    {
        if (context.Request.Path.Value is not "/")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (OneBotHttpAuthorizationStatus(context.Request) != StatusCodes.Status200OK)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var mediaType = RequestMediaType(context.Request.ContentType);
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, context.RequestAborted);
        var requestToken = requestCancellation.Token;
        JsonObject request;
        try
        {
            request = await JsonNode.ParseAsync(
                    context.Request.Body,
                    cancellationToken: requestToken).ConfigureAwait(false) as JsonObject
                ?? throw new JsonException("Expected a JSON object.");
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
            await WriteOneBotJsonReplyAsync(
                context,
                new ProtocolReply(10001, Message: error.Message)).ConfigureAwait(false);
            return;
        }

        if (OneBotV12ActionRequest.Validate(request, out var call) is { } requestFailure)
        {
            await WriteOneBotJsonReplyAsync(
                context, requestFailure, ParseV12Echo(request)).ConfigureAwait(false);
            return;
        }
        var action = call!.Name;
        var parameters = call.Parameters;
        var echo = call.Echo;

        ObserveTraffic(new TrafficEntry(TrafficDirection.InboundCall, action, request));
        ProtocolReply reply;
        try
        {
            if (ValidateV12Self(request) is { } selfFailure)
            {
                reply = selfFailure;
            }
            else if (action == "get_latest_events")
            {
                reply = await GetLatestEventsAsync(parameters, sessionToken, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                reply = await _implementation.HandleAsync(
                    call,
                    requestToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            // Once the HTTP body has been read, V12 reports failures in an action
            // envelope. Graceful shutdown must not turn an active poll into HTTP 500.
            reply = new ProtocolReply(20002, Message: "The protocol session is stopping.");
        }

        await WriteOneBotJsonReplyAsync(context, reply, echo, action).ConfigureAwait(false);
    }

    private async Task<ProtocolReply> GetLatestEventsAsync(
        JsonObject parameters,
        CancellationToken sessionToken,
        CancellationToken cancellationToken)
    {
        if (!_oneBotHttpEventsEnabled)
        {
            return new ProtocolReply(10002, Message: "get_latest_events is disabled.");
        }

        if (!TryV12NonnegativeInt64(parameters, "limit", out var limit)
            || !TryV12NonnegativeInt64(parameters, "timeout", out var timeout))
        {
            return new ProtocolReply(10003, Message: "limit and timeout must be non-negative integers.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        if (!await _oneBotHttpPollGate.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return new ProtocolReply(10004, Message: "Another get_latest_events request is already waiting.");
        }

        try
        {
            var events = await _oneBotHttpEvents.ReadAsync(limit, timeout, linked.Token).ConfigureAwait(false);
            var data = new JsonArray();
            foreach (var item in events)
            {
                data.Add(item);
            }

            return ProtocolReply.Success(data);
        }
        catch (ArgumentOutOfRangeException error)
        {
            return new ProtocolReply(10003, Message: error.ParamName == "timeoutSeconds"
                ? $"timeout must be between 0 and {OneBotEventBuffer.MaximumPollSeconds} seconds."
                : "limit is out of range.");
        }
        finally
        {
            _oneBotHttpPollGate.Release();
        }
    }

    private static async Task<JsonObject> ReadV11ParametersAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var mediaType = RequestMediaType(context.Request.ContentType);
        if (context.Request.ContentLength == 0 && string.IsNullOrWhiteSpace(mediaType))
        {
            return new JsonObject();
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return await JsonNode.ParseAsync(
                    context.Request.Body,
                    cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new JsonException("Expected a JSON object.");
        }

        if (string.Equals(mediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            var parameters = new JsonObject();
            foreach (var (key, values) in form)
            {
                parameters[key] = values.LastOrDefault() ?? string.Empty;
            }

            return parameters;
        }

        throw new UnsupportedV11ContentTypeException();
    }

    private static JsonObject QueryParameters(IQueryCollection query)
    {
        var parameters = new JsonObject();
        foreach (var (key, values) in query)
        {
            if (!key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            {
                parameters[key] = values.LastOrDefault() ?? string.Empty;
            }
        }

        return parameters;
    }

    private static string? RequestMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        return contentType.Split(';', 2)[0].Trim();
    }

    private static JsonNode? ParseV12Echo(JsonObject request)
    {
        try
        {
            return request["echo"] is JsonValue value && value.TryGetValue<string>(out var echo) ? echo : null;
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return null; }
    }

    private static bool TryV12NonnegativeInt64(JsonObject parameters, string name, out long value)
    {
        value = 0;
        if (!parameters.ContainsKey(name))
        {
            return true;
        }
        if (parameters[name] is not JsonValue scalar) return false;

        if (scalar.TryGetValue<long>(out value))
        {
            return value >= 0;
        }

        if (scalar.GetValueKind() == JsonValueKind.Number
            && long.TryParse(scalar.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value >= 0;
        }

        return false;
    }

    private ProtocolReply? ValidateV12Self(JsonObject request)
    {
        if (!request.ContainsKey("self")
            || request.GetFlexibleString("action") is "get_latest_events" or "get_supported_actions" or "get_status" or "get_version")
            return null;
        if (request["self"] is not JsonObject self
            || self["platform"] is not JsonValue platformValue || !platformValue.TryGetValue<string>(out var platform)
            || self["user_id"] is not JsonValue userValue || !userValue.TryGetValue<string>(out var userId))
            return new ProtocolReply(10001, Message: "self must contain string platform and user_id.");
        return platform == "qq" && userId == _implementation.SelfId
            ? null : new ProtocolReply(10102, Message: "The requested bot account is not available on this connection.");
    }

    private async Task WriteOneBotJsonReplyAsync(
        HttpContext context,
        ProtocolReply reply,
        JsonNode? echo = null,
        string? action = null)
    {
        var envelope = _implementation.CreateEnvelope(reply, echo);
        if (action is not null)
        {
            ObserveTraffic(new TrafficEntry(TrafficDirection.Reply, $"{action} → {reply.RetCode}", envelope));
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(envelope.ToCompactJson(), context.RequestAborted).ConfigureAwait(false);
    }

    private int OneBotHttpAuthorizationStatus(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_settings.AccessToken))
        {
            return StatusCodes.Status200OK;
        }

        if (request.Headers.ContainsKey("Authorization"))
        {
            var authorizationValues = request.Headers.Authorization;
            var value = authorizationValues.Count == 1 ? authorizationValues[0] : null;
            const string prefix = "Bearer ";
            var presented = value?.StartsWith(prefix, StringComparison.Ordinal) == true
                ? value[prefix.Length..]
                : null;
            return FixedTokenEquals(_settings.AccessToken, presented)
                ? StatusCodes.Status200OK
                : StatusCodes.Status403Forbidden;
        }

        if (!request.Query.TryGetValue("access_token", out var values) || values.Count != 1)
        {
            return StatusCodes.Status401Unauthorized;
        }

        return FixedTokenEquals(_settings.AccessToken, values[0])
            ? StatusCodes.Status200OK
            : StatusCodes.Status403Forbidden;
    }

    private static bool FixedTokenEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool IsV11DeferredAction(string action) =>
        action.EndsWith("_async", StringComparison.Ordinal)
        || action.EndsWith("_rate_limited", StringComparison.Ordinal);

    private sealed class UnsupportedV11ContentTypeException : Exception { }
}
