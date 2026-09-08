using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private bool IsAccountOnline => _platform.GetBotPresence(SelfId)?.IsOnline ?? true;

    private static bool RequiresOnlineAccount(string action) => !action.StartsWith("get_", StringComparison.Ordinal)
        && action is not ("can_send_image" or "can_send_record" or "upload_file" or "upload_file_fragmented"
            or "clean_cache" or "set_restart");

    // Uploading a OneBot 12 file stores a local cache entry; it does not send a
    // platform message. Cached reads and implementation metadata remain usable.
    private ProtocolReply? OfflineFailure(string action) =>
        !IsAccountOnline && RequiresOnlineAccount(action)
        ? new ProtocolReply(Version == OneBotVersion.V11 ? 1403 : 34000, Message: "The bot account is offline")
        : null;

    private JsonObject StatusPayload(bool? onlineSnapshot = null)
    {
        var online = onlineSnapshot ?? IsAccountOnline;
        return Version == OneBotVersion.V11
            // V11 good explicitly includes the QQ account being online.
            ? new JsonObject { ["online"] = online, ["good"] = online }
            : new JsonObject
            {
                // V12 separates implementation health from each account's login.
                ["good"] = true,
                ["bots"] = new JsonArray
                {
                    new JsonObject { ["self"] = SelfObject(), ["online"] = online },
                },
            };
    }

    private IReadOnlyList<OutboundFrame> EncodePresence(DomainEvent domainEvent, BotPresence presence) =>
        Version == OneBotVersion.V11 ? [] : [new OutboundFrame(new JsonObject
        {
            ["id"] = domainEvent.Id,
            ["time"] = domainEvent.Time.ToUnixTimeMilliseconds() / 1000d,
            ["type"] = "meta",
            ["detail_type"] = "status_update",
            ["sub_type"] = string.Empty,
            ["self"] = SelfObject(),
            // Use the queued transition's snapshot, even if a later transition
            // already changed the live state before this event is encoded.
            ["status"] = StatusPayload(presence.IsOnline),
        })];
}
