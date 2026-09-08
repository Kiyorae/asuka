using Asuka.Protocols;

namespace Asuka.App;

public sealed partial class AppEnvironment
{
    public bool CanRestartProtocol => !_disposed && !IsShowcaseMode
        && Capabilities.ImplementationRestart && _session?.State.IsActive == true;

    public void RestartProtocol()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanRestartProtocol || _session?.RequestRestart() != true)
            throw new InvalidOperationException("Connect the OneBot V11 service before restarting it.");
        AddLog("Connection", "Restart requested", "The OneBot V11 service will restart.");
    }

    public Task<MediaCacheCleanupResult> CleanMediaCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Capabilities.CacheCleanup)
            throw new InvalidOperationException("The selected protocol does not support cache cleanup.");
        return Media.CleanCacheAsync(cancellationToken);
    }
}
