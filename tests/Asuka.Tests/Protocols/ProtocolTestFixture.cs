using System.Net;
using System.Net.Sockets;
using Asuka.Core;
using Asuka.Protocols;

namespace Asuka.Tests.Protocols;

internal sealed class ProtocolTestFixture : IAsyncDisposable
{
    internal const string SelfId = "1000000001";
    internal const string SenderId = "1000000002";
    internal const string GroupId = "500000001";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"asuka-protocol-tests-{Guid.NewGuid():N}");

    internal ProtocolTestFixture()
    {
        Store = new AsukaStore();
        Platform = new PlatformService(Store);
        Assets = new AssetStore(Path.Combine(_directory, "assets"));
        Media = new MediaService(Store, Assets);
    }

    internal AsukaStore Store { get; }

    internal PlatformService Platform { get; }

    internal AssetStore Assets { get; }

    internal MediaService Media { get; }

    internal async Task SeedGroupAsync()
    {
        await Store.SaveAsync(new User("Asuka", id: SelfId, nickname: "Asuka Bot"));
        await Store.SaveAsync(new User("Sender", id: SenderId, nickname: "Alice"));
        await Store.SaveAsync(new Group("Protocol Test Group", id: GroupId, intro: "Wire tests"));
        await Store.SaveAsync(new GroupMember(GroupId, SelfId, role: GroupRole.Owner));
        await Store.SaveAsync(new GroupMember(
            GroupId,
            SenderId,
            card: "Alice Card",
            role: GroupRole.Admin,
            title: "Maintainer"));
        Platform.RegisterBot(SelfId);
    }

    internal static ushort ReserveEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        return checked((ushort)endpoint.Port);
    }

    public async ValueTask DisposeAsync()
    {
        await Platform.DisposeAsync();
        await Store.DisposeAsync();
        Assets.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class StubProtocolAssetResolver : IProtocolAssetResolver
{
    public Task<ProtocolAssetReference> GetReferenceAsync(
        Asset asset,
        bool preferLocalPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identifier = preferLocalPath ? $"local/{asset.Id}" : asset.Id;
        return Task.FromResult(new ProtocolAssetReference(
            identifier,
            $"http://127.0.0.1:5700/assets/{asset.Id}"));
    }

    public Task<Asset?> ResolveIdAsync(
        string identifier,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Asset?>(AssetFor(identifier, kind));
    }

    public Task<Asset?> ResolveReferenceAsync(
        string reference,
        string? fallbackUrl,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        _ = fallbackUrl;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Asset?>(AssetFor(reference, kind));
    }

    private static Asset AssetFor(string identifier, ProtocolAssetKind kind)
    {
        var name = kind switch
        {
            ProtocolAssetKind.Image => "image.png",
            ProtocolAssetKind.Record => "record.amr",
            ProtocolAssetKind.Video => "video.mp4",
            _ => "file.bin",
        };
        return new Asset(identifier, name, ByteCount: 4, Source: AssetSource.Inline);
    }
}
