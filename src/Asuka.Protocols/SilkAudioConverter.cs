using System.Buffers.Binary;
using System.Diagnostics;

namespace Asuka.Protocols;

/// <summary>Converts legacy QQ/Skype SILK to PCM WAV using the bundled codec.</summary>
internal static class SilkAudioConverter
{
    internal const int MaximumInputBytes = 16 * 1024 * 1024;
    internal const int SampleRate = 24000;
    internal const int MaximumPcmBytes = SampleRate * 2 * 600;
    private static readonly SemaphoreSlim ConversionGate = new(2);
    private static readonly SemaphoreSlim CleanupGate = new(1);

    internal static async Task<IDisposable> AcquireCacheLeaseAsync(CancellationToken cancellationToken)
    {
        await ConversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CacheLease(1, false);
    }

    internal static async Task<IDisposable> AcquireCleanupLeaseAsync(CancellationToken cancellationToken)
    {
        // Only one cleaner may acquire slots, otherwise two cleaners could each
        // hold one slot forever while waiting for the other one.
        await CleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var acquired = 0;
        try
        {
            for (; acquired < 2; acquired++)
                await ConversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CacheLease(2, true);
        }
        catch
        {
            if (acquired != 0) ConversionGate.Release(acquired);
            CleanupGate.Release();
            throw;
        }
    }

    private sealed class CacheLease(int slots, bool cleanup) : IDisposable
    {
        private int _slots = slots;
        public void Dispose()
        {
            var count = Interlocked.Exchange(ref _slots, 0);
            if (count == 0) return;
            ConversionGate.Release(count);
            if (cleanup) CleanupGate.Release();
        }
    }

    internal static bool IsSilk(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("#!AMR\n"u8))
        {
            header = header[6..];
        }

        if (!header.IsEmpty && header[0] == 2)
        {
            header = header[1..];
        }

        return header.StartsWith("#!SILK_V3"u8);
    }

    internal static async Task<string> GetWavePathAsync(
        string inputPath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        using var lease = await AcquireCacheLeaseAsync(cancellationToken).ConfigureAwait(false);
        return await GetWavePathCoreAsync(inputPath, cacheDirectory, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PlayableAudioSource> OpenWaveAsync(string inputPath, string cacheDirectory,
        CancellationToken cancellationToken)
    {
        using var lease = await AcquireCacheLeaseAsync(cancellationToken).ConfigureAwait(false);
        var path = await GetWavePathCoreAsync(inputPath, cacheDirectory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PlayableAudioSource(path, File.OpenRead(path));
    }

    private static async Task<string> GetWavePathCoreAsync(string inputPath, string cacheDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(inputPath).Length > MaximumInputBytes)
        {
            throw new InvalidDataException("SILK audio exceeds the 16 MiB conversion limit.");
        }

        var outputPath = Path.Combine(cacheDirectory, $"silk-v1-{Path.GetFileName(inputPath)}.wav");
        Directory.CreateDirectory(cacheDirectory);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var temporaryPath = Path.Combine(cacheDirectory, $".silk-{Guid.NewGuid():N}.tmp");
        try
        {
            await ConvertAsync(inputPath, temporaryPath, timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, outputPath);
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                // Another player may have converted the same immutable asset.
            }

            return outputPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SILK audio conversion exceeded 30 seconds.");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task ConvertAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var decoderPath = Path.Combine(AppContext.BaseDirectory, "media", "asuka-silk-decoder.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(decoderPath))
        {
            throw new NotSupportedException("The bundled SILK decoder is missing from this installation.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(decoderPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(inputPath);
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        try
        {
            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            await output.WriteAsync(new byte[44], cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var total = 0;
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total = checked(total + count);
                if (total > MaximumPcmBytes)
                {
                    throw new InvalidDataException("SILK audio exceeds the ten-minute playback limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || total == 0 || (total & 1) != 0)
            {
                throw new InvalidDataException("The SILK audio is invalid, truncated, or exceeds the playback limits.");
            }

            output.Position = 0;
            await output.WriteAsync(CreateWaveHeader(total), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The helper can exit between the state check and cancellation.
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static byte[] CreateWaveHeader(int pcmByteCount)
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), pcmByteCount + 36);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), pcmByteCount);
        return header;
    }
}
