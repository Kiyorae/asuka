using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Asuka.Core;

namespace Asuka.Protocols;

public sealed partial class OneBotProtocol
{
    private async Task<ProtocolReply> SendLikeAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        var userId = call.GetId("user_id");
        if (call.GetLong("user_id") is not > 0) return Invalid("user_id must be a positive QQ number");
        var times = call.GetInteger("times");
        if ((call.Parameters.ContainsKey("times") && times is null) || times is <= 0 or > 10)
            return Invalid("times must be an integer between 1 and 10");
        await _platform.SendProfileLikeAsync(userId!, SelfId, times ?? 1, dailyLimit: 10, cancellationToken).ConfigureAwait(false);
        return ProtocolReply.Success();
    }

    private async Task<ProtocolReply> GetImageAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (_media is null) return Unsupported("get_image");
        if (call.Parameters["file"] is not System.Text.Json.Nodes.JsonValue value
            || !value.TryGetValue<string>(out var reference) || string.IsNullOrWhiteSpace(reference))
            return Invalid("file must be a non-empty image identifier");
        var asset = await FindCachedStandardMediaAsync(reference, cancellationToken).ConfigureAwait(false);
        if (asset is null) return new ProtocolReply(1404, Message: "Cached image not found");
        if (!ImageDimensions.TryRead(await _media.ReadHeaderAsync(asset, cancellationToken).ConfigureAwait(false), out _, out _))
            return Invalid("The cached file is not a supported image");
        return ProtocolReply.Success(new JsonObject { ["file"] = _media.GetPath(asset.Id) });
    }

    private async Task<ProtocolReply> GetRecordAsync(ProtocolCall call, CancellationToken cancellationToken)
    {
        if (_media is null) return Unsupported("get_record");
        if (call.Parameters["file"] is not System.Text.Json.Nodes.JsonValue value
            || !value.TryGetValue<string>(out var reference) || string.IsNullOrWhiteSpace(reference))
            return Invalid("file must be a non-empty recording identifier");
        var format = call.GetText("out_format");
        if (format is not ("mp3" or "amr" or "wma" or "m4a" or "spx" or "ogg" or "wav" or "flac"))
            return Invalid("out_format must be mp3, amr, wma, m4a, spx, ogg, wav or flac");
        var asset = await FindCachedStandardMediaAsync(reference, cancellationToken).ConfigureAwait(false);
        if (asset is null) return new ProtocolReply(1404, Message: "Cached recording not found");
        var header = await _media.ReadHeaderAsync(asset, cancellationToken).ConfigureAwait(false);
        if (SilkAudioConverter.IsSilk(header))
        {
            if (format != "wav") return UnsupportedRecordConversion("silk", format);
            // Do not let a concurrent cache removal rehydrate an original local or
            // remote source through this cache-only query.
            var path = await _media.GetPlayableAudioPathAsync(asset with { Source = AssetSource.Inline }, cancellationToken).ConfigureAwait(false);
            return ProtocolReply.Success(new JsonObject { ["file"] = path });
        }

        var nativeFormat = DetectNativeRecordFormat(header, new FileInfo(_media.GetPath(asset.Id)).Length);
        if (nativeFormat is null)
        {
            if (IsUnsupportedRecordContainer(header))
                return new ProtocolReply(1404, Message: "Conversion from this media container is not supported by the built-in audio converter");
            return Invalid("The cached file is not a supported audio recording");
        }
        if (nativeFormat != format) return UnsupportedRecordConversion(nativeFormat, format);
        // The existing cache filename is content-addressed. Its extension is not
        // used as evidence of a codec, and the bytes are never merely relabeled.
        return ProtocolReply.Success(new JsonObject { ["file"] = _media.GetPath(asset.Id) });
    }

    private static ProtocolReply UnsupportedRecordConversion(string sourceFormat, string targetFormat) => new(
        1404,
        Message: $"Conversion from {sourceFormat} to {targetFormat} is not supported; built-in conversion supports SILK to WAV");

    private async Task<Asset?> FindCachedStandardMediaAsync(string reference, CancellationToken cancellationToken)
    {
        var media = _media!;
        var id = reference;
        if (!media.Assets.Exists(id))
        {
            // V11 currently emits the exact cache path as its segment file field.
            // Resolve only paths naming an existing content-addressed cache entry.
            if (!Path.IsPathFullyQualified(reference) || reference.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            id = Path.GetFileName(reference);
            if (!media.Assets.Exists(id)) return null;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(reference).Equals(media.GetPath(id), comparison)) return null;
        }
        id = id.ToLowerInvariant();
        return await _store.GetAssetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? new Asset(id, id, Source: AssetSource.Inline);
    }

    private static string? DetectNativeRecordFormat(ReadOnlySpan<byte> data, long fileLength)
    {
        if (IsWaveContainer(data, fileLength)) return "wav";
        if ((data.Length > 6 && data[..6].SequenceEqual("#!AMR\n"u8))
            || (data.Length > 9 && data[..9].SequenceEqual("#!AMR-WB\n"u8))) return "amr";
        if (data.Length > 42 && data[..4].SequenceEqual("fLaC"u8)
            && (data[4] & 0x7f) == 0 && data[5] == 0 && data[6] == 0 && data[7] == 34) return "flac";
        if (data.Length >= 27 && data[..4].SequenceEqual("OggS"u8))
        {
            var packetOffset = 27 + data[26];
            if (data.Length >= packetOffset + 8)
            {
                var packet = data[packetOffset..];
                if (packet[..8].SequenceEqual("Speex   "u8)) return "spx";
                if (packet[..8].SequenceEqual("OpusHead"u8) || packet[..7].SequenceEqual("\u0001vorbis"u8)) return "ogg";
            }
        }
        var frameOffset = 0;
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8))
        {
            if ((data[6] | data[7] | data[8] | data[9]) >= 128) return null;
            frameOffset = 10 + (data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9]);
            if (data[3] == 4 && (data[5] & 0x10) != 0) frameOffset += 10;
        }
        if (data.Length >= frameOffset + 4)
        {
            var frame = BinaryPrimitives.ReadUInt32BigEndian(data[frameOffset..]);
            if ((frame & 0xffe00000) == 0xffe00000 && ((frame >> 19) & 3) != 1
                && ((frame >> 17) & 3) == 1 && ((frame >> 12) & 15) is > 0 and < 15
                && ((frame >> 10) & 3) != 3) return "mp3";
        }
        return null;
    }

    private static bool IsWaveContainer(ReadOnlySpan<byte> data, long fileLength)
    {
        if (data.Length < 44 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        var end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (end > fileLength || end < 44) return false;
        var hasFormat = false;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var chunkEnd = offset + 8L + size;
            if (chunkEnd > end) return false;
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16 || offset + 24 > data.Length) return false;
                hasFormat = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 8)..]) > 0
                    && BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 10)..]) > 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]) > 0;
            }
            else if (data.Slice(offset, 4).SequenceEqual("data"u8)) return hasFormat;
            var next = chunkEnd + (size & 1);
            if (next > data.Length) return false;
            offset = checked((int)next);
        }
        return false;
    }

    private static bool IsUnsupportedRecordContainer(ReadOnlySpan<byte> data)
    {
        // ISO BMFF and ASF need an audio-track/codec parser before they can be
        // returned as m4a/wma. A container signature alone does not prove that.
        ReadOnlySpan<byte> asf = [0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c];
        return (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
            || (data.Length >= asf.Length && data[..asf.Length].SequenceEqual(asf));
    }
}
