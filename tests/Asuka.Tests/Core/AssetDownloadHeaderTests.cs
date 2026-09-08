using System.Net;
using System.Text.Json;
using Asuka.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AssetDownloadHeaderTests
{
    [TestMethod]
    public async Task PerRequestHeadersAreSentWithoutPersistingCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-download-headers-{Guid.NewGuid():N}");
        var handler = new RecordingHandler();
        try
        {
            using var store = new AssetStore(directory, handler);
            var asset = await store.DownloadAsync(new Uri("https://example.test/first"), "file.bin",
                new Dictionary<string, string> { ["Authorization"] = "Bearer private-test-token", ["X-File"] = "first" });
            Assert.AreEqual("Bearer private-test-token", handler.Authorization);
            Assert.AreEqual("first", handler.CustomValue);
            Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(asset));
            await store.DownloadAsync(new Uri("https://example.test/second"));
            Assert.IsNull(handler.Authorization);
            Assert.IsNull(handler.CustomValue);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("Host", "127.0.0.1")]
    [DataRow("Connection", "upgrade")]
    [DataRow("Content-Length", "9")]
    [DataRow("Proxy-Authorization", "secret")]
    [DataRow("X-Token", "value\r\nHost: localhost")]
    [DataRow("Bad Header", "value")]
    public async Task RoutingFramingAndMalformedHeadersAreRejectedBeforeNetwork(string name, string value)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-download-headers-{Guid.NewGuid():N}");
        var handler = new RecordingHandler();
        try
        {
            using var store = new AssetStore(directory, handler);
            await Assert.ThrowsAsync<ArgumentException>(() => store.DownloadAsync(new Uri("https://example.test/file"),
                headers: new Dictionary<string, string> { [name] = value }));
            Assert.AreEqual(0, handler.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? CustomValue { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            CustomValue = request.Headers.TryGetValues("X-File", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([1, 2, 3]),
            });
        }
    }
}
