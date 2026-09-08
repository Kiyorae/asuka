using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

// Official contracts: https://github.com/botuniverse/onebot-11/blob/master/api/public.md
[TestClass]
public sealed class OneBotStandardMediaTests
{
    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/Z2kAAAAASUVORK5CYII=";
    // Synthetic triangle tone from native/Asuka.SilkDecoder/fixtures/generate-tone.c.
    private const string TencentTone =
        "AiMhU0lMS19WMycAl6RqHt3UWk/Yi3ksxKlI0JjK63SIlqWACc9xz7nqjIlYP2D/X0h/LACLzDxrbvHZqb7xkLPZG95QjTnd52w5bGigod44SKVLyxupGzpt2G4uzJbSTzEAixQbKbgxGa/BwvR5wnThiZ4zYQObRs9Vcv8TIVnA2GPDY9uEnJv83orWP0pjD8Hy/ywAixQbKbgxGa/BwuZkaNCYIZS6FqzGDCgSV/lolb1LsBh+UHNchgo84AWqHlMrAIsUGym4MRmvwcLjt2SZGXADefuyYCwPppuotip8WCS2baXyO8oEvW0juF8uAIsUGym4MRmvwcLjtu56wLexOr/xDBjqyELzQ5Xf7DU/BikeCxzuDf97XMBxhn8nAIsUGym4MRmvwcLaxlk35SibFMMEEFNxWkefaM58emzSQNO8imM0/yUAixQbKbgxGa/Bwt4dLLBiEeDpemeg40FVlxBzE2PLKtCUJnsLvyUAixQbKbgxGa/Bwt4iU2wwb9umU3YbT2zFolOLam2Uyh6Cm1jV/yUAixQbKbgxGa/Bwti2XgwYid60lYWKBUSfdwvviUCzn+00NmYLpw==";

