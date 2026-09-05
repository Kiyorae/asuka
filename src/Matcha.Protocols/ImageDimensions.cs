using System.Buffers.Binary;

namespace Matcha.Protocols;

/// <summary>
/// Container-header dimension reader for protocol payloads. It never decompresses
/// image data and consumes at most the bounded header supplied by <see cref="MediaService"/>.
/// </summary>
internal static class ImageDimensions
{
    public static bool TryRead(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        return TryReadPng(data, out width, out height)
            || TryReadJpeg(data, out width, out height)
            || TryReadGif(data, out width, out height)
            || TryReadWebP(data, out width, out height)
            || TryReadBmp(data, out width, out height);
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 24 || !data[..8].SequenceEqual(signature)
            || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        return IsValid(
            BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4)),
            out width,
            out height);
    }

    private static bool TryReadGif(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 10
            || !(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }

        return IsValid(
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)),
            out width,
            out height);
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 26 || !data[..2].SequenceEqual("BM"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4)) < 40)
        {
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));
        if (rawHeight == int.MinValue)
        {
            return false;
        }

        return IsValid(rawWidth, Math.Abs(rawHeight), out width, out height);
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 3 < data.Length)
        {
            while (offset < data.Length && data[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                return false;
            }

            var marker = data[offset++];
            if (marker is 0x00 or 0xd8 or 0xd9 || (marker >= 0xd0 && marker <= 0xd7))
            {
                continue;
            }

            if (offset + 2 > data.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || length > data.Length - offset)
            {
                return false;
            }

            if (IsJpegStartOfFrame(marker) && length >= 7)
            {
                return IsValid(
                    BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2)),
                    out width,
                    out height);
            }

            offset += length;
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 20 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var offset = 12;
        while (offset <= data.Length - 8)
        {
            var chunk = data.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payload = offset + 8;
            if (length > data.Length - payload)
            {
                return false;
            }

            var size = checked((int)length);
            if (chunk.SequenceEqual("VP8X"u8) && size >= 10)
            {
                return IsValid(
                    1 + ReadUInt24LittleEndian(data.Slice(payload + 4, 3)),
                    1 + ReadUInt24LittleEndian(data.Slice(payload + 7, 3)),
                    out width,
                    out height);
            }

            if (chunk.SequenceEqual("VP8 "u8) && size >= 10
                && data[payload + 3] == 0x9d
                && data[payload + 4] == 0x01
                && data[payload + 5] == 0x2a)
            {
                return IsValid(
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 6, 2)) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 8, 2)) & 0x3fff,
                    out width,
                    out height);
            }

            if (chunk.SequenceEqual("VP8L"u8) && size >= 5 && data[payload] == 0x2f)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload + 1, 4));
                return IsValid(
                    (int)(bits & 0x3fff) + 1,
                    (int)((bits >> 14) & 0x3fff) + 1,
                    out width,
                    out height);
            }

            var paddedLength = (long)length + (length & 1);
            if (paddedLength > data.Length - payload)
            {
                return false;
            }

            offset = checked(payload + (int)paddedLength);
        }

        return false;
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is >= 0xc0 and <= 0xc3
        or >= 0xc5 and <= 0xc7
        or >= 0xc9 and <= 0xcb
        or >= 0xcd and <= 0xcf;

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> data) =>
        data[0] | (data[1] << 8) | (data[2] << 16);

    private static bool IsValid(int candidateWidth, int candidateHeight, out int width, out int height)
    {
        width = candidateWidth;
        height = candidateHeight;
        return candidateWidth > 0 && candidateHeight > 0;
    }
}
