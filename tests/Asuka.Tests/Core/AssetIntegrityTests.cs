using System.Net;
using System.Security.Cryptography;
using Asuka.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AssetIntegrityTests
{
    private static readonly byte[] Content = [1, 2, 3, 4];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamChecksumMismatchNeverPublishesOrDeletesSharedContent(bool alreadyStored)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-integrity-{Guid.NewGuid():N}");
        try
        {
            using var assets = new AssetStore(directory);
            var id = Convert.ToHexStringLower(SHA256.HashData(Content));
            if (alreadyStored) await assets.StoreAsync(Content, "existing.bin");
            using var stream = new MemoryStream(Content);
            var error = await Assert.ThrowsAsync<AssetChecksumMismatchException>(() =>
                assets.StoreAsync(stream, "rejected.bin", null, new string('0', 64)));
            Assert.AreEqual(id, error.ActualSha256);
            var reportedRequiredHash = error.ExpectedSha256;
            Assert.AreEqual(new string('0', 64), reportedRequiredHash);
            Assert.IsEmpty(Directory.GetFiles(directory, ".ingest-*.tmp"));
            Assert.AreEqual(alreadyStored, assets.Exists(id));
            Assert.HasCount(alreadyStored ? 1 : 0, Directory.GetFiles(directory));
            if (alreadyStored) CollectionAssert.AreEqual(Content, await assets.GetBytesAsync(id));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task DownloadChecksIntegrityBeforePublicationAndAcceptsMatchingHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-integrity-{Guid.NewGuid():N}");
        try
        {
            using var assets = new AssetStore(directory, new ContentHandler());
            var uri = new Uri("https://example.test/integrity.bin");
            await Assert.ThrowsAsync<AssetChecksumMismatchException>(() =>
                assets.DownloadAsync(uri, "rejected.bin", null, new string('0', 64)));
            Assert.IsEmpty(Directory.GetFiles(directory));
            var id = Convert.ToHexStringLower(SHA256.HashData(Content));
            var accepted = await assets.DownloadAsync(uri, "accepted.bin", null, id);
            Assert.AreEqual(id, accepted.Id);
            Assert.AreEqual("accepted.bin", accepted.Name);
            CollectionAssert.AreEqual(Content, await assets.GetBytesAsync(id));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidExpectedChecksumDoesNotCreateStagingOrStartNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-integrity-{Guid.NewGuid():N}");
        var handler = new ContentHandler();
        try
        {
            using var assets = new AssetStore(directory, handler);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                assets.DownloadAsync(new Uri("https://example.test/integrity.bin"), null, null, "INVALID"));
            using var stream = new MemoryStream(Content);
            await Assert.ThrowsAsync<ArgumentException>(() => assets.StoreAsync(stream, "invalid", null, "INVALID"));
            Assert.AreEqual(0, handler.Calls);
            Assert.AreEqual(0L, stream.Position);
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class ContentHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Content),
            });
        }
    }
}
