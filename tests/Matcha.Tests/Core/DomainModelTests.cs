using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Matcha.Tests.Core;

[TestClass]
public sealed class DomainModelTests
{
    [TestMethod]
    public void GeneratedProtocolIdentifiersAreNumericAndFitInt64()
    {
        var ids = Enumerable.Range(0, 100)
            .SelectMany(_ => new[] { Matcha.Core.IdGenerator.UserId(), Matcha.Core.IdGenerator.GroupId(), Matcha.Core.IdGenerator.MessageId() })
            .ToArray();

        Assert.HasCount(300, ids.Distinct(StringComparer.Ordinal));
        Assert.IsTrue(ids.All(id => id.All(char.IsAsciiDigit)));
        Assert.IsTrue(ids.All(id => long.TryParse(id, out var value) && value is > 0 and <= 9_007_199_254_740_991L));

        var first = Matcha.Core.IdGenerator.EncodeTimestampForTesting(1_800_000_000_000L);
        var afterFormerWrapPeriod = Matcha.Core.IdGenerator.EncodeTimestampForTesting(1_800_100_000_000L);
        Assert.AreNotEqual(first, afterFormerWrapPeriod);
    }

    [TestMethod]
    public void JsonNumericEqualityHasMatchingHashCode()
    {
        var integer = Matcha.Core.JsonValue.Parse("1");
        var floatingPoint = Matcha.Core.JsonValue.Parse("1.0");

        Assert.AreEqual(integer, floatingPoint);
        Assert.AreEqual(integer.GetHashCode(), floatingPoint.GetHashCode());

        Matcha.Core.JsonValue unset = default;
        Assert.AreEqual(Matcha.Core.JsonValue.Null, unset);
        Assert.AreEqual("null", unset.ToJsonString());
    }

    [TestMethod]
    public async Task AssetStoreDeduplicatesWindowsPathsAndInlineData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"matcha-assets-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "source.txt");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(sourcePath, "matcha", Encoding.UTF8);
        try
        {
            using var store = new Matcha.Core.AssetStore(Path.Combine(directory, "store"));
            var fromPath = await store.IngestAsync(sourcePath);
            var fromUri = await store.IngestAsync(new Uri(sourcePath).AbsoluteUri);
            var fromData = await store.IngestAsync(
                $"data:text/plain;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(sourcePath))}",
                suggestedName: "source.txt");

            Assert.IsNotNull(fromPath);
            Assert.AreEqual(fromPath.Id, fromUri?.Id);
            Assert.AreEqual(fromPath.Id, fromData?.Id);
            Assert.AreEqual(Matcha.Core.AssetSourceKind.Local, fromPath.Source.Kind);
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(sourcePath), await store.GetBytesAsync(fromPath.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AssetStoreDoesNotFollowRedirects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"matcha-assets-{Guid.NewGuid():N}");
        var handler = new RedirectHandler();
        try
        {
            using var store = new Matcha.Core.AssetStore(directory, handler);
            _ = await Assert.ThrowsAsync<HttpRequestException>(
                () => store.IngestAsync("https://example.test/source"));
            Assert.AreEqual(1, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://127.0.0.1/private") },
                RequestMessage = request,
            });
        }
    }
}
