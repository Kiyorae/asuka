using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private async Task<ProtocolReply> GroupAnonymousApiAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var groupId = call.GetId("group_id");
        if (groupId is null || !long.TryParse(groupId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericGroupId)
            || numericGroupId <= 0) return Invalid("Missing or invalid group_id");

        if (call.Name == "set_group_anonymous")
        {
            var enable = true;
            if (call.Parameters.ContainsKey("enable"))
            {
                // Query/form transports necessarily supply strings; JSON boolean
                // and its transport representation have identical semantics.
                if (call.Parameters["enable"] is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)) enable = boolean;
                else if (call.Parameters["enable"] is JsonValue text && text.TryGetValue<string>(out var value)
                    && bool.TryParse(value, out boolean)) enable = boolean;
                else return Invalid("Invalid enable boolean");
            }
            await _platform.SetGroupAnonymousAsync(groupId, SelfId, enable, cancellationToken).ConfigureAwait(false);
            return new ProtocolReply();
        }

        AnonymousIdentity? supplied = null;
        string flag;
        // Presence, not validity, determines precedence. Never let a malformed
        // anonymous object silently select a different flag target.
        if (call.Parameters.ContainsKey("anonymous"))
        {
            if (call.Parameters["anonymous"] is not JsonObject anonymous || !TryAnonymousIdentity(anonymous, out supplied))
                return Invalid("Invalid anonymous identity");
            flag = supplied!.Flag;
        }
        else
        {
            var key = call.Parameters.ContainsKey("anonymous_flag") ? "anonymous_flag" : "flag";
            if (!TryAnonymousText(call.Parameters, key, out flag)) return Invalid("Missing or invalid anonymous flag");
        }

        var duration = AnonymousDuration(call.Parameters);
        if (duration is null) return Invalid("Anonymous ban duration must be positive; anonymous bans cannot be cancelled");
        if (supplied is not null)
        {
            var existing = await _store.GetAnonymousIdentityAsync(groupId, flag, cancellationToken).ConfigureAwait(false);
            if (existing != supplied) return Invalid("Anonymous identity does not match this group");
        }
        await _platform.BanAnonymousAsync(groupId, flag, SelfId, duration.Value, cancellationToken).ConfigureAwait(false);
        return new ProtocolReply();
    }

    private static TimeSpan? AnonymousDuration(JsonObject parameters)
    {
        if (!parameters.ContainsKey("duration")) return TimeSpan.FromMinutes(30);
        if (parameters["duration"] is not JsonValue scalar) return null;
        var text = scalar.TryGetValue<string>(out var transportText) ? transportText
            : scalar.GetValueKind() == JsonValueKind.Number ? scalar.ToJsonString() : null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0
            || seconds > (DateTimeOffset.MaxValue - DateTimeOffset.UtcNow).TotalSeconds) return null;
        var duration = TimeSpan.FromSeconds(seconds);
        return duration > TimeSpan.Zero ? duration : null;
    }

    private static bool TryAnonymousIdentity(JsonObject value, out AnonymousIdentity? identity)
    {
        identity = null;
        if (value["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.Number
            || !long.TryParse(id.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0
            || !TryAnonymousText(value, "name", out var name) || !TryAnonymousText(value, "flag", out var flag)) return false;
        identity = new AnonymousIdentity(numericId, name, flag);
        return true;
    }

    private static bool TryAnonymousText(JsonObject value, string key, out string text)
    {
        text = string.Empty;
        if (value[key] is not JsonValue scalar || !scalar.TryGetValue<string>(out var found) || string.IsNullOrEmpty(found)) return false;
        text = found;
        return true;
    }
}

internal static class OneBotAnonymousEncoder
{
    internal static JsonObject Identity(AnonymousIdentity identity) => new()
    {
        ["id"] = identity.Id,
        ["name"] = identity.Name,
        ["flag"] = identity.Flag,
    };

    // V11 explicitly says sender fields are unreliable for anonymous messages.
    // Populate only the synthetic identity and never look up the real member.
    internal static JsonObject Sender(AnonymousIdentity identity) => new()
    {
        ["user_id"] = identity.Id,
        ["nickname"] = identity.Name,
        ["sex"] = "unknown",
        ["age"] = 0,
    };
}
