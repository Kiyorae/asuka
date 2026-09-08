namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private async Task<ProtocolReply> CleanCacheAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (_media is null) return Unsupported(call.Name);
        _ = await _media.CleanCacheAsync(cancellationToken).ConfigureAwait(false);
        return new ProtocolReply();
    }
}
