using System.Text;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class TriSha1Tests
{
    // The input is byte[i] = i % 251. Fixed expected values were calculated
    // independently using Node.js crypto SHA-1 and Buffer.writeBigUInt64LE,
    // applying the documented sample ranges. These are not QQ-published vectors.
    [TestMethod]
    [DataRow(0L, "05fe405753166f125559e7c9ac558654f107c7e9")]
    [DataRow(1L, "1dac579df0bc0a8262031e67f251e570d6e1dc60")]
    [DataRow(31L, "8daaa10fd1806b8674f0c0a693f235a4bedda2db")]
    [DataRow(31_457_279L, "af1ad1b0529b5603c7d99f85d40aab36fc337dde")]
    [DataRow(31_457_280L, "01a2b98d0874b402d0eced2a3a63b8ede2aafc11")]
    [DataRow(31_457_281L, "080a7cac9a4fb5ac8f1dfafb5b76c90a4f4a0a3e")]
    [DataRow(52_428_803L, "7f24f18c807e086a5ff6a79bd85d3a2808fb3546")]
    [DataRow(4_294_967_313L, "61ef5545b3ce33f49f908adb6f9da6994b3203d3")]
    public async Task StreamMatchesIndependentVectors(long length, string expected)
    {
        await using var stream = new PatternStream(length) { Position = Math.Min(length, 7) };
        var originalPosition = stream.Position;

        Assert.AreEqual(expected, await TriSha1.ComputeHashAsync(stream));
        Assert.AreEqual(originalPosition, stream.Position);
        Assert.AreEqual(Math.Min(length, 30L * 1024 * 1024), stream.BytesRead);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    public void ByteSpanIncludesTheLengthTrailer()
    {
        Assert.AreEqual("05fe405753166f125559e7c9ac558654f107c7e9", TriSha1.ComputeHash([]));
        Assert.AreEqual("22bf365e786dabc54fd81ee41a6ae4ed23ea5df7", TriSha1.ComputeHash(Encoding.ASCII.GetBytes("abc")));
    }

    [TestMethod]
    public void ByteSpanIncludesAllThreeSamplesForLargeFiles()
    {
        var bytes = new byte[52_428_803];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        Assert.AreEqual("7f24f18c807e086a5ff6a79bd85d3a2808fb3546", TriSha1.ComputeHash(bytes));
    }

    [TestMethod]
    public async Task ShortReadsAreFilledBeforeHashing()
    {
        await using var stream = new PatternStream(31) { MaximumReadLength = 3 };
        Assert.AreEqual("8daaa10fd1806b8674f0c0a693f235a4bedda2db", await TriSha1.ComputeHashAsync(stream));
        Assert.AreEqual(31L, stream.BytesRead);
    }

    [TestMethod]
    public async Task EarlyEndOfStreamFailsAndRestoresPosition()
    {
        await using var stream = new PatternStream(31) { ReadableLength = 12, Position = 5 };
        await Assert.ThrowsAsync<EndOfStreamException>(() => TriSha1.ComputeHashAsync(stream));
        Assert.AreEqual(5L, stream.Position);
    }

    [TestMethod]
    public async Task CancellationFailsWithoutReading()
    {
        await using var stream = new PatternStream(31) { Position = 5 };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => TriSha1.ComputeHashAsync(stream, cancellation.Token));
        Assert.AreEqual(5L, stream.Position);
        Assert.AreEqual(0L, stream.BytesRead);
    }

    [TestMethod]
    public async Task NonSeekableStreamIsRejected()
    {
        await using var stream = new PatternStream(31) { SupportsSeeking = false };
        await Assert.ThrowsAsync<ArgumentException>(() => TriSha1.ComputeHashAsync(stream));
        Assert.AreEqual(0L, stream.BytesRead);
    }

    private sealed class PatternStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public int MaximumReadLength { get; init; } = int.MaxValue;
        public long ReadableLength { get; init; } = length;
        public bool SupportsSeeking { get; init; } = true;
        public override bool CanRead => true;
        public override bool CanSeek => SupportsSeeking;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, MaximumReadLength), Math.Max(0, ReadableLength - Position));
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)((Position + index) % 251);
            }

            Position += count;
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
