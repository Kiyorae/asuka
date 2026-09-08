using System.Threading.Channels;
using System.Text.Json.Nodes;

namespace Asuka.Protocols;

/// <summary>
/// An event queue for the OneBot V12 HTTP polling transport.  Positive
/// capacities evict the oldest event; zero is the protocol's unbounded mode.
/// The protocol has no cursor field: a successful poll consumes the events it
/// returns.  A poll that times out consumes nothing.
/// </summary>
internal sealed class OneBotEventBuffer
{
    // The protocol defines zero as unlimited.  A positive setting is bounded
    // to the exact configured event count and uses oldest-event eviction.
    internal const int DefaultCapacity = 256;
    internal const int MaximumPollSeconds = 300;

    private readonly Channel<JsonObject> _events;

    internal OneBotEventBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _events = capacity == 0
            ? Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            })
            : Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            });
    }

    internal void Enqueue(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _events.Writer.TryWrite((JsonObject)payload.DeepClone());
    }

    internal async Task<IReadOnlyList<JsonObject>> ReadAsync(
        long limit,
        long timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        cancellationToken.ThrowIfCancellationRequested();

        if (timeoutSeconds < 0 || timeoutSeconds > MaximumPollSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        var result = new List<JsonObject>();
        if (!_events.Reader.TryRead(out var first))
        {
            if (timeoutSeconds == 0)
            {
                return result;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                first = await _events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A poll timeout returns an empty successful event list.  A
                // caller cancellation still propagates to the HTTP layer.
                return result;
            }
            catch (ChannelClosedException)
            {
                return result;
            }
        }

        result.Add(first);
        while ((limit == 0 || result.Count < limit) && _events.Reader.TryRead(out var next))
        {
            result.Add(next);
        }

        return result;
    }

    internal void Reset()
    {
        while (_events.Reader.TryRead(out _))
        {
        }
    }
}
