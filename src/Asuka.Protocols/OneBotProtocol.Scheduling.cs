using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private readonly object _schedulingLock = new();
    private OneBotActionScheduler? _scheduledActions;
    private Task _scheduledStop = Task.CompletedTask;
    private bool _schedulingPaused;
    private bool _schedulingDisposed;

    /// <summary>OneBot V11 api.rate_limit_interval; the official default is 500 ms.</summary>
    public TimeSpan RateLimitInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    // Diagnostics only: no payload, credentials, echo or additional wire response.
    internal event EventHandler<OneBotScheduledActionFailure>? ScheduledActionFailed;

    private ProtocolReply ScheduleAction(ProtocolCall request, bool rateLimited, CancellationToken cancellationToken)
    {
        lock (_schedulingLock)
        {
            if (_schedulingDisposed || _schedulingPaused)
            {
                return new ProtocolReply(1000, Message: "The action scheduler is stopped");
            }

            try
            {
                _scheduledActions ??= new OneBotActionScheduler(ExecuteCoreAsync, ReportScheduledFailure, RateLimitInterval);
                var acknowledgement = _restartResponse.Value?.Completion ?? Task.CompletedTask;
                return _scheduledActions.TrySchedule(request, rateLimited,
                    request.Name == "set_restart" ? () => UseRestartResponse(acknowledgement) : null, cancellationToken);
            }
            catch (ArgumentException error)
            {
                return Invalid(error.Message);
            }
        }
    }

    private void ReportScheduledFailure(OneBotScheduledActionFailure failure) => ScheduledActionFailed?.Invoke(this, failure);

    internal Task WaitForScheduledActionsAsync()
    {
        lock (_schedulingLock)
        {
            return _scheduledActions?.WaitForIdleAsync() ?? _scheduledStop;
        }
    }

    internal Task StopScheduledActionsAsync()
    {
        lock (_schedulingLock)
        {
            _schedulingPaused = true;
            if (_scheduledActions is { } scheduler)
            {
                _scheduledActions = null;
                _scheduledStop = scheduler.StopAsync();
            }

            return _scheduledStop;
        }
    }

    internal void ResumeScheduledActions()
    {
        lock (_schedulingLock)
        {
            ObjectDisposedException.ThrowIf(_schedulingDisposed, this);
            if (!_scheduledStop.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The previous action scheduler has not finished stopping");
            }

            _schedulingPaused = false;
        }
    }

    private void DisposeScheduledActions()
    {
        lock (_schedulingLock)
        {
            _schedulingDisposed = true;
        }

        StopScheduledActionsAsync().GetAwaiter().GetResult();
    }
}

internal sealed record OneBotScheduledActionFailure(string Action, int RetCode);

