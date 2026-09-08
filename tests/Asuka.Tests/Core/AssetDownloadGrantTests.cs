using Asuka.Tests.Protocols;

namespace Asuka.Tests.Core;

[TestClass]
public sealed class AssetDownloadGrantTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly string FirstAsset = new('a', 64);
    private static readonly string OtherAsset = new('b', 64);

    [TestMethod]
    public async Task AssetGrantIsBoundToItsResourceAndStoreAndExpiresAfterFiveMinutes()
    {
        await using var first = new ProtocolTestFixture();
        await using var second = new ProtocolTestFixture();
        var grant = first.Assets.CreateAssetDownloadToken(FirstAsset, IssuedAt);
        Assert.IsTrue(first.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt));
        Assert.IsTrue(first.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt.AddMinutes(5).AddSeconds(-1)));
        Assert.IsFalse(first.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt.AddMinutes(5)));
        Assert.IsFalse(first.Assets.ValidateAssetDownloadToken(OtherAsset, grant, IssuedAt));
        Assert.IsFalse(second.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt));
        Assert.IsFalse(first.Assets.ValidateSharedFileDownloadToken(FirstAsset, "100", grant, IssuedAt));
        Assert.IsFalse(first.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt.AddSeconds(-1)));
    }

    [TestMethod]
    public async Task SharedFileGrantIsBoundToBothTheFileAndTheAccount()
    {
        await using var fixture = new ProtocolTestFixture();
        var grant = fixture.Assets.CreateSharedFileDownloadToken("shared-file", "100", IssuedAt);
        Assert.IsTrue(fixture.Assets.ValidateSharedFileDownloadToken("shared-file", "100", grant, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("shared-file", "200", grant, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("other-file", "100", grant, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt));
    }

    [TestMethod]
    public async Task GrantFieldsAreUnambiguousAndSignaturesCannotBeChangedOrExtended()
    {
        await using var fixture = new ProtocolTestFixture();
        var grant = fixture.Assets.CreateSharedFileDownloadToken("a:b", "c", IssuedAt);
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("a", "b:c", grant, IssuedAt));
        var separator = grant.LastIndexOf('.');
        var changed = grant[..(separator + 1)] + (grant[separator + 1] == 'a' ? 'b' : 'a') + grant[(separator + 2)..];
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("a:b", "c", changed, IssuedAt));
        var fields = grant.Split('.');
        var laterExpiry = "v1." + (IssuedAt.AddMinutes(10).ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + fields[2];
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("a:b", "c", laterExpiry, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("a:b", "c", grant + "=", IssuedAt));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("v1")]
    [DataRow("v1.0.signature")]
    [DataRow("v1.-1.signature")]
    [DataRow("v1.99999999999999999999999999.signature")]
    [DataRow("v2.1788825900.signature")]
    [DataRow("v1.1788825900.not/base64+url==")]
    public async Task MalformedTokensFailClosedWithoutThrowing(string? token)
    {
        await using var fixture = new ProtocolTestFixture();
        Assert.IsFalse(fixture.Assets.ValidateAssetDownloadToken(FirstAsset, token, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateAssetDownloadToken(FirstAsset, new string('x', 4096), IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateAssetDownloadToken("../outside", token, IssuedAt));
        Assert.IsFalse(fixture.Assets.ValidateSharedFileDownloadToken("file", new string('x', 4096), token, IssuedAt));
    }

    [TestMethod]
    public async Task DisposingTheStoreRevokesItsGrants()
    {
        await using var fixture = new ProtocolTestFixture();
        var grant = fixture.Assets.CreateAssetDownloadToken(FirstAsset, IssuedAt);
        fixture.Assets.Dispose();
        Assert.IsFalse(fixture.Assets.ValidateAssetDownloadToken(FirstAsset, grant, IssuedAt));
        Assert.Throws<ObjectDisposedException>(() => fixture.Assets.CreateAssetDownloadToken(FirstAsset));
    }
}
