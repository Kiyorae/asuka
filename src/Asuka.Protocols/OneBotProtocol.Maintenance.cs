using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private Func<TimeSpan, Task?, bool>? _restartHandler;
    private readonly AsyncLocal<RestartResponseScope?> _restartResponse = new();

    internal void SetRestartHandler(Func<TimeSpan, Task?, bool>? handler) => Volatile.Write(ref _restartHandler, handler);

    private Task<ProtocolReply> RestartAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delay = TimeSpan.Zero;
        if (call.Parameters.ContainsKey("delay"))
        {
            if (call.Parameters["delay"] is not JsonValue value)
                return Task.FromResult(Invalid("Restart delay must be a nonnegative number of milliseconds"));
            var text = value.TryGetValue<string>(out var transportText) ? transportText
                : value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : null;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
                || milliseconds < 0 || milliseconds > TimeSpan.MaxValue.Ticks / (decimal)TimeSpan.TicksPerMillisecond)
                return Task.FromResult(Invalid("Restart delay must be a nonnegative, representable number of milliseconds"));
            delay = TimeSpan.FromTicks(decimal.ToInt64(milliseconds * TimeSpan.TicksPerMillisecond));
        }

        var restart = Volatile.Read(ref _restartHandler);
        return Task.FromResult(restart is not null && restart(delay, _restartResponse.Value?.Completion)
            ? new ProtocolReply(1)
            : new ProtocolReply(1000, Message: "A running protocol session is required to restart the implementation"));
    }

    // The API is asynchronous even without an _async suffix. Session handlers
    // scope the acknowledgement so a zero-delay restart cannot close its own
    // HTTP request or WebSocket before the async reply is sent.
    internal IDisposable BeginRestartResponse()
        => UseRestartResponse(null);

    private RestartResponseScope UseRestartResponse(Task? acknowledgement)
    {
        var scope = new RestartResponseScope(this, _restartResponse.Value, acknowledgement);
        _restartResponse.Value = scope;
        return scope;
    }

    private sealed class RestartResponseScope(OneBotProtocol owner, RestartResponseScope? previous, Task? acknowledgement) : IDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion => acknowledgement ?? _completed.Task;
        public void Dispose()
        {
            owner._restartResponse.Value = previous;
            _completed.TrySetResult();
        }
    }
}
