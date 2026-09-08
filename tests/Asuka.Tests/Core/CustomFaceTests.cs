using Asuka.Core;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class CustomFaceTests
{
    private const string Alice = "10001";
    private const string Bob = "10002";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRZsAAAAASUVORK5CYII=";
    private const string Gif = "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    [TestMethod]
    public async Task LibraryIsPersistentAccountScopedDeduplicatedAndStablyOrdered()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-custom-faces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var assets = new AssetStore(Path.Combine(directory, "assets"));
            var png = await assets.StoreAsync(Convert.FromBase64String(Png), "face.png", "image/png");
            var gif = await assets.StoreAsync(Convert.FromBase64String(Gif), "animated.gif", "image/gif");
            var database = Path.Combine(directory, "store.sqlite3");
            CustomFace first;
            await using (var store = new AsukaStore(database))
            await using (var platform = new PlatformService(store, assets))
            {
                await store.SaveAsync(new User("Alice", id: Alice));
                await store.SaveAsync(new User("Bob", id: Bob));
                first = await platform.AddCustomFaceAsync(Alice, png);
                var second = await platform.AddCustomFaceAsync(Alice, gif);
                var duplicate = await platform.AddCustomFaceAsync(Alice, png with { Name = "renamed.png" });
                Assert.AreEqual(first, duplicate);
                Assert.IsGreaterThan(first.Sequence, second.Sequence);
                await platform.AddCustomFaceAsync(Bob, png);
                CollectionAssert.AreEqual((string[])[png.Id, gif.Id],
                    (await platform.GetCustomFacesAsync(Alice)).Select(face => face.Asset.Id).ToArray());
                Assert.HasCount(1, await platform.GetCustomFacesAsync(Bob));
            }

            await using (var reopened = new AsukaStore(database))
            await using (var platform = new PlatformService(reopened, assets))
            {
                Assert.AreEqual(first, (await platform.GetCustomFacesAsync(Alice))[0]);
                Assert.IsFalse(await platform.RemoveCustomFaceAsync(Bob, gif.Id));
                Assert.HasCount(2, await platform.GetCustomFacesAsync(Alice));
                Assert.IsTrue(await platform.RemoveCustomFaceAsync(Alice, png.Id));
                Assert.IsFalse(await platform.RemoveCustomFaceAsync(Alice, png.Id));
                Assert.HasCount(1, await platform.GetCustomFacesAsync(Bob));
                Assert.IsNotNull(await reopened.GetAssetAsync(png.Id));
                CollectionAssert.AreEqual(Convert.FromBase64String(Png), await assets.GetBytesAsync(png.Id));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ImportRequiresAnExistingAccountAndRecognizedCachedImageAndHonorsCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-custom-face-validation-{Guid.NewGuid():N}");
        try
        {
            using var assets = new AssetStore(directory);
            await using var store = new AsukaStore();
            await using var platform = new PlatformService(store, assets);
            await store.SaveAsync(new User("Alice", id: Alice));
            var png = await assets.StoreAsync(Convert.FromBase64String(Png), "face.png", "image/png");
            var bad = await assets.StoreAsync((byte[])[1, 2, 3], "fake.png", "image/png");
            var missingUser = await Assert.ThrowsAsync<PlatformException>(() => platform.AddCustomFaceAsync(Bob, png));
            Assert.AreEqual(PlatformError.UserNotFound, missingUser.Error);
            var invalid = await Assert.ThrowsAsync<PlatformException>(() => platform.AddCustomFaceAsync(Alice, bad));
            Assert.AreEqual(PlatformError.InvalidParameter, invalid.Error);
            var missingImage = await Assert.ThrowsAsync<PlatformException>(() =>
                platform.AddCustomFaceAsync(Alice, png with { Id = new string('a', 64) }));
            Assert.AreEqual(PlatformError.FileNotFound, missingImage.Error);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => platform.AddCustomFaceAsync(Alice, png, cancelled.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => platform.GetCustomFacesAsync(Alice, cancelled.Token));
            Assert.IsEmpty(await platform.GetCustomFacesAsync(Alice));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ConcurrentImportsThroughSeparateServicesProduceOneCollectionEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"asuka-custom-face-concurrency-{Guid.NewGuid():N}");
        try
        {
            using var assets = new AssetStore(directory);
            await using var store = new AsukaStore();
            await store.SaveAsync(new User("Alice", id: Alice));
            await using var first = new PlatformService(store, assets);
            await using var second = new PlatformService(store, assets);
            var image = await assets.StoreAsync(Convert.FromBase64String(Png), "face.png", "image/png");
            var results = await Task.WhenAll(first.AddCustomFaceAsync(Alice, image), second.AddCustomFaceAsync(Alice, image));
            Assert.AreEqual(results[0], results[1]);
            Assert.HasCount(1, await first.GetCustomFacesAsync(Alice));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
