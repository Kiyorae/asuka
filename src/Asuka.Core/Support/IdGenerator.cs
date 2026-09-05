using System.Globalization;

namespace Asuka.Core;

public static class IdGenerator
{
    private const long EpochMilliseconds = 1_704_067_200_000L; // 2024-01-01 UTC
    private const int SequenceBits = 12;
    private const int MaximumSequence = (1 << SequenceBits) - 1;
    private const long MaximumTimestamp = (1L << (53 - SequenceBits)) - 1;
    private static readonly object Sync = new();
    private static long _lastTimestamp = -1;
    private static long _sequence;

    public static string UserId() => NextNumericId();
    public static string GroupId() => NextNumericId();
    public static string MessageId() => NextNumericId();
    public static string RequestId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
    public static string Flag() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string NextNumericId()
    {
        long timestamp;
        long sequence;
        lock (Sync)
        {
            var now = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - EpochMilliseconds);
            if (now > _lastTimestamp)
            {
                _lastTimestamp = now;
                _sequence = 0;
            }
            else
            {
                _sequence++;
                if (_sequence > MaximumSequence)
                {
                    _lastTimestamp++;
                    _sequence = 0;
                }
            }

            timestamp = _lastTimestamp;
            sequence = _sequence;
        }

        return Encode(timestamp, sequence).ToString(CultureInfo.InvariantCulture);
    }

    internal static long EncodeTimestampForTesting(long unixMilliseconds, int sequence = 0) =>
        Encode(unixMilliseconds - EpochMilliseconds, sequence);

    private static long Encode(long timestamp, long sequence)
    {
        if (timestamp is < 0 or > MaximumTimestamp)
        {
            throw new InvalidOperationException("The numeric ID timestamp is outside the supported 2024-2093 range.");
        }

        if (sequence is < 0 or > MaximumSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        // 41 timestamp bits + 12 sequence bits stay within IEEE-754's exact integer
        // range, so IDs survive Int64 and JavaScript-number protocol peers alike.
        return (timestamp << SequenceBits) | sequence;
    }
}
