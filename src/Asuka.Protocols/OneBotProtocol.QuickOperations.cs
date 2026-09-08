using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    /// <summary>
    /// Applies a V11 HTTP POST response to the exact event that was posted.
    /// Results are local diagnostics; they must not be emitted as extra API replies.
    /// </summary>
    internal async Task<IReadOnlyList<OneBotQuickOperationResult>> HandleQuickOperationAsync(
        JsonObject originalEvent,
        JsonObject operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Version != OneBotVersion.V11) return [new("quick_operation", 10002)];
        try
        {
            // Webhook targets can execute concurrently. Retain immutable snapshots
            // of this response and its original event across asynchronous store reads.
            var context = (JsonObject)originalEvent.DeepClone();
            var commands = (JsonObject)operation.DeepClone();
            ValidateQuickOperationTypes(commands);
            var messageAction = commands.ContainsKey("reply") || QuickFlag(commands, "delete")
                || QuickFlag(commands, "ban") || QuickFlag(commands, "kick");
            var requestAction = commands.ContainsKey("approve");
            if (!messageAction && !requestAction) return [];
            if (QuickId(context, "self_id") != SelfId) return QuickRejected();
            if (!IsAccountOnline) return [new("quick_operation", 1403)];
            // This scope begins after the webhook response has been received.
            // Each actual mutation rechecks login after acquiring the platform gate.
            using var botAction = _platform.BeginBotAction(SelfId);

            return QuickString(context, "post_type") switch
            {
                "message" when messageAction && !requestAction =>
                    await HandleMessageQuickOperationsAsync(context, commands, cancellationToken).ConfigureAwait(false),
                "request" when requestAction && !messageAction =>
                    await HandleRequestQuickOperationAsync(context, commands, cancellationToken).ConfigureAwait(false),
                _ => QuickRejected(),
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        {
            return QuickRejected();
        }
        catch (PlatformException error)
        {
            return [new("quick_operation", Failure(error).RetCode)];
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return [new("quick_operation", 1000)];
        }
    }

    private async Task<IReadOnlyList<OneBotQuickOperationResult>> HandleMessageQuickOperationsAsync(
        JsonObject context, JsonObject commands, CancellationToken cancellationToken)
    {
        var type = QuickString(context, "message_type");
        if (type is not ("group" or "private")) return QuickRejected();
        var group = type == "group";
        if (!group && (QuickFlag(commands, "delete") || QuickFlag(commands, "ban") || QuickFlag(commands, "kick")))
            return QuickRejected();
        var messageId = QuickId(context, "message_id", positive: false);
        var senderId = QuickId(context, "user_id");
        var groupId = group ? QuickId(context, "group_id") : null;
        var message = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.SelfId != SelfId
            || (message.Scene == ChatScene.Group) != group
            || message.PeerId != (group ? groupId : senderId))
            return QuickRejected();

        var anonymous = message.Anonymous;
        if (anonymous is not null)
        {
            if (!group || QuickString(context, "sub_type") != "anonymous"
                || context["anonymous"] is not JsonObject identity || !TryAnonymousIdentity(identity, out var supplied)
                || supplied != anonymous || senderId != anonymous.Id.ToString(CultureInfo.InvariantCulture)) return QuickRejected();
        }
        else if (message.SenderId != senderId || context["anonymous"] is not null
            || (group && QuickString(context, "sub_type") == "anonymous")) return QuickRejected();

        var results = new List<OneBotQuickOperationResult>();
        if (commands.ContainsKey("reply"))
        {
            results.Add(await ExecuteQuickOperationAsync(group ? "send_group_msg" : "send_private_msg", async () =>
            {
                var segments = QuickReplySegments(commands, group && anonymous is null, senderId);
                var content = await _segments.DecodeAsync(segments, cancellationToken).ConfigureAwait(false);
                // Keeping the original scene also preserves temporary private chats.
                await _platform.SendMessageAsync(message.Scene, message.PeerId, SelfId, SelfId, content, cancellationToken)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false));
        }
        if (QuickFlag(commands, "delete"))
        {
            var reply = await ExecuteCoreAsync(new ProtocolCall("delete_msg", new JsonObject { ["message_id"] = message.Id }),
                cancellationToken).ConfigureAwait(false);
            results.Add(new("delete_msg", reply.RetCode));
        }
        // A ban precedes a kick when both are requested, so kicking does not make
        // the independent mute action target a member that was just removed.
        if (QuickFlag(commands, "ban"))
        {
            var duration = commands.ContainsKey("ban_duration") ? QuickDuration(commands["ban_duration"]) : 1800;
            results.Add(anonymous is null
                ? await ExecuteQuickOperationAsync("set_group_ban", () =>
                    _platform.MuteMemberAsync(groupId!, senderId, SelfId, duration, cancellationToken), cancellationToken).ConfigureAwait(false)
                : await ExecuteQuickOperationAsync("set_group_anonymous_ban", () =>
                    _platform.BanAnonymousAsync(groupId!, anonymous.Flag, SelfId, TimeSpan.FromSeconds(duration), cancellationToken), cancellationToken).ConfigureAwait(false));
        }
        // V11 explicitly defines kick and automatic at_sender as ineffective for
        // anonymous messages. Never translate the alias into a real group member.
        if (QuickFlag(commands, "kick") && anonymous is null)
        {
            var reply = await ExecuteCoreAsync(new ProtocolCall("set_group_kick", new JsonObject
            {
                ["group_id"] = groupId,
                ["user_id"] = senderId,
                ["reject_add_request"] = false,
            }), cancellationToken).ConfigureAwait(false);
            results.Add(new("set_group_kick", reply.RetCode));
        }
        return results;
    }

    private async Task<IReadOnlyList<OneBotQuickOperationResult>> HandleRequestQuickOperationAsync(
        JsonObject context, JsonObject commands, CancellationToken cancellationToken)
    {
        var type = QuickString(context, "request_type");
        if (type is not ("friend" or "group")) return QuickRejected();
        var flag = QuickString(context, "flag");
        var userId = QuickId(context, "user_id");
        var request = await _store.GetRequestByFlagAsync(flag, SelfId, cancellationToken).ConfigureAwait(false);
        if (request is null || request.SelfId != SelfId || request.Flag != flag) return QuickRejected();
        var friend = type == "friend";
        var reportedUser = request.Kind == RequestKind.GroupInvitedJoin ? request.TargetUserId : request.RequesterId;
        if (reportedUser != userId || friend != (request.Kind == RequestKind.Friend)) return QuickRejected();
        if (!friend)
        {
            var subtype = QuickString(context, "sub_type");
            if (QuickId(context, "group_id") != request.GroupId
                || subtype != (request.Kind is RequestKind.GroupJoin or RequestKind.GroupInvitedJoin ? "add" : "invite"))
                return QuickRejected();
        }

        // Presence was tested separately: false means rejection, not no operation.
        var approve = QuickFlag(commands, "approve");
        var action = friend ? "set_friend_add_request" : "set_group_add_request";
        return [await ExecuteQuickOperationAsync(action, () => _platform.ResolveRequestAsync(flag, approve,
            reason: !friend && !approve ? QuickOptionalString(commands, "reason") : string.Empty,
            remark: friend && approve ? QuickOptionalString(commands, "remark") : string.Empty,
            expectedSelfId: SelfId, expectedRequestId: request.Id, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false)];
    }

    private async Task<OneBotQuickOperationResult> ExecuteQuickOperationAsync(
        string action, Func<Task> execute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await execute().ConfigureAwait(false);
            return new(action, 0);
        }
        catch (PlatformException error) { return new(action, Failure(error).RetCode); }
        catch (Exception error) when (error is ArgumentException or OneBotSegmentException or JsonException)
        {
            return new(action, 1400);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(action, 1000);
        }
    }

    private static JsonArray QuickReplySegments(JsonObject operation, bool group, string senderId)
    {
        var reply = operation["reply"];
        JsonArray content;
        if (reply is JsonValue value && value.TryGetValue<string>(out var text))
        {
            content = QuickFlag(operation, "auto_escape")
                ? new JsonArray(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = text } })
                : OneBotSegmentCodec.ParseCqString(text);
        }
        else
        {
            content = reply is JsonArray array ? (JsonArray)array.DeepClone() : new JsonArray(reply!.DeepClone());
        }
        if (content.Count == 0) throw new ArgumentException("Empty quick reply");
        if (group && (!operation.ContainsKey("at_sender") || QuickFlag(operation, "at_sender")))
        {
            content.Insert(0, new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = " " } });
            content.Insert(0, new JsonObject { ["type"] = "at", ["data"] = new JsonObject { ["qq"] = senderId } });
        }
        return content;
    }

    private static void ValidateQuickOperationTypes(JsonObject operation)
    {
        foreach (var key in new[] { "auto_escape", "at_sender", "delete", "kick", "ban", "approve" })
        {
            if (operation.ContainsKey(key) && (operation[key] is not JsonValue value || !value.TryGetValue<bool>(out _)))
                throw new ArgumentException("Invalid quick operation boolean");
        }
        foreach (var key in new[] { "remark", "reason" })
        {
            if (operation.ContainsKey(key) && (operation[key] is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new ArgumentException("Invalid quick operation text");
        }
        if (operation.ContainsKey("ban_duration")) _ = QuickDuration(operation["ban_duration"]);
        if (!operation.ContainsKey("reply")) return;
        if (operation["reply"] is JsonValue text && text.TryGetValue<string>(out _)) return;
        if (operation["reply"] is JsonObject segment && IsQuickMessageSegment(segment)) return;
        if (operation["reply"] is JsonArray array && array.All(item => item is JsonObject entry && IsQuickMessageSegment(entry))) return;
        throw new ArgumentException("A quick reply must be a string or message segment(s)");
    }

    private static bool IsQuickMessageSegment(JsonObject segment) =>
        segment["type"] is JsonValue value && value.TryGetValue<string>(out var type) && !string.IsNullOrEmpty(type)
        && segment["data"] is JsonObject;

    private static double QuickDuration(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || !double.IsFinite(duration) || duration < 0
            || duration > (DateTimeOffset.MaxValue - DateTimeOffset.UtcNow).TotalSeconds)
            throw new ArgumentException("Invalid quick ban duration");
        return duration;
    }

    private static string QuickId(JsonObject source, string key, bool positive = true)
    {
        if (source[key] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            || (positive && id <= 0)) throw new ArgumentException("Invalid quick operation event identity");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static bool QuickFlag(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    private static string QuickString(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
            ? text : throw new ArgumentException("Invalid quick operation event field");

    private static string QuickOptionalString(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static OneBotQuickOperationResult[] QuickRejected() => [new("quick_operation", 1400)];
}

internal sealed record OneBotQuickOperationResult(string Action, int RetCode);
