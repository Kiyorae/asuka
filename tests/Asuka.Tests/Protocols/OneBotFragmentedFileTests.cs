using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class OneBotFragmentedFileTests
{
    // Wire stages and mandatory fields come from https://12.onebot.dev/interface/file/actions/.
    [TestMethod]
    public async Task FragmentActionsAreAdvertisedOnlyForV12WithMediaStorage()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        using var withoutMedia = new OneBotProtocol(OneBotVersion.V12, ProtocolTestFixture.SelfId,
            fixture.Platform, new StubProtocolAssetResolver());
        using var v11 = new OneBotProtocol(OneBotVersion.V11, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);
        var actions = (JsonArray)(await protocol.HandleAsync(new ProtocolCall("get_supported_actions", new JsonObject()))).Data!;
        var limited = (JsonArray)(await withoutMedia.HandleAsync(new ProtocolCall("get_supported_actions", new JsonObject()))).Data!;
        foreach (var action in new[] { "upload_file_fragmented", "get_file_fragmented" })
        {
            Assert.IsTrue(actions.Any(node => node!.GetValue<string>() == action));
            Assert.IsFalse(limited.Any(node => node!.GetValue<string>() == action));
            Assert.IsFalse((await v11.HandleAsync(new ProtocolCall(action, new JsonObject()))).IsSuccess);
        }
    }

    [TestMethod]
    public async Task FragmentedUploadSupportsOutOfOrderWritesAndDownloadsExactRanges()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        byte[] bytes = [0, 1, 2, 3, 4, 5];
        var pendingId = await PrepareAsync(protocol, bytes.Length);
        Assert.IsFalse((await GetAsync(protocol, pendingId, "prepare")).IsSuccess);
        var transfer = await TransferAsync(protocol, pendingId, 3, bytes[3..]);
        Assert.IsTrue(transfer.IsSuccess);
        Assert.IsNull(transfer.Data);
        Assert.IsTrue((await TransferAsync(protocol, pendingId, 0, bytes[..4])).IsSuccess);

        var finished = await FinishAsync(protocol, pendingId, Hash(bytes));
        Assert.IsTrue(finished.IsSuccess, finished.Message);
        var id = finished.Data!["file_id"]!.GetValue<string>();
        Assert.AreNotEqual(pendingId, id);
        Assert.IsFalse((await TransferAsync(protocol, pendingId, 0, bytes)).IsSuccess);
        var prepared = await GetAsync(protocol, id, "prepare");
        Assert.IsTrue(prepared.IsSuccess, prepared.Message);
        Assert.AreEqual("fragment.bin", prepared.Data!["name"]!.GetValue<string>());
        Assert.AreEqual(bytes.LongLength, prepared.Data["total_size"]!.GetValue<long>());
        Assert.AreEqual(Hash(bytes), prepared.Data["sha256"]!.GetValue<string>());
        var downloaded = await GetAsync(protocol, id, "transfer", 2, 3);
        CollectionAssert.AreEqual(bytes[2..5], Convert.FromBase64String(downloaded.Data!["data"]!.GetValue<string>()));
        Assert.IsEmpty(Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
    }

    [TestMethod]
    public async Task IncompleteAndIncorrectChecksumsNeverPublishAnAsset()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        byte[] bytes = [1, 2, 3, 4];
        var id = await PrepareAsync(protocol, bytes.Length);
        Assert.IsTrue((await TransferAsync(protocol, id, 0, bytes[..2])).IsSuccess);
        Assert.IsTrue((await TransferAsync(protocol, id, 3, bytes[3..])).IsSuccess);
        Assert.AreEqual(35000, (await FinishAsync(protocol, id, Hash(bytes))).RetCode);
        Assert.IsFalse(fixture.Assets.Exists(Hash(bytes)));
        Assert.IsTrue((await TransferAsync(protocol, id, 2, bytes[2..3])).IsSuccess);
        Assert.AreEqual(35000, (await FinishAsync(protocol, id, new string('0', 64))).RetCode);
        Assert.IsFalse(fixture.Assets.Exists(Hash(bytes)));
        Assert.IsTrue((await FinishAsync(protocol, id, Hash(bytes))).IsSuccess);
    }

    [TestMethod]
    public async Task PendingUploadsAreOwnedByProtocolAndDisposedWithTheirFiles()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        using var other = NewProtocol(fixture);
        var id = await PrepareAsync(protocol, 4);
        Assert.AreEqual(35000, (await TransferAsync(other, id, 0, [1])).RetCode);
        Assert.AreEqual(35000, (await FinishAsync(other, id, Hash([1, 2, 3, 4]))).RetCode);
        Assert.HasCount(1, Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
        protocol.Dispose();
        protocol.Dispose();
        Assert.IsEmpty(Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
    }

    [TestMethod]
    public async Task ClearingStoppedSessionUploadsAllowsANewTransferOnRestart()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        var stale = await PrepareAsync(protocol, 1);
        protocol.ClearPendingFileTransfers();
        Assert.IsEmpty(Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
        Assert.AreEqual(35000, (await TransferAsync(protocol, stale, 0, [7])).RetCode);
        var current = await PrepareAsync(protocol, 1);
        Assert.IsTrue((await TransferAsync(protocol, current, 0, [7])).IsSuccess);
        Assert.IsTrue((await FinishAsync(protocol, current, Hash([7]))).IsSuccess);
    }

    [TestMethod]
    public async Task CancelledTransferDoesNotConsumePendingUpload()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        var id = await PrepareAsync(protocol, 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TransferAsync(protocol, id, 0, [7], cancellation.Token));
        Assert.IsTrue((await TransferAsync(protocol, id, 0, [7])).IsSuccess);
        Assert.IsTrue((await FinishAsync(protocol, id, Hash([7]))).IsSuccess);
    }

    [TestMethod]
    public async Task EmptyFileCanBeFinishedAndReadWithoutAnyTransfers()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        var pending = await PrepareAsync(protocol, 0);
        var result = await FinishAsync(protocol, pending, Hash([]));
        Assert.IsTrue(result.IsSuccess, result.Message);
        var id = result.Data!["file_id"]!.GetValue<string>();
        Assert.AreEqual(0L, (await GetAsync(protocol, id, "prepare")).Data!["total_size"]!.GetValue<long>());
        Assert.AreEqual(string.Empty, (await GetAsync(protocol, id, "transfer", 0, 0)).Data!["data"]!.GetValue<string>());
        Assert.AreEqual(10003, (await GetAsync(protocol, id, "transfer", 0, 1)).RetCode);
    }

    [TestMethod]
    public async Task FragmentRequestsRejectInvalidStagesTypesSizesAndRanges()
    {
        await using var fixture = new ProtocolTestFixture();
        using var protocol = NewProtocol(fixture);
        var id = await PrepareAsync(protocol, 4);
        foreach (var parameters in new JsonObject[]
        {
            new() { ["stage"] = "prepare", ["name"] = "x", ["total_size"] = -1 },
            new() { ["stage"] = "prepare", ["name"] = "x", ["total_size"] = fixture.Assets.MaximumByteCount + 1 },
            new() { ["stage"] = "prepare", ["name"] = 3, ["total_size"] = 1 },
            new() { ["stage"] = "prepare", ["name"] = "x", ["total_size"] = "4" },
            new() { ["stage"] = "transfer", ["file_id"] = id, ["offset"] = -1, ["data"] = "AQ==" },
            new() { ["stage"] = "transfer", ["file_id"] = id, ["offset"] = long.MaxValue, ["data"] = "AQ==" },
            new() { ["stage"] = "transfer", ["file_id"] = id, ["offset"] = 4, ["data"] = "AQ==" },
            new() { ["stage"] = "transfer", ["file_id"] = id, ["offset"] = 0, ["data"] = "??" },
            new() { ["stage"] = "transfer", ["file_id"] = id, ["offset"] = 0, ["data"] = 5 },
            new() { ["stage"] = "finish", ["file_id"] = id },
            new() { ["stage"] = "finish", ["file_id"] = id, ["sha256"] = new string('A', 64) },
        })
        {
            var reply = await protocol.HandleAsync(new ProtocolCall("upload_file_fragmented", parameters));
            Assert.AreEqual(10003, reply.RetCode, $"{parameters}: {reply.Message}");
        }

        Assert.AreEqual(10004, (await protocol.HandleAsync(new ProtocolCall("upload_file_fragmented", new JsonObject { ["stage"] = "unknown" }))).RetCode);
        Assert.IsTrue((await TransferAsync(protocol, id, 0, [1, 2, 3, 4])).IsSuccess);
        var final = (await FinishAsync(protocol, id, Hash([1, 2, 3, 4]))).Data!["file_id"]!.GetValue<string>();
        foreach (var (offset, size) in new (long, long)[] { (-1, 1), (0, -1), (4, 1), (long.MaxValue, 1), (0, long.MaxValue) })
        {
            Assert.AreEqual(10003, (await GetAsync(protocol, final, "transfer", offset, size)).RetCode);
        }
    }

    [TestMethod]
    public async Task IdleExpiryDeletesStagingAndReleasesSharedQuota()
    {
        await using var fixture = new ProtocolTestFixture();
        var time = new ManualTimeProvider();
        using var first = new OneBotFileTransfers(fixture.Media, fixture.Store, time);
        using var second = new OneBotFileTransfers(fixture.Media, fixture.Store, time);
        using var third = new OneBotFileTransfers(fixture.Media, fixture.Store, time);
        var prepare = new JsonObject { ["stage"] = "prepare", ["name"] = "empty", ["total_size"] = 0 };
        for (var index = 0; index < OneBotFileTransfers.MaximumPendingUploadCount; index++)
        {
            Assert.IsTrue((await first.UploadAsync(prepare)).IsSuccess);
            Assert.IsTrue((await second.UploadAsync(prepare)).IsSuccess);
        }

        Assert.AreEqual(35000, (await first.UploadAsync(prepare)).RetCode);
        Assert.AreEqual(35000, (await third.UploadAsync(prepare)).RetCode);
        Assert.HasCount(32, Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
        time.Advance(OneBotFileTransfers.IdleTimeout);
        first.SweepExpired();
        second.SweepExpired();
        Assert.IsEmpty(Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
        Assert.IsTrue((await third.UploadAsync(prepare)).IsSuccess);
    }

    [TestMethod]
    public async Task ChunkLimitsPreventOversizedAllocationsAndDisposalReleasesByteQuota()
    {
        await using var fixture = new ProtocolTestFixture();
        using var first = new OneBotFileTransfers(fixture.Media, fixture.Store);
        var prepare = new JsonObject { ["stage"] = "prepare", ["name"] = "large", ["total_size"] = fixture.Assets.MaximumByteCount };
        var prepared = await first.UploadAsync(prepare);
        Assert.IsTrue(prepared.IsSuccess);
        Assert.IsTrue((await first.UploadAsync(prepare)).IsSuccess);
        Assert.AreEqual(35000, (await first.UploadAsync(prepare)).RetCode);
        var id = prepared.Data!["file_id"]!.GetValue<string>();
        var overlarge = Convert.ToBase64String(new byte[OneBotFileTransfers.MaximumChunkByteCount + 1]);
        Assert.AreEqual(10003, (await first.UploadAsync(new JsonObject
        {
            ["stage"] = "transfer",
            ["file_id"] = id,
            ["offset"] = 0,
            ["data"] = overlarge,
        })).RetCode);
        first.Dispose();
        Assert.IsEmpty(Directory.GetFiles(fixture.Assets.DirectoryPath, ".onebot-fragment-*.tmp"));
        using var next = new OneBotFileTransfers(fixture.Media, fixture.Store);
        Assert.IsTrue((await next.UploadAsync(prepare)).IsSuccess);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static OneBotProtocol NewProtocol(ProtocolTestFixture fixture) =>
        new(OneBotVersion.V12, ProtocolTestFixture.SelfId, fixture.Platform, fixture.Media);

    private static async Task<string> PrepareAsync(OneBotProtocol protocol, long length)
    {
        var result = await protocol.HandleAsync(new ProtocolCall("upload_file_fragmented", new JsonObject
        {
            ["stage"] = "prepare",
            ["name"] = "fragment.bin",
            ["total_size"] = length,
        }));
        Assert.IsTrue(result.IsSuccess, result.Message);
        return result.Data!["file_id"]!.GetValue<string>();
    }

    private static Task<ProtocolReply> TransferAsync(OneBotProtocol protocol, string id, long offset, byte[] bytes, CancellationToken cancellationToken = default) =>
        protocol.HandleAsync(new ProtocolCall("upload_file_fragmented", new JsonObject
        {
            ["stage"] = "transfer",
            ["file_id"] = id,
            ["offset"] = offset,
            ["data"] = Convert.ToBase64String(bytes),
        }), cancellationToken);

    private static Task<ProtocolReply> FinishAsync(OneBotProtocol protocol, string id, string checksum) =>
        protocol.HandleAsync(new ProtocolCall("upload_file_fragmented", new JsonObject
        {
            ["stage"] = "finish",
            ["file_id"] = id,
            ["sha256"] = checksum,
        }));

    private static Task<ProtocolReply> GetAsync(OneBotProtocol protocol, string id, string stage, long offset = 0, long size = 0) =>
        protocol.HandleAsync(new ProtocolCall("get_file_fragmented", new JsonObject
        {
            ["stage"] = stage,
            ["file_id"] = id,
            ["offset"] = offset,
            ["size"] = size,
        }));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
