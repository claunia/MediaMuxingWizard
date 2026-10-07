using System.Buffers.Binary;
using System.Text;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// Video for Windows / Audio Compression Manager descriptions, as AVI and ASF carry codecs and as Matroska stores them in
/// its compatibility modes (V_MS/VFW/FOURCC with a BITMAPINFOHEADER, A_MS/ACM with a WAVEFORMATEX), the way mkvmerge
/// writes them.
/// </summary>
public static class Vfw
{
    /// <summary>
    /// A BITMAPINFOHEADER as mkvmerge writes it (biSize 40 even with extra data, 1 plane, 24 bits, the image size of a
    /// 24-bit frame) followed by the codec's extra data.
    /// </summary>
    public static byte[] BitmapInfoHeader(int width, int height, string fourCc, ReadOnlySpan<byte> extra = default)
    {
        var b = new byte[40 + extra.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(14), 24);
        Encoding.ASCII.GetBytes(fourCc.PadRight(4).AsSpan(0, 4), b.AsSpan(16, 4));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), width * height * 3);
        extra.CopyTo(b.AsSpan(40));
        return b;
    }

    /// <summary>Width, height, FourCC and extra data of a BITMAPINFOHEADER; null when it is too short.</summary>
    public static (int Width, int Height, string FourCc, byte[] Extra)? ParseBitmapInfoHeader(ReadOnlySpan<byte> bih)
    {
        if (bih.Length < 40)
            return null;
        var size = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(bih), 40, bih.Length);
        return (BinaryPrimitives.ReadInt32LittleEndian(bih[4..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bih[8..])),
            Encoding.ASCII.GetString(bih.Slice(16, 4)), bih[size..].ToArray());
    }

    /// <summary>A WAVEFORMATEX (18 bytes, cbSize = the extra data's length) followed by the codec's extra data.</summary>
    public static byte[] WaveFormatEx(int tag, int channels, int sampleRate, long bitRate, int blockAlign, int bitsPerSample, ReadOnlySpan<byte> extra = default)
    {
        var b = new byte[18 + extra.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)Math.Max(0, bitRate / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)bitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), (ushort)extra.Length);
        extra.CopyTo(b.AsSpan(18));
        return b;
    }

    /// <summary>The fields of a WAVEFORMATEX (and its extra data); null when it is too short.</summary>
    public static (int Tag, int Channels, int SampleRate, long BitRate, int BlockAlign, int BitsPerSample, byte[] Extra)? ParseWaveFormatEx(ReadOnlySpan<byte> wfx)
    {
        if (wfx.Length < 16)
            return null;
        var extra = wfx.Length >= 18 ? wfx.Slice(18, Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(wfx[16..]), wfx.Length - 18)).ToArray() : [];
        return (BinaryPrimitives.ReadUInt16LittleEndian(wfx), BinaryPrimitives.ReadUInt16LittleEndian(wfx[2..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(wfx[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wfx[8..]) * 8L, BinaryPrimitives.ReadUInt16LittleEndian(wfx[12..]), BinaryPrimitives.ReadUInt16LittleEndian(wfx[14..]), extra);
    }

    /// <summary>A FourCC as a display name ("MS-MPEG4 v3", "WMV 9", "VC-1", "DV" …); the FourCC itself when unknown.</summary>
    public static string VideoName(string fourCc) => fourCc.ToUpperInvariant() switch
    {
        "MPG4" or "MP41" => "MS-MPEG4 v1",
        "MP42" or "DIV2" => "MS-MPEG4 v2",
        "MP43" or "DIV3" or "DIV4" or "DIV5" or "DIV6" or "AP41" or "COL1" => "MS-MPEG4 v3",
        "WMV1" => "WMV 7",
        "WMV2" => "WMV 8",
        "WMV3" => "WMV 9",
        "WMVA" or "WVC1" => "VC-1",
        "DVSD" or "DV25" or "DVSL" or "DVHD" or "DV50" or "DVH1" or "CDVC" or "DVCP" or "DVPP" => "DV",
        "DIVX" or "XVID" or "DX50" or "FMP4" or "MP4V" or "M4S2" or "3IV2" => "MPEG-4 Visual",
        "CVID" => "Cinepak",
        "IV31" or "IV32" => "Indeo 3",
        "IV41" => "Indeo 4",
        "IV50" => "Indeo 5",
        "H263" or "S263" or "U263" => "H.263",
        "MJPG" or "AVRN" or "LJPG" or "JPGL" => "Motion JPEG",
        "MSVC" or "CRAM" or "WHAM" => "Microsoft Video 1",
        "VP30" or "VP31" => "VP3",
        "VP50" => "VP5",
        "VP60" or "VP61" or "VP62" or "VP6F" => "VP6",
        "FLV1" => "Sorenson Spark",
        "SVQ1" => "Sorenson Video",
        "HFYU" or "FFVH" => "HuffYUV",
        "FFV1" => "FFV1",
        "UYVY" or "YUY2" or "YV12" or "I420" or "\0\0\0\0" or "RAW " => "Uncompressed",
        _ => fourCc.Trim(),
    };

    /// <summary>A WAVEFORMATEX format tag as a display name ("WMA 2", "IMA ADPCM" …).</summary>
    public static string AudioName(int tag) => tag switch
    {
        0x0002 => "MS ADPCM",
        0x0006 => "A-law",
        0x0007 => "µ-law",
        0x0011 => "IMA ADPCM",
        0x0031 => "GSM 6.10",
        0x0160 => "WMA 1",
        0x0161 => "WMA 2",
        0x0162 => "WMA Pro",
        0x0163 => "WMA Lossless",
        0x000A => "WMA Voice",
        0x0130 => "ACELP.net",
        0x0270 => "ATRAC3",
        0x2004 => "RealAudio Cook",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ACM 0x{tag:X4}"),
    };

    /// <summary>The FourCC (RV10…RV40) of RealMedia 'VIDO' type-specific data; null when it is not one.</summary>
    public static string? RealVideoFourCc(ReadOnlySpan<byte> vido) =>
        vido.Length >= 26 && vido.Slice(4, 4).SequenceEqual("VIDO"u8) ? Encoding.ASCII.GetString(vido.Slice(8, 4)) : null;

    /// <summary>Width, height and the decoder's extra data (what follows the fixed fields) of RealMedia 'VIDO' data.</summary>
    public static (int Width, int Height, byte[] Extra)? ParseRealVideo(ReadOnlySpan<byte> vido) =>
        RealVideoFourCc(vido) is null ? null : (BinaryPrimitives.ReadUInt16BigEndian(vido[12..]), BinaryPrimitives.ReadUInt16BigEndian(vido[14..]), vido[26..].ToArray());

    /// <summary>
    /// RealMedia 'VIDO' type-specific data (size, "VIDO", FourCC, width, height, bit count, 0, 16.16 frame rate, the
    /// decoder's extra data), for streams whose original is not at hand.
    /// </summary>
    public static byte[] RealVideo(string fourCc, int width, int height, double frameRate, ReadOnlySpan<byte> extra)
    {
        var b = new byte[26 + extra.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        "VIDO"u8.CopyTo(b.AsSpan(4));
        Encoding.ASCII.GetBytes(fourCc.PadRight(4).AsSpan(0, 4), b.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16), 12);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(22), (uint)Math.Round(frameRate * 65536));
        extra.CopyTo(b.AsSpan(26));
        return b;
    }

    /// <summary>A RealVideo FourCC as a display name.</summary>
    public static string RealVideoName(string fourCc) => fourCc switch
    {
        "RV10" => "RealVideo 1",
        "RV20" => "RealVideo G2",
        "RV30" => "RealVideo 8",
        "RV40" => "RealVideo 9",
        "RV60" => "RealVideo 11",
        _ => "RealVideo",
    };
}
