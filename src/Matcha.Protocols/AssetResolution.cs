using Matcha.Core;

namespace Matcha.Protocols;

public enum ProtocolAssetKind
{
    Image,
    Record,
    Video,
    File,
}

public sealed record ProtocolAssetReference(
    string Identifier,
    string Url,
    Asset? ResolvedAsset = null);

public interface IProtocolAssetResolver
{
    Task<ProtocolAssetReference> GetReferenceAsync(
        Asset asset,
        bool preferLocalPath,
        CancellationToken cancellationToken = default);

    Task<Asset?> ResolveIdAsync(
        string identifier,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default);

    Task<Asset?> ResolveReferenceAsync(
        string reference,
        string? fallbackUrl,
        ProtocolAssetKind kind,
        CancellationToken cancellationToken = default);
}
