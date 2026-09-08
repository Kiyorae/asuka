using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asuka.Core;

public sealed partial class AssetStore
{
    public const string DownloadTokenQueryParameter = "download_token";
    private const int DownloadGrantLifetimeSeconds = 5 * 60;
    private static readonly UTF8Encoding GrantEncoding = new(false, true);
    // Instance-local and deliberately never persisted: a new store revokes old URLs.
    private readonly byte[] _downloadGrantKey = RandomNumberGenerator.GetBytes(32);

    public string CreateAssetDownloadToken(string assetId) => CreateAssetDownloadToken(assetId, DateTimeOffset.UtcNow);

    public string CreateSharedFileDownloadToken(string fileId, string selfId) =>
        CreateSharedFileDownloadToken(fileId, selfId, DateTimeOffset.UtcNow);

    public bool ValidateAssetDownloadToken(string assetId, string? token) =>
        ValidateAssetDownloadToken(assetId, token, DateTimeOffset.UtcNow);

    public bool ValidateSharedFileDownloadToken(string fileId, string selfId, string? token) =>
        ValidateSharedFileDownloadToken(fileId, selfId, token, DateTimeOffset.UtcNow);

    internal string CreateAssetDownloadToken(string assetId, DateTimeOffset issuedAt) =>
        CreateDownloadGrant(1, assetId, string.Empty, issuedAt);

    internal string CreateSharedFileDownloadToken(string fileId, string selfId, DateTimeOffset issuedAt) =>
        CreateDownloadGrant(2, fileId, selfId, issuedAt);

    internal bool ValidateAssetDownloadToken(string assetId, string? token, DateTimeOffset now) =>
        ValidateDownloadGrant(1, assetId, string.Empty, token, now);

    internal bool ValidateSharedFileDownloadToken(string fileId, string selfId, string? token, DateTimeOffset now) =>
        ValidateDownloadGrant(2, fileId, selfId, token, now);

    private string CreateDownloadGrant(byte kind, string resourceId, string selfId, DateTimeOffset issuedAt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expiry = issuedAt.ToUnixTimeSeconds() + DownloadGrantLifetimeSeconds;
        if (expiry <= 0 || !TryDownloadMac(kind, resourceId, selfId, expiry, out var mac))
            throw new ArgumentException("A download grant requires a valid resource and account identity");
        return $"v1.{expiry.ToString(CultureInfo.InvariantCulture)}.{Base64Url(mac)}";
    }

    private bool ValidateDownloadGrant(byte kind, string resourceId, string selfId, string? token, DateTimeOffset now)
    {
        if (_disposed || token is null || token.Length is < 48 or > 64 || !token.StartsWith("v1.", StringComparison.Ordinal)) return false;
        var separator = token.IndexOf('.', 3);
        if (separator is < 4 or > 15 || token.Length - separator - 1 != 43) return false;
        var expiryText = token.AsSpan(3, separator - 3);
        if (expiryText.Length > 1 && expiryText[0] == '0') return false;
        if (!long.TryParse(expiryText, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)) return false;
        var nowSeconds = now.ToUnixTimeSeconds();
        if (expiry <= nowSeconds || expiry - nowSeconds > DownloadGrantLifetimeSeconds) return false;

        Span<char> padded = stackalloc char[44];
        var signature = token.AsSpan(separator + 1);
        for (var index = 0; index < signature.Length; index++)
        {
            var character = signature[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return false;
            padded[index] = character is '-' ? '+' : character is '_' ? '/' : character;
        }
        padded[43] = '=';
        Span<byte> presented = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(padded, presented, out var length) || length != 32
            || !signature.SequenceEqual(Base64Url(presented))) return false;
        return TryDownloadMac(kind, resourceId, selfId, expiry, out var expected)
            && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private bool TryDownloadMac(byte kind, string resourceId, string selfId, long expiry, out byte[] mac)
    {
        mac = [];
        if (resourceId is null || selfId is null || resourceId.Length is 0 or > 256 || selfId.Length > 256) return false;
        if (kind == 1)
        {
            if (selfId.Length != 0 || resourceId.Length != 64 || resourceId.Any(static c => !char.IsAsciiHexDigit(c))) return false;
        }
        else if (kind != 2 || selfId.Length == 0) return false;
        byte[] resource;
        byte[] owner;
        try
        {
            resource = GrantEncoding.GetBytes(resourceId);
            owner = GrantEncoding.GetBytes(selfId);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        // Length framing prevents delimiter/Unicode ambiguity between identities.
        var message = new byte[18 + resource.Length + owner.Length];
        message[0] = 1;
        message[1] = kind;
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(2), expiry);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10), resource.Length);
        resource.CopyTo(message, 14);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14 + resource.Length), owner.Length);
        owner.CopyTo(message, 18 + resource.Length);
        mac = HMACSHA256.HashData(_downloadGrantKey, message);
        return true;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool HasDownloadGrantQuery(Uri uri)
    {
        var query = uri.Query;
        var offset = 1;
        while (offset < query.Length)
        {
            var end = query.IndexOf('&', offset);
            if (end < 0) end = query.Length;
            var separator = query.IndexOf('=', offset, end - offset);
            var keyEnd = separator < 0 ? end : separator;
            if (keyEnd - offset <= 128
                && Uri.UnescapeDataString(query[offset..keyEnd].Replace('+', ' '))
                    .Equals(DownloadTokenQueryParameter, StringComparison.OrdinalIgnoreCase)) return true;
            offset = end + 1;
        }
        return false;
    }
}
