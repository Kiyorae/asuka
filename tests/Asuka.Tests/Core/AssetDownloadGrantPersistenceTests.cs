using System.Net;
using System.Text.Json;
using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AssetDownloadGrantPersistenceTests
{
    [TestMethod]
    [DataRow("download_token")]
    [DataRow("DOWNLOAD_TOKEN")]
    [DataRow("%64ownload_token")]
    public async Task EchoedGrantUrlIsUsedForDownloadButNeverSavedAsARemoteSource(string queryKey)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-grant-persistence-{Guid.NewGuid():N}");
        try
        {
            var handler = new RecordingHandler();
            using var store = new AssetStore(directory, handler);
            var uri = new Uri($"https://example.test/media?{queryKey}=temporary-download-secret&format=original");
            var asset = await store.DownloadAsync(uri);
            Assert.AreEqual(uri, handler.RequestUri);
            Assert.AreEqual(AssetSource.Inline, asset.Source);
            Assert.DoesNotContain("temporary-download-secret", JsonSerializer.Serialize(asset));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await store.GetBytesAsync(asset.Id));
            var ordinary = await store.DownloadAsync(new Uri("https://example.test/ordinary?format=original"));
            Assert.AreEqual(AssetSource.Remote("https://example.test/ordinary?format=original"), ordinary.Source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([1, 2, 3]),
            });
        }
    }
}