/// <summary>
/// Two bounded, owned workers keep ordinary async calls independent from the
/// serialized rate-limited queue. Bounds include running calls and cloned data.
/// </summary>
internal sealed class OneBotActionScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly Channel<ScheduledCall> _ordinary;
    private readonly Channel<ScheduledCall> _limited;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ProtocolCall, CancellationToken, Task<ProtocolReply>> _execute;
    private readonly Action<OneBotScheduledActionFailure> _failure;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly long _maximumQueuedBytes;
    private readonly Task _ordinaryWorker;
    private readonly Task _limitedWorker;
    private TaskCompletionSource _idle = CompletedSignal();
    private Task? _stop;
    private int _count;
    private long _queuedBytes;
    private bool _stopped;

    internal OneBotActionScheduler(
        Func<ProtocolCall, CancellationToken, Task<ProtocolReply>> execute,
        Action<OneBotScheduledActionFailure> failure,
        TimeSpan interval,
        int capacity = 64,
        long maximumQueuedBytes = 16L * 1024 * 1024,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumQueuedBytes, 512);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumQueuedBytes, int.MaxValue);
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Rate limit interval must be between zero and one hour");
        }

        _execute = execute;
        _failure = failure;
        _interval = interval;
        _capacity = capacity;
        _maximumQueuedBytes = maximumQueuedBytes;
        _delay = delay ?? Task.Delay;
        _ordinary = CreateQueue(capacity);
        _limited = CreateQueue(capacity);
        _ordinaryWorker = Task.Run(() => RunAsync(_ordinary.Reader, false));
        _limitedWorker = Task.Run(() => RunAsync(_limited.Reader, true));
    }

    internal ProtocolReply TrySchedule(ProtocolCall call, bool rateLimited, CancellationToken cancellationToken = default) =>
        TrySchedule(call, rateLimited, null, cancellationToken);

    internal ProtocolReply TrySchedule(ProtocolCall call, bool rateLimited, Func<IDisposable>? executionScope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_stopped) return Rejected("The action scheduler is stopped");
            if (_count >= _capacity) return Rejected("The action queue is full");

            long weight;
            ProtocolCall copy;
            try
            {
                weight = MeasureCall(call, _maximumQueuedBytes - _queuedBytes);
                copy = new ProtocolCall(call.Name, (JsonObject)call.Parameters.DeepClone(), call.Echo?.DeepClone());
            }
            catch (QueueBudgetExceededException)
            {
                return Rejected("The action queue payload budget is exhausted");
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
            {
                return new ProtocolReply(1400, Message: "The queued action parameters cannot be copied");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var queued = new ScheduledCall(copy, weight, cancellation, executionScope);
            if (_count == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _count++;
            _queuedBytes += weight;
            if (!(rateLimited ? _limited.Writer : _ordinary.Writer).TryWrite(queued))
            {
                Complete(queued);
                return Rejected("The action queue is unavailable");
            }

            return new ProtocolReply(1);
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync) return _idle.Task;
    }

    internal Task StopAsync()
    {
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            _stopped = true;
            _ordinary.Writer.TryComplete();
            _limited.Writer.TryComplete();
            // Run cancellation callbacks outside this lock, and retain/observe
            // the stop task so callers can await every queued/running operation.
            return _stop = Task.Run(async () =>
            {
                try
                {
                    await _lifetime.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    await Task.WhenAll(_ordinaryWorker, _limitedWorker).ConfigureAwait(false);
                    _lifetime.Dispose();
                }
            });
        }
    }

    private async Task RunAsync(ChannelReader<ScheduledCall> reader, bool rateLimited)
    {
        try
        {
            await foreach (var queued in reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var executed = false;
                try
                {
                    lock (_sync)
                    {
                        if (_stopped || queued.Cancellation.IsCancellationRequested) continue;
                        executed = true;
                    }

                    queued.Cancellation.Token.ThrowIfCancellationRequested();
                    using var executionScope = queued.ExecutionScope?.Invoke();
                    var reply = await _execute(queued.Call, queued.Cancellation.Token).ConfigureAwait(false);
                    if (reply.RetCode is not (0 or 1)) NotifyFailure(queued.Call.Name, reply.RetCode);
                }
                catch (OperationCanceledException) when (queued.Cancellation.IsCancellationRequested)
                {
                    // A caller or the session explicitly cancelled this operation.
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    NotifyFailure(queued.Call.Name, 1000);
                }
                finally
                {
                    Complete(queued);
                }

                if (executed && rateLimited && _interval > TimeSpan.Zero)
                {
                    await _delay(_interval, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            NotifyFailure("scheduler", 1000);
            _ = StopAsync();
        }
        finally
        {
            while (reader.TryRead(out var queued)) Complete(queued);
        }
    }

    private void NotifyFailure(string action, int retCode)
    {
        try
        {
            _failure(new OneBotScheduledActionFailure(action, retCode));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostic observers must not kill the worker or emit a second reply.
        }
    }

    private void Complete(ScheduledCall queued)
    {
        queued.Cancellation.Dispose();
        lock (_sync)
        {
            _count--;
            _queuedBytes -= queued.Weight;
            if (_count == 0) _idle.TrySetResult();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private static Channel<ScheduledCall> CreateQueue(int capacity) =>
        Channel.CreateBounded<ScheduledCall>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

    private static ProtocolReply Rejected(string message) => new(1000, Message: message);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private static long MeasureCall(ProtocolCall call, long remaining)
    {
        // Account for JSON nodes as well as text. Serialize into a bounded sink
        // before cloning; even a huge raw JSON number cannot evade the byte budget.
        long nodes = 2;
        CountNodes(call.Parameters, ref nodes, remaining, 0);
        CountNodes(call.Echo, ref nodes, remaining, 0);
        var nodeBytes = nodes * 128;
        if (nodeBytes >= remaining) throw new QueueBudgetExceededException();
        var sink = new CountingBuffer(checked((int)((remaining - nodeBytes) / 2)));
        using var writer = new Utf8JsonWriter(sink, new JsonWriterOptions { MaxDepth = 66 });
        writer.WriteStartArray();
        writer.WriteStringValue(call.Name);
        call.Parameters.WriteTo(writer);
        if (call.Echo is null) writer.WriteNullValue();
        else call.Echo.WriteTo(writer);
        writer.WriteEndArray();
        writer.Flush();
        return nodeBytes + sink.Written * 2L;
    }

    private static void CountNodes(JsonNode? node, ref long count, long maximum, int depth)
    {
        if (++count * 128 >= maximum || depth > 64) throw new QueueBudgetExceededException();
        if (node is JsonObject map)
        {
            foreach (var pair in map) CountNodes(pair.Value, ref count, maximum, depth + 1);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) CountNodes(item, ref count, maximum, depth + 1);
        }
    }

    private sealed class CountingBuffer(int maximum) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        internal int Written { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || count > maximum - Written) throw new QueueBudgetExceededException();
            Written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var size = Math.Max(1, sizeHint);
            if (size > maximum - Written) throw new QueueBudgetExceededException();
            if (_buffer.Length < size) _buffer = new byte[size];
            return _buffer.AsMemory(0, Math.Min(_buffer.Length, maximum - Written));
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private sealed class QueueBudgetExceededException : Exception { }
    private sealed record ScheduledCall(ProtocolCall Call, long Weight, CancellationTokenSource Cancellation,
        Func<IDisposable>? ExecutionScope);
}
