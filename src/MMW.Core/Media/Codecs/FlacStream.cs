using System.Buffers.Binary;
using System.Text;

namespace MMW.Core.Media.Codecs;

/// <summary>A FLAC frame header (RFC 9639 §9.1), validated by its CRC-8.</summary>
/// <param name="Samples">Block size of the frame in samples.</param>
/// <param name="Number">Frame number (fixed block size) or first sample number (variable block size).</param>
/// <param name="VariableBlockSize">The stream uses variable block sizes (the number counts samples).</param>
/// <param name="HeaderLength">Bytes of the header, CRC-8 included.</param>
public sealed record FlacFrameHeader(int Samples, long Number, bool VariableBlockSize, int HeaderLength);

/// <summary>The STREAMINFO metadata block (RFC 9639 §8.2).</summary>
public sealed record FlacStreamInfo(int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize, int SampleRate, int Channels,
    int BitsPerSample, long TotalSamples, byte[] Md5)
{
    /// <summary>True when the encoder stored the MD5 of the decoded audio (all zeros means unknown).</summary>
    public bool HasMd5 => Md5.Any(b => b != 0);
}

public static partial class Flac
{
    public const int StreamInfoType = 0;
    public const int PaddingType = 1;
    public const int VorbisCommentType = 4;
    public const int PictureType = 6;

    /// <summary>Longest possible frame header (sync to CRC-8).</summary>
    public const int MaxHeaderLength = 16;

    private static readonly byte[] s_crc8 = BuildCrc8();
    private static readonly ushort[] s_crc16 = BuildCrc16();

    /// <summary>The metadata blocks (type, offset of the body, body length) of <paramref name="blocks"/>.</summary>
    public static IEnumerable<(int Type, int Offset, int Length)> EnumerateBlocks(byte[] blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var pos = 0;
        while (pos + 4 <= blocks.Length)
        {
            var type = blocks[pos] & 0x7F;
            var length = (blocks[pos + 1] << 16) | (blocks[pos + 2] << 8) | blocks[pos + 3];
            if (pos + 4 + length > blocks.Length)
                yield break;
            yield return (type, pos + 4, length);
            if ((blocks[pos] & 0x80) != 0)
                yield break;
            pos += 4 + length;
        }
    }

