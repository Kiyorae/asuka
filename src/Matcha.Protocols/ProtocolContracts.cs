using System.Text.Json.Nodes;
using Matcha.Core;

namespace Matcha.Protocols;

public sealed record WebSocketClientHandshake(
    string DefaultPath,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> Subprotocols)
{
    public static WebSocketClientHandshake Default { get; } = new(
        "/",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        []);
}

public sealed record ProtocolCall(string Name, JsonObject Parameters, JsonNode? Echo = null)
{
    public string? GetId(string key) => Parameters.GetFlexibleString(key);

    public string? GetText(string key) => Parameters.GetFlexibleString(key);

    public long? GetLong(string key) => Parameters.GetFlexibleInt64(key);

    public int? GetInteger(string key)
    {
        var value = GetLong(key);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    public bool? GetBoolean(string key) => Parameters.GetFlexibleBoolean(key);

    public JsonArray? GetArray(string key) => Parameters[key] as JsonArray;
}

public sealed record ProtocolReply(
    int RetCode = 0,
    JsonNode? Data = null,
    string Message = "",
    int HttpStatus = 200)
{
    public bool IsSuccess => RetCode == 0;

    public JsonNode EffectiveData => Data ?? new JsonObject();

    public static ProtocolReply Success(JsonNode? data = null) => new(Data: data ?? new JsonObject());
}

public sealed record OutboundFrame(JsonObject Payload, string? EventName = null);

public sealed class UnsupportedTransportException : InvalidOperationException
{
    public UnsupportedTransportException(string protocolIdentifier, TransportMode transport)
        : base($"{protocolIdentifier} does not support {transport}.")
    {
        ProtocolIdentifier = protocolIdentifier;
        Transport = transport;
    }

    public string ProtocolIdentifier { get; }

    public TransportMode Transport { get; }
}

public interface IProtocolImplementation
{
    string Identifier { get; }

    string DisplayName { get; }

    string SelfId { get; }

    IReadOnlySet<TransportMode> SupportedTransports { get; }

    WebSocketClientHandshake ClientHandshake { get; }

    TimeSpan? HeartbeatInterval { get; }

    Task<ProtocolReply> HandleAsync(ProtocolCall request, CancellationToken cancellationToken = default);

    JsonObject CreateEnvelope(ProtocolReply reply, JsonNode? echo = null);

    Task<IReadOnlyList<OutboundFrame>> EncodeAsync(
        DomainEvent domainEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboundFrame>> GetHandshakeFramesAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OutboundFrame>>([]);

    Task<OutboundFrame?> GetHeartbeatFrameAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<OutboundFrame?>(null);
}

public enum SessionStateKind
{
    Idle,
    Listening,
    Ready,
    Connecting,
    Connected,
    Failed,
}

public sealed record SessionState(SessionStateKind Kind, ushort? Port = null, string? Error = null)
{
    public bool IsActive => Kind is SessionStateKind.Listening
        or SessionStateKind.Ready
        or SessionStateKind.Connecting
        or SessionStateKind.Connected;
}

public enum RoundTripTimeKind
{
    Unavailable,
    Measuring,
    Measured,
    TimedOut,
    Unsupported,
}

public sealed record RoundTripTimeState(RoundTripTimeKind Kind, TimeSpan? Duration = null);

public enum TrafficDirection
{
    InboundCall,
    OutboundEvent,
    Reply,
}

public sealed record TrafficEntry(
    Guid Id,
    TrafficDirection Direction,
    DateTimeOffset Timestamp,
    string Summary,
    JsonNode Payload)
{
    public TrafficEntry(TrafficDirection direction, string summary, JsonNode payload)
        : this(Guid.NewGuid(), direction, DateTimeOffset.UtcNow, summary, payload.DeepClone())
    {
    }
}
