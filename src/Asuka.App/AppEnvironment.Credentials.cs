using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asuka.Core;
using Asuka.Protocols;
using Windows.Security.Credentials;

namespace Asuka.App;

public sealed partial class AppEnvironment
{
    // One bounded locker entry per account avoids exhausting the per-app entry limit with domains.
    // Larger simulator snapshots remain usable in memory with Remember turned off.
    internal const int MaximumRememberedCredentialCharacters = 1024;
    private const int CredentialNotFoundHResult = unchecked((int)0x80070490);
    private readonly SemaphoreSlim _accountCredentialsGate = new(1, 1);
    private readonly HashSet<string> _restoredCredentialAccounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedCredentialAccounts = new(StringComparer.Ordinal);

    internal async Task RestoreAccountCredentialsAsync(string selfId, CancellationToken cancellationToken = default)
    {
        if (IsShowcaseMode || _disposed) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _refreshCancellation.Token);
        await _accountCredentialsGate.WaitAsync(lifetime.Token);
        try
        {
            if (_restoredCredentialAccounts.Contains(selfId)) return;
            lifetime.Token.ThrowIfCancellationRequested();
            var snapshot = ReadAccountCredentialSnapshot(selfId);
            if (snapshot is not null)
            {
                if (snapshot.Length > MaximumRememberedCredentialCharacters)
                    throw new InvalidOperationException("The saved credential snapshot exceeds this app's storage limit.");
                var saved = JsonSerializer.Deserialize<SavedAccountCredentials>(snapshot)
                    ?? throw new InvalidOperationException("Saved simulator credentials could not be read.");
                if (saved.Version != 1 || saved.Cookies is null)
                    throw new InvalidOperationException("Saved simulator credentials have an unsupported format.");
                var credentials = new AccountCredentials(saved.Cookies, saved.CsrfToken);
                await Platform.SetAccountCredentialsAsync(selfId, credentials, lifetime.Token);
                _rememberedCredentialAccounts.Add(selfId);
            }
            _restoredCredentialAccounts.Add(selfId);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never log a deserializer, Windows locker or protocol error containing credential data.
            AddLog("Credentials", "Restore unavailable",
                "Saved simulator credentials could not be restored. Reopen Account credentials to retry or replace them.");
        }
        finally { _accountCredentialsGate.Release(); }
    }

    internal async Task<(AccountCredentials Credentials, bool Remembered)> LoadAccountCredentialsSettingsAsync(
        string selfId, ProtocolKind protocol, CancellationToken cancellationToken)
    {
        RequireAccountCredentialContext(selfId, protocol, cancellationToken);
        await RestoreAccountCredentialsAsync(selfId, cancellationToken);
        RequireAccountCredentialContext(selfId, protocol, cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _refreshCancellation.Token);
        await _accountCredentialsGate.WaitAsync(lifetime.Token);
        try
        {
            RequireAccountCredentialContext(selfId, protocol, lifetime.Token);
            if (!_restoredCredentialAccounts.Contains(selfId))
                throw new InvalidOperationException("Saved simulator credentials could not be restored. Reopen the dialog to retry or choose Clear all.");
            var credentials = await Platform.GetAccountCredentialsAsync(selfId, lifetime.Token);
            RequireAccountCredentialContext(selfId, protocol, lifetime.Token);
            return (credentials, _rememberedCredentialAccounts.Contains(selfId));
        }
        finally { _accountCredentialsGate.Release(); }
    }

    internal async Task SaveAccountCredentialsAsync(string selfId, ProtocolKind protocol,
        AccountCredentials credentials, bool remember, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        RequireAccountCredentialContext(selfId, protocol, cancellationToken);
        string? snapshot = null;
        if (remember && (credentials.Cookies.Count != 0 || credentials.CsrfToken is not null))
        {
            snapshot = JsonSerializer.Serialize(new SavedAccountCredentials(1,
                new Dictionary<string, string>(credentials.Cookies, StringComparer.Ordinal), credentials.CsrfToken));
            if (snapshot.Length > MaximumRememberedCredentialCharacters)
                throw new InvalidOperationException("These credentials are too large to remember. Turn off Remember on this device to use them for this app session.");
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _refreshCancellation.Token);
        await _accountCredentialsGate.WaitAsync(lifetime.Token);
        try
        {
            RequireAccountCredentialContext(selfId, protocol, lifetime.Token);
            // Validate the account before changing its locker entry. The platform validates it again at commit.
            _ = await Platform.GetAccountCredentialsAsync(selfId, lifetime.Token);
            RequireAccountCredentialContext(selfId, protocol, lifetime.Token);
            var previous = ReadAccountCredentialSnapshot(selfId);
            WriteAccountCredentialSnapshot(selfId, snapshot);
            try
            {
                await Platform.SetAccountCredentialsAsync(selfId, credentials, lifetime.Token);
            }
            catch (Exception)
            {
                try { WriteAccountCredentialSnapshot(selfId, previous); }
                catch (Exception)
                {
                    throw new InvalidOperationException("The update failed and Windows could not restore the previous saved credentials. Reopen Account credentials to review or clear the saved values.");
                }
                throw;
            }
            _restoredCredentialAccounts.Add(selfId);
            if (snapshot is null) _rememberedCredentialAccounts.Remove(selfId);
            else _rememberedCredentialAccounts.Add(selfId);
        }
        finally { _accountCredentialsGate.Release(); }
    }

    // Called before platform shutdown, after _refreshCancellation has been canceled.
    private async Task DisposeAccountCredentialsAsync()
    {
        await _accountCredentialsGate.WaitAsync();
        _restoredCredentialAccounts.Clear();
        _rememberedCredentialAccounts.Clear();
        _accountCredentialsGate.Release();
    }

    private void RequireAccountCredentialContext(string selfId, ProtocolKind protocol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsShowcaseMode || !Capabilities.AccountCredentials || BotPersona?.Id != selfId || Preferences.Protocol != protocol)
            throw new InvalidOperationException("The bot account or protocol changed. Reopen Account credentials to continue.");
    }

    private string AccountCredentialResource
    {
        get
        {
            var storageRoot = Path.GetFullPath(Path.GetDirectoryName(_preferencesPath)!).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            return "Asuka.SimulatorAccountCredentials.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storageRoot)));
        }
    }

    private string? ReadAccountCredentialSnapshot(string selfId)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(AccountCredentialResource, selfId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception error) when (error.HResult == CredentialNotFoundHResult) { return null; }
        catch (Exception) { throw new InvalidOperationException("Windows could not read the saved simulator credentials."); }
    }

    private void WriteAccountCredentialSnapshot(string selfId, string? snapshot)
    {
        try
        {
            var vault = new PasswordVault();
            if (snapshot is not null)
            {
                // Add replaces this resource/user pair. Do not remove the old entry before a potentially failed write.
                vault.Add(new PasswordCredential(AccountCredentialResource, selfId, snapshot));
            }
            else
            {
                try { vault.Remove(vault.Retrieve(AccountCredentialResource, selfId)); }
                catch (Exception error) when (error.HResult == CredentialNotFoundHResult) { }
            }
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Windows could not update the saved simulator credentials. No runtime change was applied.");
        }
    }

    private sealed record SavedAccountCredentials(int Version, Dictionary<string, string>? Cookies, string? CsrfToken);
}
