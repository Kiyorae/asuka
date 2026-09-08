using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class MediaCacheCleanupTests
{
    // Original synthetic tone used by SilkAudioPlaybackTests; no user recording.
    private const string TencentTone =
        "AiMhU0lMS19WMycAl6RqHt3UWk/Yi3ksxKlI0JjK63SIlqWACc9xz7nqjIlYP2D/X0h/LACLzDxrbvHZqb7xkLPZG95QjTnd52w5bGigod44SKVLyxupGzpt2G4uzJbSTzEAixQbKbgxGa/BwvR5wnThiZ4zYQObRs9Vcv8TIVnA2GPDY9uEnJv83orWP0pjD8Hy/ywAixQbKbgxGa/BwuZkaNCYIZS6FqzGDCgSV/lolb1LsBh+UHNchgo84AWqHlMrAIsUGym4MRmvwcLjt2SZGXADefuyYCwPppuotip8WCS2baXyO8oEvW0juF8uAIsUGym4MRmvwcLjtu56wLexOr/xDBjqyELzQ5Xf7DU/BikeCxzuDf97XMBxhn8nAIsUGym4MRmvwcLaxlk35SibFMMEEFNxWkefaM58emzSQNO8imM0/yUAixQbKbgxGa/Bwt4dLLBiEeDpemeg40FVlxBzE2PLKtCUJnsLvyUAixQbKbgxGa/Bwt4iU2wwb9umU3YbT2zFolOLam2Uyh6Cm1jV/yUAixQbKbgxGa/Bwti2XgwYid60lYWKBUSfdwvviUCzn+00NmYLpw==";

    [TestMethod]
    public async Task CleanupOfAbsentOrEmptyCachesIsAnIdempotentNoOp()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var absent = await fixture.Media.CleanCacheAsync();
        Assert.AreEqual(0, absent.DeletedFiles);
        Assert.AreEqual(0L, absent.DeletedBytes);
        Assert.AreEqual(0, absent.SkippedFiles);
        Directory.CreateDirectory(fixture.AudioCache);
        Directory.CreateDirectory(fixture.PreviewRoot);

        var empty = await fixture.Media.CleanCacheAsync();

        Assert.AreEqual(0, empty.DeletedFiles);
        Assert.AreEqual(0L, empty.DeletedBytes);
        Assert.AreEqual(0, empty.SkippedFiles);
    }

    [TestMethod]
    public async Task RecognizedDerivativesAreDeletedWithExactByteCountsWhileAssetsAndSqliteRowsRemain()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        byte[] originalBytes = [1, 2, 3, 4, 5];
        var original = await fixture.Assets.StoreAsync(originalBytes, "source.silk");
        await fixture.Store.SaveAsync(original);
        var wave = WriteFile(fixture.WavePath(original.Id), 17);
        var preview = WriteFile(Path.Combine(fixture.PreviewRoot, original.Id, "Asuka-source.silk"), 23);

        var result = await fixture.Media.CleanCacheAsync();

        Assert.AreEqual(2, result.DeletedFiles);
        Assert.AreEqual(40L, result.DeletedBytes);
        Assert.AreEqual(0, result.SkippedFiles);
        Assert.IsFalse(File.Exists(wave));
        Assert.IsFalse(File.Exists(preview));
        CollectionAssert.AreEqual(originalBytes, await fixture.Assets.GetBytesAsync(original.Id));
        Assert.AreEqual(original, await fixture.Store.GetAssetAsync(original.Id));
        Assert.IsTrue(File.Exists(fixture.Store.DatabasePath));
        Assert.AreEqual(0, (await fixture.Media.CleanCacheAsync()).DeletedFiles);
    }

    [TestMethod]
    public async Task UnrecognizedNamesWorkFilesDirectoriesAndFilesOutsideCacheRootsArePreserved()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var hash = new string('a', 64);
        var preserved = new[]
        {
            Path.Combine(fixture.Assets.DirectoryPath, hash),
            Path.Combine(fixture.Assets.DirectoryPath, ".onebot-fragment-upload.tmp"),
            Path.Combine(fixture.Assets.DirectoryPath, ".storetmp-upload"),
            Path.Combine(fixture.AudioCache, ".silk-running.tmp"),
            Path.Combine(fixture.AudioCache, "silk-v1-short.wav"),
            Path.Combine(fixture.AudioCache, $"silk-v1-{new string('g', 64)}.wav"),
            Path.Combine(fixture.AudioCache, $"silk-v2-{hash}.wav"),
            Path.Combine(fixture.AudioCache, $"silk-v1-{hash}.wav.tmp"),
            Path.Combine(fixture.AudioCache, "unknown", $"silk-v1-{hash}.wav"),
            Path.Combine(fixture.WavePath(new string('b', 64)), "keep.txt"),
            Path.Combine(fixture.PreviewRoot, "Asuka-root-level.txt"),
            Path.Combine(fixture.PreviewRoot, "not-an-asset", "Asuka-keep.txt"),
            Path.Combine(fixture.PreviewRoot, hash, "unrecognized.txt"),
            Path.Combine(fixture.PreviewRoot, hash, "Asuka-copy-in-progress.tmp"),
            Path.Combine(fixture.PreviewRoot, hash, "nested", "Asuka-keep.txt"),
            Path.Combine(fixture.PreviewRoot, hash, "Asuka-directory", "keep.txt"),
            Path.Combine(fixture.PreviewRoot, new string('c', 64), "Asuka-other-profile.txt"),
            Path.Combine(fixture.Root, "outside", $"silk-v1-{hash}.wav"),
            Path.Combine(fixture.Root, "outside", hash, "Asuka-keep.txt"),
        };
        foreach (var path in preserved) WriteFile(path, 11);

        var result = await fixture.Media.CleanCacheAsync();

        Assert.AreEqual(0, result.DeletedFiles);
        Assert.AreEqual(0L, result.DeletedBytes);
        foreach (var path in preserved)
        {
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(11L, new FileInfo(path).Length, path);
        }
    }

    [TestMethod]
    [DataRow(FileShare.Read, false)]
    [DataRow(FileShare.None, false)]
    [DataRow(FileShare.Read, true)]
    [DataRow(FileShare.None, true)]
    public async Task OpenFilesAreSkippedAndRemainReadableUntilTheirHandlesClose(FileShare share, bool preview)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var hash = new string('a', 64);
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, hash), 19);
        var path = WriteFile(preview
            ? Path.Combine(fixture.PreviewRoot, hash, "Asuka-preview.txt")
            : fixture.WavePath(hash), 19);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            var skipped = await fixture.Media.CleanCacheAsync();
            Assert.AreEqual(0, skipped.DeletedFiles);
            Assert.AreEqual(0L, skipped.DeletedBytes);
            Assert.IsGreaterThanOrEqualTo(1, skipped.SkippedFiles);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(42, reader.ReadByte());
            Assert.AreEqual(19L, reader.Length);
        }

        var deleted = await fixture.Media.CleanCacheAsync();
        Assert.AreEqual(1, deleted.DeletedFiles);
        Assert.AreEqual(19L, deleted.DeletedBytes);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task AlreadyCancelledCleanupLeavesEveryCandidateUntouched()
    {
        await using var fixture = new CacheFixture();
        var wave = WriteFile(fixture.WavePath(new string('a', 64)), 7);
        var preview = WriteFile(Path.Combine(fixture.PreviewRoot, new string('b', 64), "Asuka-preview.txt"), 13);
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, new string('b', 64)), 13);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Media.CleanCacheAsync(cancellation.Token));

        Assert.IsTrue(File.Exists(wave));
        Assert.IsTrue(File.Exists(preview));
        Assert.AreEqual(7L, new FileInfo(wave).Length);
        Assert.AreEqual(13L, new FileInfo(preview).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HardLinkedCandidatesAreSkippedWithoutUnlinkingEitherName(bool preview)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var original = await fixture.Assets.StoreAsync(new byte[] { 1, 2, 3 }, "original.dat");
        await fixture.Store.SaveAsync(original);
        var link = preview
            ? Path.Combine(fixture.PreviewRoot, original.Id, "Asuka-original.dat")
            : fixture.WavePath(original.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.IsTrue(CreateHardLink(link, fixture.Assets.LocationOf(original.Id), nint.Zero),
            $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");

        var result = await fixture.Media.CleanCacheAsync();

        Assert.AreEqual(0, result.DeletedFiles);
        Assert.IsGreaterThanOrEqualTo(1, result.SkippedFiles);
        Assert.IsTrue(File.Exists(link));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await fixture.Assets.GetBytesAsync(original.Id));
        Assert.AreEqual(original, await fixture.Store.GetAssetAsync(original.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileSymlinksAreSkippedWithoutFollowingOrRemovingThem(bool preview)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var target = WriteFile(Path.Combine(fixture.Root, "outside", "private.txt"), 31);
        var link = preview
            ? Path.Combine(fixture.PreviewRoot, new string('a', 64), "Asuka-preview.txt")
            : fixture.WavePath(new string('a', 64));
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, new string('a', 64)), 31);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symlink requires Windows developer mode or symlink privileges: {error.Message}");
        }
        try
        {
            var result = await fixture.Media.CleanCacheAsync();
            Assert.AreEqual(0, result.DeletedFiles);
            Assert.IsGreaterThanOrEqualTo(1, result.SkippedFiles);
            Assert.IsNotNull(new FileInfo(link).LinkTarget);
            Assert.AreEqual(31L, new FileInfo(target).Length);
        }
        finally { File.Delete(link); }
    }

    [TestMethod]
    [DataRow("audio-root")]
    [DataRow("preview-root")]
    [DataRow("preview-child")]
    public async Task DirectorySymlinksAtEachCleanupBoundaryAreNeverTraversed(string boundary)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        var hash = new string('a', 64);
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, hash), 37);
        var targetFile = boundary switch
        {
            "audio-root" => Path.Combine(outside, $"silk-v1-{hash}.wav"),
            "preview-root" => Path.Combine(outside, hash, "Asuka-private.txt"),
            _ => Path.Combine(outside, "Asuka-private.txt"),
        };
        WriteFile(targetFile, 37);
        var link = boundary switch
        {
            "audio-root" => fixture.AudioCache,
            "preview-root" => fixture.PreviewRoot,
            _ => Path.Combine(fixture.PreviewRoot, hash),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a directory symlink requires Windows developer mode or symlink privileges: {error.Message}");
        }
        try
        {
            var result = await fixture.Media.CleanCacheAsync();
            Assert.AreEqual(0, result.DeletedFiles);
            Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
            Assert.IsTrue(File.Exists(targetFile));
            Assert.AreEqual(37L, new FileInfo(targetFile).Length);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task CleanupWaitsForAnActiveCacheLeaseAndConcurrentCleanersDoNotDeadlock()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var path = WriteFile(fixture.WavePath(new string('a', 64)), 43);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<MediaCacheCleanupResult> first;
        Task<MediaCacheCleanupResult> second;
        using (await MediaService.AcquireCacheLeaseAsync(timeout.Token))
        {
            first = fixture.Media.CleanCacheAsync(timeout.Token);
            second = fixture.Media.CleanCacheAsync(timeout.Token);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.IsTrue(File.Exists(path));
        }

        var results = await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.AreEqual(1, results.Sum(result => result.DeletedFiles));
        Assert.AreEqual(43L, results.Sum(result => result.DeletedBytes));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task CancellingACleanerWaitingForALeaseReleasesEveryGateForTheNextCleanup()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var path = WriteFile(fixture.WavePath(new string('a', 64)), 47);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (await MediaService.AcquireCacheLeaseAsync(timeout.Token))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var waiting = fixture.Media.CleanCacheAsync(cancellation.Token);
            Assert.IsFalse(waiting.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            Assert.IsTrue(File.Exists(path));
        }

        var result = await fixture.Media.CleanCacheAsync(timeout.Token);

        Assert.AreEqual(1, result.DeletedFiles);
        Assert.AreEqual(47L, result.DeletedBytes);
        using var subsequentLease = await MediaService.AcquireCacheLeaseAsync(timeout.Token);
    }

    [TestMethod]
    public async Task OpenPlayableAudioProtectsTheConvertedWaveAndCleanupAllowsLaterReconversion()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var originalBytes = Convert.FromBase64String(TencentTone);
        var asset = await fixture.Assets.StoreAsync(originalBytes, "voice.silk");
        await fixture.Store.SaveAsync(asset);
        string wavePath;
        using (var source = await fixture.Media.OpenPlayableAudioAsync(asset))
        {
            wavePath = source.Path;
            Assert.AreEqual(fixture.WavePath(asset.Id), wavePath);
            var result = await fixture.Media.CleanCacheAsync();
            Assert.AreEqual(0, result.DeletedFiles);
            Assert.IsGreaterThanOrEqualTo(1, result.SkippedFiles);
            Assert.IsTrue(File.Exists(wavePath));
            Assert.AreEqual((int)'R', source.Stream.ReadByte());
        }

        var deleted = await fixture.Media.CleanCacheAsync();
        Assert.AreEqual(1, deleted.DeletedFiles);
        Assert.IsFalse(File.Exists(wavePath));
        using (var reopened = await fixture.Media.OpenPlayableAudioAsync(asset))
        {
            Assert.AreEqual(wavePath, reopened.Path);
            Assert.AreEqual((int)'R', reopened.Stream.ReadByte());
        }
        CollectionAssert.AreEqual(originalBytes, await fixture.Assets.GetBytesAsync(asset.Id));
        Assert.AreEqual(asset, await fixture.Store.GetAssetAsync(asset.Id));
    }

    [TestMethod]
    public async Task ConcurrentPlaybackAndCleanupKeepReturnedStreamsReadable()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var asset = await fixture.Assets.StoreAsync(Convert.FromBase64String(TencentTone), "voice.silk");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var playback = Enumerable.Range(0, 4)
            .Select(_ => fixture.Media.OpenPlayableAudioAsync(asset, timeout.Token)).ToArray();
        var cleaners = Task.WhenAll(fixture.Media.CleanCacheAsync(timeout.Token), fixture.Media.CleanCacheAsync(timeout.Token));
        try
        {
            var sources = await Task.WhenAll(playback).WaitAsync(timeout.Token);
            await cleaners.WaitAsync(timeout.Token);
            foreach (var source in sources)
            {
                Assert.IsTrue(File.Exists(source.Path));
                Assert.AreEqual((int)'R', source.Stream.ReadByte());
            }
        }
        finally
        {
            // Dispose successful tasks even if another decoder or cleaner fails.
            foreach (var task in playback.Where(task => task.IsCompletedSuccessfully))
                (await task).Dispose();
        }
        Assert.AreEqual(1, (await fixture.Media.CleanCacheAsync(timeout.Token)).DeletedFiles);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OneBotElevenMediaHandlerCleansOnlyTheConfiguredRootsAndReturnsNullData(bool offline)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        await using var platform = new PlatformService(fixture.Store);
        await platform.SaveUserAsync(new User("Bot", id: "10001"));
        platform.RegisterBot("10001");
        if (offline) await platform.SetBotPresenceAsync("10001", false, "offline cache cleanup test");
        var wave = WriteFile(fixture.WavePath(new string('a', 64)), 5);
        var preview = WriteFile(Path.Combine(fixture.PreviewRoot, new string('b', 64), "Asuka-preview.txt"), 7);
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, new string('b', 64)), 7);
        var outside = WriteFile(Path.Combine(fixture.Root, "outside", "Asuka-keep.txt"), 11);
        using var protocol = new OneBotProtocol(OneBotVersion.V11, "10001", platform, fixture.Media);

        var reply = await protocol.HandleAsync(new ProtocolCall("clean_cache", new JsonObject()));

        Assert.IsTrue(reply.IsSuccess, reply.Message);
        Assert.IsNull(reply.Data);
        Assert.IsFalse(File.Exists(wave));
        Assert.IsFalse(File.Exists(preview));
        Assert.IsTrue(File.Exists(outside));
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("clean_cache", new JsonObject()))).IsSuccess);
    }

    [TestMethod]
    [DataRow("_async", false)]
    [DataRow("_rate_limited", false)]
    [DataRow("_async", true)]
    [DataRow("_rate_limited", true)]
    public async Task ScheduledCleanupAcknowledgesBeforeDeletionAndCompletesEvenWhenOffline(string suffix, bool offline)
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        await using var platform = new PlatformService(fixture.Store);
        await platform.SaveUserAsync(new User("Bot", id: "10001"));
        platform.RegisterBot("10001");
        if (offline) await platform.SetBotPresenceAsync("10001", false, "offline scheduled cleanup test");
        var wave = WriteFile(fixture.WavePath(new string('a', 64)), 5);
        var preview = WriteFile(Path.Combine(fixture.PreviewRoot, new string('b', 64), "Asuka-preview.txt"), 7);
        WriteFile(Path.Combine(fixture.Assets.DirectoryPath, new string('b', 64)), 7);
        using var protocol = new OneBotProtocol(OneBotVersion.V11, "10001", platform, fixture.Media);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using (await MediaService.AcquireCacheLeaseAsync(timeout.Token))
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("clean_cache" + suffix, new JsonObject()), timeout.Token);
            Assert.AreEqual(1, reply.RetCode);
            Assert.IsNull(reply.Data);
            var envelope = protocol.CreateEnvelope(reply);
            Assert.AreEqual("async", envelope["status"]!.GetValue<string>());
            Assert.IsNull(envelope["data"]);
            Assert.IsTrue(File.Exists(wave), "The cache lease keeps execution pending after acknowledgement.");
            Assert.IsTrue(File.Exists(preview));
        }

        await protocol.WaitForScheduledActionsAsync().WaitAsync(timeout.Token);

        Assert.IsFalse(File.Exists(wave));
        Assert.IsFalse(File.Exists(preview));
    }

    [TestMethod]
    public async Task InPlaceJunctionConversionAfterValidationCannotRedirectCleanupToAnOutsideFile()
    {
        RequireWindows();
        await using var fixture = new CacheFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        var target = WriteFile(Path.Combine(outside, $"silk-v1-{new string('a', 64)}.wav"), 53);
        Directory.CreateDirectory(fixture.AudioCache);

        // Probe the same filesystem before cleanup takes its directory handles.
        // Unsupported hosts may skip; a failure only while cleanup runs must fail.
        var probe = Path.Combine(fixture.Root, "junction-probe");
        Directory.CreateDirectory(probe);
        var supported = TrySetJunction(probe, outside, out var probeError);
        if (!supported && probeError is 1 or 5 or 50 or 1314)
            Assert.Inconclusive($"This Windows host cannot create a test junction (Win32 error {probeError}).");
        Assert.IsTrue(supported, $"Junction fixture setup failed with Win32 error {probeError}.");
        Assert.IsTrue(TryRemoveJunction(probe, out var removeProbeError),
            $"Junction fixture cleanup failed with Win32 error {removeProbeError}.");

        var hookRan = false;
        var junctionSet = false;
        var setError = 0;
        var media = new MediaService(fixture.Store, fixture.Assets)
        {
            AttachmentPreviewCacheDirectory = fixture.PreviewRoot,
            CacheDirectoryReadyForEnumeration = path =>
            {
                if (!string.Equals(path, fixture.AudioCache, StringComparison.OrdinalIgnoreCase) || hookRan) return;
                hookRan = true;
                // Change the already-open empty directory itself. No rename or
                // replacement is attempted, so no-delete ancestor handles alone
                // cannot prevent this reparse-point race.
                junctionSet = TrySetJunction(fixture.AudioCache, outside, out setError);
            },
        };
        try
        {
            var result = await media.CleanCacheAsync();
            Assert.IsTrue(hookRan, "The attack must run after the audio directory was validated.");
            Assert.IsTrue(junctionSet, $"The in-place junction attack failed with Win32 error {setError}.");
            Assert.AreEqual(0, result.DeletedFiles);
            Assert.AreEqual(0L, result.DeletedBytes);
            Assert.IsGreaterThanOrEqualTo(1, result.SkippedFiles);
            Assert.IsTrue(File.Exists(target));
            Assert.AreEqual(53L, new FileInfo(target).Length);
        }
        finally
        {
            if (junctionSet)
                Assert.IsTrue(TryRemoveJunction(fixture.AudioCache, out var error),
                    $"Removing the test junction failed with Win32 error {error}.");
        }
    }

    [TestMethod]
    public async Task CleanCacheIsUnsupportedInOneBotTwelveAndWithoutAConcreteMediaService()
    {
        await using var fixture = new CacheFixture();
        await using var platform = new PlatformService(fixture.Store);
        var candidate = WriteFile(fixture.WavePath(new string('a', 64)), 3);
        var v12 = new OneBotProtocol(OneBotVersion.V12, "10001", platform, fixture.Media);
        var withoutMedia = new OneBotProtocol(OneBotVersion.V11, "10001", platform, new StubProtocolAssetResolver());

        Assert.IsFalse(v12.IsSupportedAction("clean_cache"));
        Assert.IsFalse(withoutMedia.IsSupportedAction("clean_cache"));
        Assert.IsFalse((await v12.HandleAsync(new ProtocolCall("clean_cache", new JsonObject()))).IsSuccess);
        Assert.IsFalse((await withoutMedia.HandleAsync(new ProtocolCall("clean_cache", new JsonObject()))).IsSuccess);
        Assert.IsTrue(File.Exists(candidate));
    }

    [TestMethod]
    public async Task NonWindowsCleanupConservativelyPreservesCandidates()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This checks the conservative non-Windows fallback.");
        await using var fixture = new CacheFixture();
        var candidate = WriteFile(fixture.WavePath(new string('a', 64)), 3);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => fixture.Media.CleanCacheAsync());
        Assert.IsTrue(File.Exists(candidate));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Deletion requires the Windows handle-based cleanup implementation.");
    }

    private static string WriteFile(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)42);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool TrySetJunction(string directory, string destination, out int error)
    {
        var printName = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + printName);
        var print = Encoding.Unicode.GetBytes(printName);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        return ChangeReparsePoint(directory, 0x900A4, buffer, out error);
    }

    private static bool TryRemoveJunction(string directory, out int error)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        return ChangeReparsePoint(directory, 0x900AC, buffer, out error);
    }

    private static bool ChangeReparsePoint(string directory, uint controlCode, byte[] buffer, out int error)
    {
        using var handle = CreateDirectoryHandle(directory, 0x40000000, (uint)(FileShare.Read | FileShare.Write),
            nint.Zero, 3, 0x00200000 | 0x02000000, nint.Zero);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        var changed = DeviceIoControl(handle, controlCode, buffer, checked((uint)buffer.Length),
            nint.Zero, 0, out _, nint.Zero);
        error = changed ? 0 : Marshal.GetLastPInvokeError();
        return changed;
    }

#pragma warning disable SYSLIB1054 // This test-only native call does not require enabling unsafe source-generated interop.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateDirectoryHandle(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, [In] byte[] inputBuffer,
        uint inputSize, nint outputBuffer, uint outputSize, out uint bytesReturned, nint overlapped);
#pragma warning restore SYSLIB1054

    private sealed class CacheFixture : IAsyncDisposable
    {
        internal CacheFixture()
        {
            Directory.CreateDirectory(Root);
            Store = new AsukaStore(Path.Combine(Root, "store.sqlite3"));
            Assets = new AssetStore(Path.Combine(Root, "assets"));
            Media = new MediaService(Store, Assets) { AttachmentPreviewCacheDirectory = PreviewRoot };
        }

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"asuka-clean-cache-tests-{Guid.NewGuid():N}");
        internal string PreviewRoot => Path.Combine(Root, "attachment-preview");
        internal string AudioCache => Path.Combine(Assets.DirectoryPath, ".audio-cache");
        internal AsukaStore Store { get; }
        internal AssetStore Assets { get; }
        internal MediaService Media { get; }
        internal string WavePath(string hash) => Path.Combine(AudioCache, $"silk-v1-{hash}.wav");

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            Assets.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
