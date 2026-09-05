using System.Text;
using Asuka.Core;
using Asuka.Protocols;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.Protocols;

[TestClass]
public sealed class MediaServiceSecurityTests
{
    [TestMethod]
    [DataRow(@"C:\Users\alice\secret.txt")]
    [DataRow(@"\\server\share\secret.txt")]
    [DataRow("file:///C:/Users/alice/secret.txt")]
    [DataRow("FILE:C:/Users/alice/secret.txt")]
    [DataRow(@"  C:\Users\alice\secret.txt  ")]
    [DataRow(@"  \\server\share\secret.txt  ")]
    [DataRow("  file:///C:/Windows/win.ini  ")]
    public async Task ProtocolMediaRejectsLocalAndUncReferences(string reference)
    {
        await using var fixture = new ProtocolTestFixture();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Media.ResolveReferenceAsync(reference, null, ProtocolAssetKind.File));
    }

    [TestMethod]
    public async Task ProtocolMediaAcceptsInlineAndExistingAssetReferences()
    {
        await using var fixture = new ProtocolTestFixture();
        var inline = "data:text/plain;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("safe inline"));

        var inlineAsset = await fixture.Media.ResolveReferenceAsync(inline, null, ProtocolAssetKind.File);
        Assert.IsNotNull(inlineAsset);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("safe inline"), await fixture.Assets.GetBytesAsync(inlineAsset.Id));

        var existing = await fixture.Assets.StoreAsync(Encoding.UTF8.GetBytes("cached"), "cached.txt");
        var resolved = await fixture.Media.ResolveReferenceAsync(
            $"asuka-asset://{existing.Id}",
            null,
            ProtocolAssetKind.File);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(existing.Id, resolved.Id);
    }

    [TestMethod]
    public async Task ProtocolMediaRejectsUnsafeFallbackBeforeDownloading()
    {
        await using var fixture = new ProtocolTestFixture();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Media.ResolveReferenceAsync(
                "https://example.test/image.png",
                @"  \\server\share\fallback.png  ",
                ProtocolAssetKind.Image));
    }
}
