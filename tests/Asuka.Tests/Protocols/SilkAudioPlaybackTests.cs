using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class SilkAudioPlaybackTests
{
    // Original synthetic 400 Hz triangle wave, encoded by the pinned Skype SDK.
    // Reproduce with native/Asuka.SilkDecoder/fixtures/generate-tone.c.
    private const string TencentTone =
        "AiMhU0lMS19WMycAl6RqHt3UWk/Yi3ksxKlI0JjK63SIlqWACc9xz7nqjIlYP2D/X0h/LACLzDxrbvHZqb7xkLPZG95QjTnd52w5bGigod44SKVLyxupGzpt2G4uzJbSTzEAixQbKbgxGa/BwvR5wnThiZ4zYQObRs9Vcv8TIVnA2GPDY9uEnJv83orWP0pjD8Hy/ywAixQbKbgxGa/BwuZkaNCYIZS6FqzGDCgSV/lolb1LsBh+UHNchgo84AWqHlMrAIsUGym4MRmvwcLjt2SZGXADefuyYCwPppuotip8WCS2baXyO8oEvW0juF8uAIsUGym4MRmvwcLjtu56wLexOr/xDBjqyELzQ5Xf7DU/BikeCxzuDf97XMBxhn8nAIsUGym4MRmvwcLaxlk35SibFMMEEFNxWkefaM58emzSQNO8imM0/yUAixQbKbgxGa/Bwt4dLLBiEeDpemeg40FVlxBzE2PLKtCUJnsLvyUAixQbKbgxGa/Bwt4iU2wwb9umU3YbT2zFolOLam2Uyh6Cm1jV/yUAixQbKbgxGa/Bwti2XgwYid60lYWKBUSfdwvviUCzn+00NmYLpw==";

    [TestMethod]
    [DataRow("tencent")]
    [DataRow("standard")]
    [DataRow("amr")]
    public async Task RealSilkPacketsProducePlayableWaveRegardlessOfFileName(string variant)
    {
        await using var fixture = new ProtocolTestFixture();
        var tencent = Convert.FromBase64String(TencentTone);
        var bytes = variant switch
        {
            "standard" => [.. tencent[1..], 255, 255],
            "amr" => [.. "#!AMR\n"u8, .. tencent],
            _ => tencent,
        };
        var asset = await fixture.Assets.StoreAsync(bytes, "record.amr");

        var path = await fixture.Media.GetPlayableAudioPathAsync(asset);
        var wave = await File.ReadAllBytesAsync(path);

        Assert.EndsWith(".wav", path);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.AreEqual("WAVEfmt ", Encoding.ASCII.GetString(wave, 8, 8));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20)));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)));
        Assert.AreEqual(24000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)));
        Assert.AreEqual(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)));
        Assert.AreEqual(9600, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)));
        Assert.HasCount(9644, wave);
        Assert.IsGreaterThanOrEqualTo(0, wave.AsSpan(44).IndexOfAnyExcept((byte)0));
        CollectionAssert.AreEqual(bytes, await fixture.Assets.GetBytesAsync(asset.Id));
    }

    [TestMethod]
    public async Task ConcurrentPlaybackReusesAnAtomicWaveCache()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(Convert.FromBase64String(TencentTone), "voice.silk");
        var paths = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => fixture.Media.GetPlayableAudioPathAsync(asset)));
        Assert.IsTrue(paths.All(path => path == paths[0]));
        var modified = File.GetLastWriteTimeUtc(paths[0]);

        Assert.AreEqual(paths[0], await fixture.Media.GetPlayableAudioPathAsync(asset));
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(paths[0]));
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(paths[0])!));
    }

    [TestMethod]
    public async Task NonSilkAudioUsesTheOriginalCachedAsset()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync("RIFFordinary audio data"u8.ToArray(), "voice.wav");
        Assert.AreEqual(fixture.Assets.LocationOf(asset.Id), await fixture.Media.GetPlayableAudioPathAsync(asset));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache")));
    }

    [TestMethod]
    [DataRow("header-only")]
    [DataRow("truncated-length")]
    [DataRow("truncated-packet")]
    [DataRow("oversized-packet")]
    [DataRow("zero-packet")]
    [DataRow("corrupt-packet")]
    [DataRow("trailing-after-end")]
    public async Task MalformedSilkDoesNotLeavePartialPlaybackFiles(string corruption)
    {
        await using var fixture = new ProtocolTestFixture();
        var valid = Convert.FromBase64String(TencentTone);
        byte[] bytes = corruption switch
        {
            "header-only" => valid[..10],
            "truncated-length" => valid[..11],
            "truncated-packet" => valid[..^1],
            "oversized-packet" => [.. valid[..10], 1, 4],
            "zero-packet" => [.. valid[..10], 0, 0],
            "corrupt-packet" => [.. valid[..10], 1, 0, 255],
            _ => [.. valid, 255, 255, 0],
        };
        var asset = await fixture.Assets.StoreAsync(bytes, "record.amr");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Media.GetPlayableAudioPathAsync(asset));

        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache")));
    }

    [TestMethod]
    public async Task OversizedEncodedSilkIsRejectedBeforeStartingDecoder()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(Convert.FromBase64String(TencentTone), "record.amr");
        await using (var oversized = File.OpenWrite(fixture.Assets.LocationOf(asset.Id)))
        {
            oversized.SetLength((16 * 1024 * 1024) + 1L);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Media.GetPlayableAudioPathAsync(asset));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache")));
    }

    [TestMethod]
    public async Task CancelledPlaybackDoesNotStartOrCacheConversion()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(Convert.FromBase64String(TencentTone), "voice.silk");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Media.GetPlayableAudioPathAsync(asset, cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache")));
    }

    [TestMethod]
    public async Task DecodedDurationLimitRejectsSmallHighlyCompressedInput()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(CreateRepeatedPackets(30001), "voice.silk");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Media.GetPlayableAudioPathAsync(asset));

        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache")));
    }

    [TestMethod]
    public async Task CancellingActiveDecoderRemovesTemporaryOutput()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await fixture.Assets.StoreAsync(CreateRepeatedPackets(30000), "voice.silk");
        using var cancellation = new CancellationTokenSource();
        var conversion = fixture.Media.GetPlayableAudioPathAsync(asset, cancellation.Token);
        var cacheDirectory = Path.Combine(fixture.Assets.DirectoryPath, ".audio-cache");
        var timer = Stopwatch.StartNew();
        var hasOutput = false;
        while (!conversion.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            hasOutput = Directory.Exists(cacheDirectory)
                && Directory.GetFiles(cacheDirectory, "*.tmp").Any(path => new FileInfo(path).Length > 44);
            if (hasOutput)
            {
                break;
            }

            await Task.Delay(5);
        }

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => conversion);
        Assert.IsTrue(hasOutput, "The test must cancel an active decoder after it writes PCM.");
        Assert.IsEmpty(Directory.GetFiles(cacheDirectory));
    }

    private static byte[] CreateRepeatedPackets(int count)
    {
        var tone = Convert.FromBase64String(TencentTone);
        var offset = 10;
        var lastPacket = offset;
        while (offset < tone.Length)
        {
            lastPacket = offset;
            offset += 2 + BinaryPrimitives.ReadInt16LittleEndian(tone.AsSpan(offset));
        }

        var packet = tone.AsSpan(lastPacket);
        var result = new byte[10 + (count * packet.Length)];
        tone.AsSpan(0, 10).CopyTo(result);
        for (var index = 0; index < count; index++)
        {
            packet.CopyTo(result.AsSpan(10 + (index * packet.Length)));
        }

        return result;
    }
}
