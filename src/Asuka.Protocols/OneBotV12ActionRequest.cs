using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asuka.Protocols;

/// <summary>
/// Validates the OneBot V12 action-request envelope shared by every Connect
/// transport.  Transport-specific self selection is deliberately left to the
/// caller, because it depends on the connection's account topology.
/// </summary>
internal static class OneBotV12ActionRequest
{
    internal const string BadRequestMessage = "Invalid action request.";

    /// <summary>
    /// Returns a V12 <c>10001 Bad Request</c> response for a malformed action
    /// envelope, or <see langword="null"/> together with an isolated call.
    /// </summary>
    internal static ProtocolReply? Validate(JsonNode? payload, out ProtocolCall? call)
    {
        call = null;

        try
        {
            if (payload is not JsonObject request
                || !TryMaterialize(payload, depth: 1)
                || !TryGetRequiredString(request, "action", out var action)
                || request["params"] is not JsonObject parameters
                || !TryGetEcho(request, out var echo))
            {
                return BadRequest();
            }

            // A parsed JsonNode can retain deferred JSON materialization.  The
            // complete tree was traversed above, so malformed nested duplicate
            // properties cannot later escape through traffic recording, self
            // validation, or an action handler.
            call = new ProtocolCall(
                action,
                (JsonObject)parameters.DeepClone(),
                echo?.DeepClone());
            return null;
        }
        catch (ArgumentException)
        {
            // System.Text.Json.Nodes throws this for duplicate object keys when a
            // deferred object is materialized.
            return BadRequest();
        }
        catch (JsonException)
        {
            return BadRequest();
        }
    }

    private static bool TryGetRequiredString(JsonObject request, string name, out string value)
    {
        value = string.Empty;
        if (request[name] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text)) return false;
        value = text;
        return true;
    }

    private static bool TryGetEcho(JsonObject request, out JsonNode? echo)
    {
        echo = null;
        if (!request.ContainsKey("echo"))
        {
            return true;
        }

        if (request["echo"] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text))
        {
            return false;
        }

        echo = JsonValue.Create(text);
        return true;
    }

    private static bool TryMaterialize(JsonNode node, int depth)
    {
        // JsonDocument defaults to 64 nested containers.  JsonNode instances can
        // also be constructed in-process, so retain the same bound while walking
        // instead of risking unbounded recursive traversal.
        if (depth > 64)
        {
            return false;
        }

        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    if (property.Value is not null && !TryMaterialize(property.Value, depth + 1))
                    {
                        return false;
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    if (item is not null && !TryMaterialize(item, depth + 1))
                    {
                        return false;
                    }
                }

                break;
        }

        return true;
    }

    private static ProtocolReply BadRequest() => new(10001, Message: BadRequestMessage);
}
