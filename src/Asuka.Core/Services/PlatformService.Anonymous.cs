namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task SetGroupAnonymousAsync(string groupId, string operatorId, bool enable = true,
        CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.SetGroupAnonymousAsync(groupId, operatorId, enable, token), cancellationToken);

    public Task BanAnonymousAsync(string groupId, string flag, string operatorId, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        MutateAsync(token => Store.BanAnonymousAsync(groupId, flag, operatorId, duration, token), cancellationToken);
}