    [TestMethod]
    public async Task SendLikeDefaultsToOneAndPersistsTheCountForTheFriend()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedFriendsAsync(fixture);
        var protocol = Protocol(fixture);
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("send_like", LikeParameters()))).IsSuccess);
        Assert.IsTrue((await protocol.HandleAsync(new ProtocolCall("send_like", LikeParameters(3)))).IsSuccess);
        var likes = await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId);
        Assert.HasCount(1, likes);
        Assert.AreEqual(4L, likes[0].Count);
        Assert.AreEqual(ProtocolTestFixture.SelfId, likes[0].SenderId);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("11")]
    [DataRow("-1")]
    [DataRow("1.5")]
    [DataRow("true")]
    [DataRow("null")]
    public async Task SendLikeRejectsInvalidCountsWithoutMutating(string jsonCount)
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedFriendsAsync(fixture);
        var parameters = LikeParameters();
        parameters["times"] = JsonNode.Parse(jsonCount);
        Assert.AreEqual(1400, (await Protocol(fixture).HandleAsync(new ProtocolCall("send_like", parameters))).RetCode);
        Assert.HasCount(0, await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId));
    }

    [TestMethod]
    public async Task SendLikeRequiresFriendshipAndHonorsTheSharedDailyLimitAcrossInstances()
    {
        await using var fixture = new ProtocolTestFixture();
        await fixture.SeedGroupAsync();
        Assert.AreEqual(1403, (await Protocol(fixture).HandleAsync(new ProtocolCall("send_like", LikeParameters()))).RetCode);
        await fixture.Platform.AddFriendshipAsync(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
        await fixture.Platform.SendProfileLikeAsync(ProtocolTestFixture.SenderId, ProtocolTestFixture.SelfId, 6);
        Assert.AreEqual(1403, (await Protocol(fixture).HandleAsync(new ProtocolCall("send_like", LikeParameters(5)))).RetCode);
        Assert.IsTrue((await Protocol(fixture).HandleAsync(new ProtocolCall("send_like", LikeParameters(4)))).IsSuccess);
        Assert.AreEqual(1403, (await Protocol(fixture).HandleAsync(new ProtocolCall("send_like", LikeParameters()))).RetCode);
        Assert.AreEqual(10L, (await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId))[0].Count);
    }

    [TestMethod]
    public async Task ConcurrentSendLikeCallsCannotExceedTheDailyLimit()
    {
        await using var fixture = new ProtocolTestFixture();
        await SeedFriendsAsync(fixture);
        var replies = await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(_ => Protocol(fixture).HandleAsync(new ProtocolCall("send_like", LikeParameters()))));
        Assert.AreEqual(10, replies.Count(reply => reply.IsSuccess));
        Assert.AreEqual(5, replies.Count(reply => reply.RetCode == 1403));
        Assert.AreEqual(10L, (await fixture.Store.GetProfileLikesAsync(ProtocolTestFixture.SenderId))[0].Count);
    }

    [TestMethod]
    public async Task GetImageAcceptsTheFileFieldEmittedByTheV11CodecAndReturnsOnlyItsCachedPath()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await StoreAsync(fixture, Convert.FromBase64String(PixelPng), "original.png");
        var encoded = await new OneBotSegmentCodec(OneBotVersion.V11, fixture.Media)
            .EncodeAsync([new ImageSegment(asset)]);
        var protocol = Protocol(fixture);
        foreach (var reference in new[] { encoded[0]!["data"]!["file"]!.GetValue<string>(), asset.Id })
        {
            var result = await protocol.HandleAsync(new ProtocolCall("get_image", new JsonObject { ["file"] = reference }));
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(fixture.Assets.LocationOf(asset.Id), result.Data!["file"]!.GetValue<string>());
            Assert.HasCount(1, result.Data!.AsObject());
            CollectionAssert.AreEqual(Convert.FromBase64String(PixelPng), await File.ReadAllBytesAsync(result.Data!["file"]!.GetValue<string>()));
        }
    }

    [TestMethod]
    [DataRow("get_image")]
    [DataRow("get_record")]
    public async Task MediaQueriesRejectExternalPathsAndDoNotRehydrateMissingCachedAssets(string action)
    {
        await using var fixture = new ProtocolTestFixture();
        var bytes = action == "get_image" ? Convert.FromBase64String(PixelPng) : Wave();
        var asset = await StoreAsync(fixture, bytes, action == "get_image" ? "image.png" : "voice.wav");
        var outside = Path.Combine(fixture.Assets.DirectoryPath, $"external-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(outside, bytes);
        var protocol = Protocol(fixture);
        foreach (var reference in new[] { outside, new Uri(outside).AbsoluteUri, "https://example.com/media", "../secret" })
        {
            var result = await protocol.HandleAsync(new ProtocolCall(action, new JsonObject { ["file"] = reference, ["out_format"] = "wav" }));
            Assert.IsFalse(result.IsSuccess);
        }
        await fixture.Store.SaveAsync(asset with { Source = AssetSource.Local(outside) });
        File.Delete(fixture.Assets.LocationOf(asset.Id));
        var missing = await protocol.HandleAsync(new ProtocolCall(action, new JsonObject { ["file"] = asset.Id, ["out_format"] = "wav" }));
        Assert.AreEqual(1404, missing.RetCode);
        Assert.IsFalse(fixture.Assets.Exists(asset.Id));
    }

    [TestMethod]
    public async Task GetRecordReturnsRealWaveBytesRegardlessOfTheStoredFileName()
    {
        await using var fixture = new ProtocolTestFixture();
        var original = Wave();
        var asset = await StoreAsync(fixture, original, "misleading.mp3");
        var protocol = Protocol(fixture);
        var result = await protocol.HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = "wav" }));
        Assert.IsTrue(result.IsSuccess, result.Message);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(result.Data!["file"]!.GetValue<string>()));
        var unavailable = await protocol.HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = "mp3" }));
        Assert.AreEqual(1404, unavailable.RetCode);
        Assert.Contains("not supported", unavailable.Message);
    }

    [TestMethod]
    public async Task GetRecordDecodesQQSilkToWaveAndReusesThePlaybackCache()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await StoreAsync(fixture, Convert.FromBase64String(TencentTone), "qq.amr");
        var protocol = Protocol(fixture);
        var request = new ProtocolCall("get_record", new JsonObject { ["file"] = fixture.Assets.LocationOf(asset.Id), ["out_format"] = "wav" });
        var result = await protocol.HandleAsync(request);
        Assert.IsTrue(result.IsSuccess, result.Message);
        var path = result.Data!["file"]!.GetValue<string>();
        Assert.EndsWith(".wav", path);
        var wave = await File.ReadAllBytesAsync(path);
        CollectionAssert.AreEqual("RIFF"u8.ToArray(), wave[..4]);
        CollectionAssert.AreEqual("WAVE"u8.ToArray(), wave[8..12]);
        Assert.HasCount(9644, wave);
        Assert.AreEqual(path, await fixture.Media.GetPlayableAudioPathAsync(asset));
        Assert.AreEqual(path, (await protocol.HandleAsync(request)).Data!["file"]!.GetValue<string>());
        CollectionAssert.AreEqual(Convert.FromBase64String(TencentTone), await fixture.Assets.GetBytesAsync(asset.Id));
    }

    [TestMethod]
    public async Task GetRecordPreservesNativeAmrAndDoesNotPretendToConvertItToWave()
    {
        await using var fixture = new ProtocolTestFixture();
        // AMR-NB mode 7: storage magic, one good-quality 12.2 kbit/s speech frame.
        byte[] amr = [.. "#!AMR\n"u8, 0x3c, .. new byte[31]];
        var asset = await StoreAsync(fixture, amr, "native.silk");
        var protocol = Protocol(fixture);
        var result = await protocol.HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = "amr" }));
        Assert.IsTrue(result.IsSuccess, result.Message);
        CollectionAssert.AreEqual(amr, await File.ReadAllBytesAsync(result.Data!["file"]!.GetValue<string>()));
        var wave = await protocol.HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = "wav" }));
        Assert.AreEqual(1404, wave.RetCode);
        Assert.Contains("not supported", wave.Message);
    }

    [TestMethod]
    public async Task MediaQueriesRejectMissingParametersInvalidFormatsAndMislabeledContent()
    {
        await using var fixture = new ProtocolTestFixture();
        var asset = await StoreAsync(fixture, "This is not an image or audio recording"u8.ToArray(), "pretend.wav");
        var protocol = Protocol(fixture);
        Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("get_image", new JsonObject()))).RetCode);
        Assert.AreEqual(1400, (await protocol.HandleAsync(new ProtocolCall("get_image", new JsonObject { ["file"] = asset.Id }))).RetCode);
        foreach (var format in new string?[] { null, "", "silk", "../wav", "wav" })
        {
            var result = await protocol.HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = format }));
            Assert.AreEqual(1400, result.RetCode);
        }
    }

    [TestMethod]
    public async Task GetRecordRejectsTruncatedWaveEvenWhenItsMagicAndFilenameMatch()
    {
        await using var fixture = new ProtocolTestFixture();
        var truncated = Wave()[..44];
        var asset = await StoreAsync(fixture, truncated, "truncated.wav");
        var result = await Protocol(fixture).HandleAsync(new ProtocolCall("get_record", new JsonObject { ["file"] = asset.Id, ["out_format"] = "wav" }));
        Assert.AreEqual(1400, result.RetCode);
    }

    [TestMethod]
    public async Task StandardV11MediaActionsAreNotOneBotV12Extensions()
    {
        await using var fixture = new ProtocolTestFixture();
        var protocol = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        foreach (var action in new[] { "send_like", "get_image", "get_record" })
            Assert.AreEqual(10002, (await protocol.HandleAsync(new ProtocolCall(action, new JsonObject()))).RetCode);
    }

    private static OneBotProtocol Protocol(ProtocolTestFixture fixture) =>
        new(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static JsonObject LikeParameters(int? times = null)
    {
        var parameters = new JsonObject { ["user_id"] = 1_000_000_002L };
        if (times is not null) parameters["times"] = times.Value;
        return parameters;
    }

    private static async Task SeedFriendsAsync(ProtocolTestFixture fixture)
    {
        await fixture.SeedGroupAsync();
        await fixture.Platform.AddFriendshipAsync(ProtocolTestFixture.SelfId, ProtocolTestFixture.SenderId);
    }

    private static async Task<Asset> StoreAsync(ProtocolTestFixture fixture, byte[] bytes, string name)
    {
        var asset = await fixture.Assets.StoreAsync(bytes, name);
        await fixture.Store.SaveAsync(asset);
        return asset;
    }

    private static byte[] Wave()
    {
        var wave = new byte[64];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 24000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 48000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), wave.Length - 44);
        return wave;
    }
}
