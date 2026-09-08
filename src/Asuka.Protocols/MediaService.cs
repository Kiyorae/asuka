using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class MediaService(AsukaStore store, AssetStore assets) : IProtocolAssetResolver
{
    private string _host = "127.0.0.1";
    private ushort _port = ConnectionSettings.DefaultPort;

    public AssetStore Assets { get; } = assets;

    public void SetEndpoint(string host, ushort port, string? advertisedHost = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = !string.IsNullOrWhiteSpace(advertisedHost)
            ? advertisedHost
            : host is "0.0.0.0" or "::"
                ? System.Net.Dns.GetHostName()
                : host;
        _port = port;
    }

    public Uri GetUrl(string assetId)
    {
        return new UriBuilder(Uri.UriSchemeHttp, _host, _port, $"/assets/{Uri.EscapeDataString(assetId)}")
        {
            Query = $"{AssetStore.DownloadTokenQueryParameter}={Assets.CreateAssetDownloadToken(assetId)}",
        }.Uri;
    }

    public string GetPath(string assetId) => Assets.LocationOf(assetId);

    public Uri GetSharedFileUrl(string fileId, string selfId) =>
        new UriBuilder(Uri.UriSchemeHttp, _host, _port, $"/files/{Uri.EscapeDataString(fileId)}")
        {
            Query = $"{AssetStore.DownloadTokenQueryParameter}={Assets.CreateSharedFileDownloadToken(fileId, selfId)}",
        }.Uri;

    /// <summary>Returns a local playback source, converting legacy SILK to cached WAV when needed.</summary>
    public async Task<string> GetPlayableAudioPathAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var reference = await GetReferenceAsync(asset, preferLocalPath: true, cancellationToken).ConfigureAwait(false);
        var resolved = reference.ResolvedAsset ?? asset;
        var path = Assets.LocationOf(resolved.Id);
        var header = await ReadHeaderAsync(resolved, cancellationToken).ConfigureAwait(false);
        return SilkAudioConverter.IsSilk(header)
            ? await SilkAudioConverter.GetWavePathAsync(
                path, Path.Combine(Assets.DirectoryPath, ".audio-cache"), cancellationToken).ConfigureAwait(false)
            : path;
    }

    /// <summary>
    /// Reads only the leading bytes needed to inspect an asset's container metadata.
    /// This deliberately avoids decoding image data or loading an entire attachment.
    /// </summary>
    internal async Task<byte[]> ReadHeaderAsync(
        Asset asset,
        CancellationToken cancellationToken = default)
    {
        const int maximumHeaderByteCount = 256 * 1024;
        ArgumentNullException.ThrowIfNull(asset);

        var path = Assets.LocationOf(asset.Id);
        var length = checked((int)Math.Min(new FileInfo(path).Length, maximumHeaderByteCount));
        if (length == 0)
        {
            return [];
        }

        var header = new byte[length];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = 0;
        while (read < header.Length)
        {
            var current = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        return read == header.Length ? header : header[..read];
    }

    public async Task<ProtocolAssetReference> GetReferenceAsync(
        Asset asset,
        bool preferLocalPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = asset;
        if (!Assets.Exists(asset.Id))
        {
            if (asset.Source is not { Kind: AssetSourceKind.Local or AssetSourceKind.Remote, Value: { } source })
            {
                throw new FileNotFoundException("The cached asset is no longer available.", asset.Id);
            }

            resolved = await Assets.IngestAsync(
                source,
                suggestedName: asset.Name,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The asset source is no longer available.", source);
            await store.SaveAsync(resolved, cancellationToken).ConfigureAwait(false);
        }

        var identifier = preferLocalPath ? Assets.LocationOf(resolved.Id) : resolved.Id;
        return new ProtocolAssetReference(
            identifier,
            GetUrl(resolved.Id).AbsoluteUri,
            resolved);
    }

    public async Task<Asset?> ResolveIdAsync(
        string identifier,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        var metadata = await store.GetAssetAsync(identifier, cancellationToken).ConfigureAwait(false);
        if (metadata is not null && Assets.Exists(identifier))
        {
            return metadata;
        }

        if (metadata?.Source is { Kind: AssetSourceKind.Local or AssetSourceKind.Remote, Value: { } source })
        {
            // Persisted metadata was created by the app's trusted attachment
            // flow.  Rehydrating it must retain local-file support; only a new
            // reference received from a protocol peer goes through the stricter
            // ResolveReferenceAsync boundary.
            return await IngestAsync(source, metadata.Name, cancellationToken).ConfigureAwait(false);
        }

        if (!Assets.Exists(identifier))
        {
            return null;
        }

        var bytes = await Assets.GetBytesAsync(identifier, cancellationToken).ConfigureAwait(false);
        var asset = new Asset(
            identifier,
            DefaultName(kind),
            AssetStore.MimeTypeForFileName(DefaultName(kind)),
            bytes.LongLength,
            AssetSource.Inline);
        await store.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        return asset;
    }

    public async Task<Asset?> ResolveReferenceAsync(
        string reference,
        string? fallbackUrl,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default)
    {
        var normalizedReference = NormalizeProtocolReference(reference);
        var normalizedFallbackUrl = fallbackUrl?.Trim();
        RejectProtocolLocalReference(normalizedReference);
        RejectProtocolLocalReference(normalizedFallbackUrl);
        var asset = await Assets.IngestAsync(
            normalizedReference,
            normalizedFallbackUrl,
            DefaultName(kind),
            cancellationToken).ConfigureAwait(false);
        if (asset is not null)
        {
            await store.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        }

        return asset;
    }

    public async Task<Asset?> IngestAsync(
        string reference,
        string? suggestedName = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await Assets.IngestAsync(
            reference,
            suggestedName: suggestedName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (asset is not null)
        {
            await store.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        }

        return asset;
    }

    private static string DefaultName(ProtocolAssetKind kind) => kind switch
    {
        ProtocolAssetKind.Image => "image.png",
        ProtocolAssetKind.Record => "record.amr",
        ProtocolAssetKind.Video => "video.mp4",
        _ => "file.bin",
    };

    private static void RejectProtocolLocalReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        // This method processes content supplied by a bot over the protocol.
        // Never let that content turn a full-trust desktop process into a local
        // file or SMB reader.  The app's own attachment flow still uses
        // IngestAsync/StoreAsync and intentionally retains local-file support.
        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("\\\\", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(reference))
        {
            throw new UnauthorizedAccessException(
                "Protocol media references must not name local files or UNC shares.");
        }
    }

    private static string NormalizeProtocolReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Trim();
    }
}