    /// <summary>STREAMINFO from the metadata blocks (STREAMINFO first), or null.</summary>
    public static FlacStreamInfo? ParseStreamInfo(ReadOnlySpan<byte> blocks)
    {
        if (blocks.Length < 4 + 34 || (blocks[0] & 0x7F) != StreamInfoType)
            return null;
        var s = blocks.Slice(4, 34);
        var (rate, channels, bits) = Describe(blocks);
        var total = ((long)(s[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(s[14..]);
        return new FlacStreamInfo(BinaryPrimitives.ReadUInt16BigEndian(s), BinaryPrimitives.ReadUInt16BigEndian(s[2..]),
            (s[4] << 16) | (s[5] << 8) | s[6], (s[7] << 16) | (s[8] << 8) | s[9], rate, channels, bits, total, s.Slice(18, 16).ToArray());
    }

    /// <summary>The encoder's vendor string from the VORBIS_COMMENT block, or null.</summary>
    public static string? Vendor(byte[] blocks)
    {
        foreach (var (type, offset, length) in EnumerateBlocks(blocks))
        {
            if (type != VorbisCommentType || length < 4)
                continue;
            var size = BinaryPrimitives.ReadInt32LittleEndian(blocks.AsSpan(offset));
            return size >= 0 && size <= length - 4 ? Encoding.UTF8.GetString(blocks, offset + 4, size) : null;
        }

        return null;
    }

    /// <summary>
    /// What STREAMINFO and the vendor string say about a stream: bit depth, block size and encoder, e.g.
    /// "24-bit, 4096-sample blocks, libFLAC 1.4.3".
    /// </summary>
    public static string DescribeStream(byte[]? blocks)
    {
        if (blocks is null || ParseStreamInfo(blocks) is not { } info)
            return string.Empty;
        var parts = new List<string> { $"{info.BitsPerSample}-bit" };
        parts.Add(info.MinBlockSize == info.MaxBlockSize ? $"{info.MaxBlockSize}-sample blocks" : $"{info.MinBlockSize}–{info.MaxBlockSize}-sample blocks");
        if (EncoderName(Vendor(blocks)) is { Length: > 0 } encoder)
            parts.Add(encoder);
        if (!info.HasMd5)
            parts.Add("no MD5");
        return string.Join(", ", parts);
    }

    /// <summary>"reference libFLAC 1.4.3 20230623" → "libFLAC 1.4.3"; "Lavf61.7.100" stays.</summary>
    public static string EncoderName(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return string.Empty;
        var name = vendor.Trim();
        if (name.StartsWith("reference ", StringComparison.Ordinal))
            name = name["reference ".Length..];
        var space = name.LastIndexOf(' ');
        if (space > 0 && name.Length - space - 1 == 8 && name[(space + 1)..].All(char.IsAsciiDigit))
            name = name[..space]; // build date
        return name;
    }

    /// <summary>The frame header at the start of <paramref name="data"/>, or null when it is not a valid one.</summary>
    /// <param name="data">Bytes from a possible frame start (at least <see cref="MaxHeaderLength"/> unless the stream ends).</param>
    /// <param name="info">STREAMINFO, for the block size of frames that refer to it; may be null.</param>
    public static FlacFrameHeader? ParseFrameHeader(ReadOnlySpan<byte> data, FlacStreamInfo? info = null)
    {
        if (data.Length < 6 || data[0] != 0xFF || (data[1] & 0xFE) != 0xF8)
            return null;
        var variable = (data[1] & 1) != 0;
        var sizeCode = data[2] >> 4;
        var rateCode = data[2] & 0x0F;
        var channelCode = data[3] >> 4;
        var depthCode = (data[3] >> 1) & 7;
        if (sizeCode == 0 || rateCode == 15 || channelCode > 10 || depthCode == 3 || (data[3] & 1) != 0)
            return null;

        // UTF-8-like coded number: up to 36 bits in 7 bytes.
        var pos = 4;
        var first = data[pos++];
        int extra;
        long number;
        switch (first)
        {
            case < 0x80:
                extra = 0;
                number = first;
                break;
            case >= 0xC0 and < 0xE0:
                extra = 1;
                number = first & 0x1F;
                break;
            case >= 0xE0 and < 0xF0:
                extra = 2;
                number = first & 0x0F;
                break;
            case >= 0xF0 and < 0xF8:
                extra = 3;
                number = first & 0x07;
                break;
            case >= 0xF8 and < 0xFC:
                extra = 4;
                number = first & 0x03;
                break;
            case >= 0xFC and < 0xFE:
                extra = 5;
                number = first & 0x01;
                break;
            case 0xFE:
                extra = 6;
                number = 0;
                break;
            default:
                return null;
        }

        if (!variable && extra > 5)
            return null; // frame numbers have at most 31 bits
        if (pos + extra > data.Length)
            return null;
        for (var i = 0; i < extra; i++)
        {
            var b = data[pos++];
            if ((b & 0xC0) != 0x80)
                return null;
            number = (number << 6) | (uint)(b & 0x3F);
        }

        int samples;
        switch (sizeCode)
        {
            case 1:
                samples = 192;
                break;
            case >= 2 and <= 5:
                samples = 576 << (sizeCode - 2);
                break;
            case 6:
                if (pos + 1 > data.Length)
                    return null;
                samples = data[pos++] + 1;
                break;
            case 7:
                if (pos + 2 > data.Length)
                    return null;
                samples = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) + 1;
                pos += 2;
                break;
            default:
                samples = 256 << (sizeCode - 8);
                break;
        }

        pos += rateCode switch
        {
            12 => 1,
            13 or 14 => 2,
            _ => 0,
        };
        if (pos + 1 > data.Length || Crc8(data[..pos]) != data[pos])
            return null;
        if (info is not null && (channelCode switch { <= 7 => channelCode + 1, _ => 2 }) != info.Channels)
            return null;
        return new FlacFrameHeader(samples, number, variable, pos + 1);
    }

    /// <summary>CRC-8 (polynomial 0x07) of a frame header.</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
            crc = s_crc8[crc ^ b];
        return crc;
    }

    /// <summary>CRC-16 (polynomial 0x8005) of a frame; 0 over a whole frame including its stored CRC.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
            crc = (ushort)((crc << 8) ^ s_crc16[(crc >> 8) ^ b]);
        return crc;
    }

    private static byte[] BuildCrc8()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
            table[i] = (byte)c;
        }

        return table;
    }

    private static ushort[] BuildCrc16()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i << 8;
            for (var k = 0; k < 8; k++)
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            table[i] = (ushort)c;
        }

        return table;
    }
}
