using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Asuka.Core;

public sealed class AssetStore : IDisposable
{
    public const long DefaultMaximumByteCount = 64L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private bool _disposed;

    public AssetStore(string directory, long maximumByteCount = DefaultMaximumByteCount)
        : this(directory, CreateHttpHandler(), maximumByteCount)
    {
    }

    internal AssetStore(string directory, HttpMessageHandler httpHandler, long maximumByteCount = DefaultMaximumByteCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(httpHandler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteCount);
        DirectoryPath = Path.GetFullPath(directory);
        MaximumByteCount = maximumByteCount;
        Directory.CreateDirectory(DirectoryPath);
        _httpClient = new HttpClient(httpHandler, disposeHandler: true);
    }

    public string DirectoryPath { get; }
    public long MaximumByteCount { get; }

    public static AssetStore DefaultLocation() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Asuka",
            "Cache",
            "assets"));

    public string LocationOf(string id)
    {
        ValidateAssetId(id);
        return Path.Combine(DirectoryPath, id.ToLowerInvariant());
    }

    public bool Exists(string id)
    {
        try
        {
            return File.Exists(LocationOf(id));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public Task<byte[]> GetBytesAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return File.ReadAllBytesAsync(LocationOf(id), cancellationToken);
    }

    public async Task<Asset> StoreAsync(
        ReadOnlyMemory<byte> data,
        string name,
        string? mimeType = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.Length > MaximumByteCount)
        {
            throw new AssetTooLargeException(MaximumByteCount);
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(data.Span));
        var destination = LocationOf(id);
        if (!File.Exists(destination))
        {
            var temporary = Path.Combine(DirectoryPath, $".store-{Guid.NewGuid():N}.tmp");
            try
            {
                await using var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Close();
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return new Asset(
            id,
            string.IsNullOrEmpty(name) ? id : name,
            mimeType ?? MimeTypeForFileName(name),
            data.Length,
            AssetSource.Inline);
    }

    public async Task<Asset> StoreAsync(
        Stream source,
        string name,
        string? mimeType = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        var temporary = Path.Combine(DirectoryPath, $".ingest-{Guid.NewGuid():N}.tmp");
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) != 0)
                    {
                        length += read;
                        if (length > MaximumByteCount)
                        {
                            throw new AssetTooLargeException(MaximumByteCount);
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var id = Convert.ToHexStringLower(hash.GetHashAndReset());
            var destination = LocationOf(id);
            if (File.Exists(destination))
            {
                File.Delete(temporary);
            }
            else
            {
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }

            return new Asset(
                id,
                string.IsNullOrEmpty(name) ? id : name,
                mimeType ?? MimeTypeForFileName(name),
                length,
                AssetSource.Inline);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<Asset?> IngestAsync(
        string reference,
        string? fallbackUrl = null,
        string? suggestedName = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (TryGetStoredAssetId(reference, out var existingId) && Exists(existingId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(LocationOf(existingId)).Length;
            if (length > MaximumByteCount)
            {
                throw new AssetTooLargeException(MaximumByteCount);
            }

            return new Asset(
                existingId,
                suggestedName ?? existingId,
                MimeTypeForFileName(suggestedName ?? string.Empty),
                length,
                AssetSource.Inline);
        }

        if (IsInlineReference(reference)
            && reference.Length > Math.Min(int.MaxValue, ((MaximumByteCount + 2) / 3 * 4) + 1_024))
        {
            throw new AssetTooLargeException(MaximumByteCount);
        }

        if (TryParseInline(reference, out var inline, out var inlineMimeType))
        {
            if (inline.LongLength > MaximumByteCount)
            {
                throw new AssetTooLargeException(MaximumByteCount);
            }

            return await StoreAsync(
                inline,
                suggestedName ?? "inline",
                inlineMimeType,
                cancellationToken).ConfigureAwait(false);
        }

        if (TryGetFilePath(reference, out var path))
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaximumByteCount)
            {
                throw new AssetTooLargeException(MaximumByteCount);
            }

            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var asset = await StoreAsync(
                input,
                suggestedName ?? Path.GetFileName(path),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return asset with { Source = AssetSource.Local(path) };
        }

        Exception? lastDownloadError = null;
        foreach (var candidate in new[] { reference, fallbackUrl })
        {
            if (!TryGetHttpUri(candidate, out var uri))
            {
                continue;
            }

            try
            {
                return await DownloadAsync(uri, suggestedName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastDownloadError = exception;
            }
        }

        if (lastDownloadError is not null)
        {
            throw lastDownloadError;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    public static string? MimeTypeForFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".amr" => "audio/amr",
        ".silk" => "audio/silk",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => null,
    };

    private async Task<Asset> DownloadAsync(
        Uri uri,
        string? suggestedName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
        {
            throw new HttpRequestException("Asset downloads must not follow redirects.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > MaximumByteCount)
        {
            throw new AssetTooLargeException(MaximumByteCount);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var name = suggestedName ?? GetRemoteName(uri, response.Content.Headers.ContentDisposition);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var asset = await StoreAsync(stream, name, mediaType, cancellationToken).ConfigureAwait(false);
        return asset with { Source = AssetSource.Remote(uri.AbsoluteUri) };
    }

    private static SocketsHttpHandler CreateHttpHandler() => new()
    {
        // Downloads originate from protocol-controlled content.  Do not inherit a
        // machine proxy or cookie jar, and resolve each connection ourselves so
        // that the address checked below is the one that is actually connected.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = ConnectToPublicInternetAsync,
    };

    private static async ValueTask<Stream> ConnectToPublicInternetAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsPublicInternetAddress);
        if (address is null)
        {
            throw new HttpRequestException("Asset downloads may only connect to public Internet addresses.");
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Determines whether an address is globally routable enough for an untrusted
    /// media download.  Keep this deliberately conservative: local, link-local,
    /// carrier-grade NAT, documentation, benchmark, multicast, and reserved
    /// networks are not useful asset origins and are common SSRF targets.
    /// </summary>
    internal static bool IsPublicInternetAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicInternetAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];

            if (first is 0 or 10 or 127 or >= 224
                || (first == 100 && second is >= 64 and <= 127)
                || (first == 169 && second == 254)
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && (second == 0 || second == 168))
                || (first == 192 && second == 2)
                || (first == 198 && (second is 18 or 19 or 51))
                || (first == 203 && second == 0))
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        var ipv6 = address.GetAddressBytes();
        // Unique local fc00::/7; discard-only 100::/64; documentation,
        // benchmarking, and ORCHIDv2 allocations in 2001::/16.
        if ((ipv6[0] & 0xfe) == 0xfc
            || (ipv6[0] == 0x01 && ipv6[1] == 0x00 && ipv6.Skip(2).All(static value => value == 0))
            || (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8)
            || (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x00 && (ipv6[3] is 0x02 or >= 0x10 and <= 0x1f)))
        {
            return false;
        }

        return true;
    }

    private static string GetRemoteName(Uri uri, ContentDispositionHeaderValue? disposition)
    {
        var headerName = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            return headerName.Trim('"');
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        return string.IsNullOrEmpty(fileName) ? "download" : fileName;
    }

    private static bool TryParseInline(string reference, out byte[] data, out string? mimeType)
    {
        data = [];
        mimeType = null;
        if (reference.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                data = Convert.FromBase64String(reference[9..]);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        if (!reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = reference.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var metadata = reference[5..comma];
        var value = reference[(comma + 1)..];
        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        mimeType = parts.FirstOrDefault(part => part.Contains('/'));
        try
        {
            data = parts.Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase))
                ? Convert.FromBase64String(value)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(value));
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }

    private static bool TryGetFilePath(string reference, out string path)
    {
        path = string.Empty;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = Path.GetFullPath(uri.LocalPath);
            return true;
        }

        if (Path.IsPathFullyQualified(reference))
        {
            path = Path.GetFullPath(reference);
            return true;
        }

        return false;
    }

    private static bool IsInlineReference(string reference) =>
        reference.StartsWith("base64://", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool TryGetStoredAssetId(string reference, out string id)
    {
        const string prefix = "asuka-asset://";
        if (reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            id = reference[prefix.Length..].Trim('/');
            return IsValidAssetId(id);
        }

        id = string.Empty;
        return false;
    }

    private static void ValidateAssetId(string id)
    {
        if (!IsValidAssetId(id))
        {
            throw new ArgumentException("An asset ID must be a 64-character SHA-256 hexadecimal digest.", nameof(id));
        }
    }

    private static bool IsValidAssetId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit);
}

public sealed class AssetTooLargeException(long maximumByteCount)
    : IOException($"The asset exceeds the configured limit of {maximumByteCount} bytes.")
{
    public long MaximumByteCount { get; } = maximumByteCount;
}
