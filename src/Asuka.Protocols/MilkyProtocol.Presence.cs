namespace Asuka.Protocols;

public sealed partial class MilkyProtocol
{
    private static bool RequiresOnlineAccount(string action) => !action.StartsWith("get_", StringComparison.Ordinal);

    // Read actions use the local cache. Operations that act as the logged-in
    // account cannot succeed after its runtime login state becomes offline.
    private ProtocolReply? OfflineFailure(string action) =>
        _platform.GetBotPresence(SelfId) is { IsOnline: false }
            && RequiresOnlineAccount(action)
        ? new ProtocolReply(-500, Message: "The bot account is offline")
        : null;
}
