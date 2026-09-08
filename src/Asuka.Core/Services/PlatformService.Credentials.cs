namespace Asuka.Core;

public sealed partial class PlatformService
{
    // All access is serialized by the mutation gate. No credentials enter SQLite or domain events.
    private readonly Dictionary<string, AccountCredentials> _accountCredentials = new(StringComparer.Ordinal);

    public Task<AccountCredentials> GetAccountCredentialsAsync(string selfId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            return _accountCredentials.GetValueOrDefault(selfId) ?? AccountCredentials.Empty;
        }, cancellationToken);

    public Task SetAccountCredentialsAsync(string selfId, AccountCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            token.ThrowIfCancellationRequested();
            if (credentials.Cookies.Count == 0 && credentials.CsrfToken is null) _accountCredentials.Remove(selfId);
            else _accountCredentials[selfId] = credentials;
        }, cancellationToken);
    }

    public Task ClearAccountCredentialsAsync(string selfId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            token.ThrowIfCancellationRequested();
            _accountCredentials.Remove(selfId);
        }, cancellationToken);
}
