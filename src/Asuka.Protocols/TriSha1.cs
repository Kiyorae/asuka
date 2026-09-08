using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Asuka.Protocols;

/// <summary>Computes QQ's sampled file checksum for Milky private-file hashes.</summary>
/// <remarks>
/// Files up to 30 MiB contribute all bytes. Larger files contribute three 10 MiB
/// samples: the beginning, the centered sample, and the end. The file's byte
/// length, encoded as a little-endian 64-bit integer, follows those bytes before
/// SHA-1 is applied. This checksum is a protocol identifier, not a security hash.
/// Algorithm reference (stream overload; implementation here is independent):
/// https://github.com/LagrangeDev/Lagrange.Core/blob/20c2ba079b5ee67156bd71419ed54ea61a24e91f/Lagrange.Core/Utility/Cryptography/TriSha1Provider.cs
/// </remarks>
public static class TriSha1
{
    private const int SampleLength = 10 * 1024 * 1024;
    private const int FullFileThreshold = SampleLength * 3;
    private const int BufferLength = 64 * 1024;

    /// <summary>Returns the lowercase hexadecimal checksum for the supplied bytes.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
#pragma warning disable CA5350 // SHA-1 is required by QQ's TriSHA1 file identifier.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        if (data.Length <= FullFileThreshold)
        {
            hash.AppendData(data);
        }
        else
        {
            hash.AppendData(data[..SampleLength]);
            hash.AppendData(data.Slice(data.Length / 2 - SampleLength / 2, SampleLength));
            hash.AppendData(data[^SampleLength..]);
        }

        return Finish(hash, data.Length);
    }

    /// <summary>
    /// Hashes an entire readable, seekable stream, leaving it open and restoring
    /// its original position. Reads at most 30 MiB, using a bounded buffer.
    /// </summary>
    public static async Task<string> ComputeHashAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("TriSHA1 requires a readable, seekable stream.", nameof(stream));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var originalPosition = stream.Position;
        var length = stream.Length;
#pragma warning disable CA5350 // SHA-1 is required by QQ's TriSHA1 file identifier.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        var buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
        try
        {
            if (length <= FullFileThreshold)
            {
                await AppendRangeAsync(0, length).ConfigureAwait(false);
            }
            else
            {
                await AppendRangeAsync(0, SampleLength).ConfigureAwait(false);
                await AppendRangeAsync(length / 2 - SampleLength / 2, SampleLength).ConfigureAwait(false);
                await AppendRangeAsync(length - SampleLength, SampleLength).ConfigureAwait(false);
            }

            return Finish(hash, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Position = originalPosition;
        }

        async Task AppendRangeAsync(long start, long count)
        {
            stream.Position = start;
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesToRead = (int)Math.Min(count, BufferLength);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, bytesToRead);
                count -= bytesToRead;
            }
        }
    }

    private static string Finish(IncrementalHash hash, long length)
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
