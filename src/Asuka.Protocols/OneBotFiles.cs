using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Asuka.Core;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private async Task<ProtocolReply> UploadFileAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (_media is null)
        {
            return Unsupported("upload_file");
        }

        var type = call.GetText("type");
        var name = call.GetText("name");
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name))
        {
            return Invalid("Missing type or name");
        }

        var checksum = call.Parameters["sha256"] is JsonValue checksumValue && checksumValue.TryGetValue<string>(out var checksumText)
            ? checksumText : null;
        if (call.Parameters.ContainsKey("sha256") && (checksum is null || !OneBotFileTransfers.IsLowercaseSha256(checksum)))
        {
            return Invalid("sha256 must be a lowercase SHA256 checksum");
        }

        Asset? asset;
        if (type == "data")
        {
            if (call.Parameters["data"] is not JsonValue dataValue || !dataValue.TryGetValue<string>(out var encoded))
            {
                return Invalid("Missing data");
            }

            if (encoded.Length > (_media.Assets.MaximumByteCount + 2) / 3 * 4)
            {
                return Invalid("File exceeds the asset size limit");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return Invalid("data must be Base64 encoded");
            }

            if (bytes.LongLength > _media.Assets.MaximumByteCount)
            {
                return Invalid("File exceeds the asset size limit");
            }

            if (checksum is not null && checksum != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            {
                return new ProtocolReply(35000, Message: "File SHA256 checksum does not match");
            }

            asset = await _media.Assets.StoreAsync(bytes, name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (type == "url")
        {
            if (call.Parameters.ContainsKey("headers") && call.Parameters["headers"] is not JsonObject)
            {
                return Invalid("headers must be a string map");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (call.Parameters["headers"] is JsonObject headerObject)
            {
                foreach (var (key, value) in headerObject)
                {
                    if (value is not JsonValue headerValue || !headerValue.TryGetValue<string>(out var text)
                        || !headers.TryAdd(key, text))
                    {
                        return Invalid("headers must be a string map without duplicate header names");
                    }
                }
            }

            var url = call.Parameters["url"] is JsonValue urlValue && urlValue.TryGetValue<string>(out var urlText)
                ? urlText : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            {
                return Invalid("url must use HTTP or HTTPS");
            }

            try
            {
                asset = await _media.Assets.DownloadAsync(parsed, name, headers, checksum, cancellationToken).ConfigureAwait(false);
            }
            catch (AssetChecksumMismatchException error)
            {
                return new ProtocolReply(35000, Message: error.Message);
            }
            catch (HttpRequestException error)
            {
                return new ProtocolReply(33000, Message: error.Message);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new ProtocolReply(32000, Message: error.Message);
            }

        }
        else
        {
            // Incoming bot actions cannot read arbitrary files from the desktop.
            return new ProtocolReply(10004, Message: $"Unsupported upload type: {type}");
        }

        await _store.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success(new JsonObject { ["file_id"] = asset.Id });
    }

    private async Task<ProtocolReply> GetFileAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (_media is null)
        {
            return Unsupported("get_file");
        }

        var id = call.GetText("file_id");
        var type = call.GetText("type");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
        {
            return Invalid("Missing file_id or type");
        }

        if (type is not ("url" or "path" or "data"))
        {
            return new ProtocolReply(10004, Message: $"Unsupported download type: {type}");
        }

        var asset = await _media.ResolveIdAsync(id, ProtocolAssetKind.File, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return new ProtocolReply(35000, Message: "File not found");
        }

        var result = new JsonObject { ["name"] = asset.Name, ["sha256"] = asset.Id };
        result[type] = type switch
        {
            "url" => _media.GetUrl(asset.Id).AbsoluteUri,
            "path" => _media.GetPath(asset.Id),
            _ => Convert.ToBase64String(await _media.Assets.GetBytesAsync(asset.Id, cancellationToken).ConfigureAwait(false)),
        };
        return ProtocolReply.Success(result);
    }
}
