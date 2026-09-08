namespace Asuka.Core;

public sealed partial class PlatformService
{
    public Task<IReadOnlyList<CustomFace>> GetCustomFacesAsync(string selfId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            return await Store.GetCustomFacesAsync(selfId, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Adds a cached image. Native importers also validate decoding before calling this method.</summary>
    public Task<CustomFace> AddCustomFaceAsync(string selfId, Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            if (Assets is null || !Assets.Exists(asset.Id))
                throw new PlatformException(PlatformError.FileNotFound, "The custom image is not available in the cache.") { ResourceId = asset.Id };
            await using var stream = await Assets.OpenReadAsync(asset.Id, token).ConfigureAwait(false);
            if (stream.Length == 0 || stream.Length > Assets.MaximumByteCount)
                throw InvalidParameter("The custom image size is invalid.");
            var header = new byte[32];
            var count = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            var mimeType = CustomFaceMimeType(header.AsSpan(0, count));
            if (mimeType is null || !string.Equals(mimeType, asset.MimeType, StringComparison.OrdinalIgnoreCase))
                throw InvalidParameter("The custom image must be a supported PNG, JPEG, GIF, WebP, or BMP with matching content.");
            return await Store.AddCustomFaceAsync(selfId,
                asset with { ByteCount = stream.Length, MimeType = mimeType, Source = AssetSource.Inline }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<bool> RemoveCustomFaceAsync(string selfId, string assetId, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            _ = await Store.GetUserAsync(selfId, token).ConfigureAwait(false) ?? throw UserNotFound(selfId);
            return await Store.RemoveCustomFaceAsync(selfId, assetId, token).ConfigureAwait(false);
        }, cancellationToken);

    private static string? CustomFaceMimeType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 24 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10])
            && header.Slice(12, 4).SequenceEqual("IHDR"u8)) return "image/png";
        if (header.Length >= 14 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (header.Length >= 4 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff) return "image/jpeg";
        if (header.Length >= 20 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (header.Length >= 26 && header[..2].SequenceEqual("BM"u8)) return "image/bmp";
        return null;
    }
}
