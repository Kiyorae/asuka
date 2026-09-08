namespace Asuka.Protocols;

public sealed partial class ProtocolSession
{
    private readonly object _restartSync = new();
    private long _restartGeneration;
    private bool _restartEnabled;
    private PendingRestart? _pendingRestart;

    /// <summary>
    /// Schedules a V11 implementation restart while preserving platform data.
    /// Concurrent requests coalesce into the first accepted restart and delay.
    /// Returns false when the session is stopped, stopping, disposed, or not V11.
    /// </summary>
    public bool RequestRestart(TimeSpan? delay = null)
    {
        var requested = delay ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(requested, TimeSpan.Zero);
        lock (_restartSync) return ScheduleRestart(requested, null, _restartGeneration);
    }

    internal Task WaitForPendingRestartAsync()
    {
        lock (_restartSync) return _pendingRestart?.Worker ?? Task.CompletedTask;
    }

    private void InstallRestartHandler()
    {
        lock (_restartSync)
        {
            var generation = ++_restartGeneration;
            _restartEnabled = Volatile.Read(ref _disposed) == 0 && _implementation is OneBotProtocol { Version: OneBotVersion.V11 };
            if (_implementation is OneBotProtocol oneBot)
                oneBot.SetRestartHandler(_restartEnabled ? (delay, response) => ScheduleRestart(delay, response, generation) : null);
        }
    }

    private void DetachRestartHandler()
    {
        lock (_restartSync)
        {
            _restartEnabled = false;
            if (_implementation is OneBotProtocol oneBot) oneBot.SetRestartHandler(null);
        }
    }

    private bool ScheduleRestart(TimeSpan delay, Task? response, long generation)
    {
        lock (_restartSync)
        {
            if (!_restartEnabled || Volatile.Read(ref _disposed) != 0 || generation != _restartGeneration) return false;
            if (_pendingRestart is { } pending) return !pending.Cancellation.IsCancellationRequested;
            var scheduled = new PendingRestart(generation, delay, response);
            _pendingRestart = scheduled;
            // Never run stop inline on a request, socket, webhook, or action worker:
            // shutdown awaits those tasks. This independent task owns the restart.
            if (ExecutionContext.IsFlowSuppressed()) scheduled.Worker = Task.Run(() => RunRestartAsync(scheduled));
            else
            {
                using (ExecutionContext.SuppressFlow())
                    scheduled.Worker = Task.Run(() => RunRestartAsync(scheduled));
            }
            return true;
        }
    }

    private Task CancelPendingRestart()
    {
        lock (_restartSync)
        {
            ++_restartGeneration;
            _restartEnabled = false;
            if (_implementation is OneBotProtocol oneBot) oneBot.SetRestartHandler(null);
            if (_pendingRestart is not { } pending) return Task.CompletedTask;
            pending.Cancellation.Cancel();
            return pending.Worker;
        }
    }

    private async Task RunRestartAsync(PendingRestart pending)
    {
        var token = pending.Cancellation.Token;
        try
        {
            // Delay counts from acceptance, not from response delivery. Both the
            // specified delay and acknowledgement must finish before shutdown.
            await Task.WhenAll(DelayRestartAsync(pending.Delay, token),
                (pending.Response ?? Task.CompletedTask).WaitAsync(token)).ConfigureAwait(false);
            await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_restartSync)
                {
                    if (pending.Generation != _restartGeneration || token.IsCancellationRequested
                        || Volatile.Read(ref _disposed) != 0 || !_restartEnabled) return;
                }
                await StopCoreAsync().ConfigureAwait(false);
                // Manual Stop invalidates the generation before waiting for the
                // gate, preventing this worker from resurrecting a stopped app.
                lock (_restartSync)
                {
                    if (pending.Generation != _restartGeneration || token.IsCancellationRequested
                        || Volatile.Read(ref _disposed) != 0) return;
                }
                await StartCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Fixed diagnostics: restart failures must not expose endpoint URLs,
            // tokens, request payloads, or inner networking exception messages.
            ReportRestartFailure();
        }
        finally
        {
            lock (_restartSync)
            {
                if (ReferenceEquals(_pendingRestart, pending)) _pendingRestart = null;
                pending.Cancellation.Dispose();
            }
        }
    }

    private void ReportRestartFailure()
    {
        if (DeferredActionFailed is not { } observers) return;
        foreach (EventHandler<Exception> observer in observers.GetInvocationList())
        {
            try { observer(this, new InvalidOperationException("The deferred protocol restart failed.")); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    private static async Task DelayRestartAsync(TimeSpan delay, CancellationToken token)
    {
        // Task.Delay has a timer-sized limit; the API's delay need not acquire
        // that arbitrary limit. Long waits remain cancellable between chunks.
        var maximumChunk = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        while (delay > maximumChunk)
        {
            await Task.Delay(maximumChunk, token).ConfigureAwait(false);
            delay -= maximumChunk;
        }
        await Task.Delay(delay, token).ConfigureAwait(false);
    }

    private sealed class PendingRestart(long generation, TimeSpan delay, Task? response)
    {
        internal long Generation { get; } = generation;
        internal TimeSpan Delay { get; } = delay;
        internal Task? Response { get; } = response;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task Worker { get; set; } = Task.CompletedTask;
    }
}
