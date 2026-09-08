namespace Asuka.Core;

public sealed partial class PlatformService
{
    private readonly Dictionary<string, BotPresence> _botPresence = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _botActionSelfId = new();

    public event EventHandler<BotPresence>? BotPresenceChanged;

    public bool IsBotOnline(string selfId) => GetBotPresence(selfId)?.IsOnline == true;

    public BotPresence? GetBotPresence(string selfId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfId);
        lock (_stateSync) return _botPresence.GetValueOrDefault(selfId);
    }

    /// <summary>
    /// Marks a protocol write's execution context so its mutations recheck login
    /// state after acquiring the mutation gate. Dispose in the same async flow;
    /// cached reads and native simulator actions must not enter this scope.
    /// </summary>
    public IDisposable BeginBotAction(string selfId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _botActionSelfId.Value;
        _botActionSelfId.Value = selfId;
        return new BotActionScope(this, previous);
    }

    private void RequireBotActionOnline()
    {
        if (_botActionSelfId.Value is { } selfId && GetBotPresence(selfId) is { IsOnline: false })
            throw NotPermitted("The bot account is offline");
    }

    /// <summary>
    /// Changes one registered bot's runtime login state. Duplicate states are no-ops.
    /// Offline recipients retain locally simulated messages but receive no ordinary
    /// protocol events, and sending as the offline bot is rejected.
    /// </summary>
    public async Task<bool> SetBotPresenceAsync(string selfId, bool isOnline, string reason = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfId);
        ArgumentNullException.ThrowIfNull(reason);
        var changed = await MutateAsync<BotPresence?>(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            BotPresence presence;
            lock (_stateSync)
            {
                if (!_botPresence.TryGetValue(selfId, out var existing))
                    throw InvalidParameter("Only a registered bot can change its online state");
                if (existing.IsOnline == isOnline) return null;
                presence = new BotPresence(selfId, isOnline, isOnline ? string.Empty : reason, DateTimeOffset.UtcNow);
                _botPresence[selfId] = presence;
            }

            Publish(new DomainEvent(selfId, new BotPresenceChangedEvent(presence), time: presence.ChangedAt));
            return presence;
        }, cancellationToken).ConfigureAwait(false);

        if (changed is null) return false;
        // Notify after releasing the mutation gate. An observer may start another
        // operation, and it must never prevent the already-published domain event.
        var observers = BotPresenceChanged;
        if (observers is not null)
        {
            foreach (EventHandler<BotPresence> observer in observers.GetInvocationList())
            {
                try { observer(this, changed); }
                catch { /* Runtime observers cannot undo a completed presence transition. */ }
            }
        }
        return true;
    }

    private sealed class BotActionScope(PlatformService platform, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            platform._botActionSelfId.Value = previous;
        }
    }
}
