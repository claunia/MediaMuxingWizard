using System.Text;
using System.Buffers.Binary;
using System.Globalization;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Matroska;

/// <summary>Codec-related helpers: display names for CodecIDs and decoding of codec configuration records.</summary>
internal static class MatroskaCodecs
{
    private static readonly (string Prefix, string Name)[] s_formats =
    [
        ("V_MPEG4/ISO/AVC", "H.264"),
        ("V_MPEGH/ISO/HEVC", "HEVC"),
        ("V_MPEGI/ISO/VVC", "VVC"),
        ("V_AV1", "AV1"),
        ("V_VP9", "VP9"),
        ("V_VP8", "VP8"),
        ("V_MPEG4/ISO/", "MPEG-4 Visual"),
        ("V_MPEG4/MS/V3", "MS MPEG-4 v3"),
        ("V_MPEG2", "MPEG-2"),
        ("V_AVS2", "AVS2"),
        ("V_AVS3", "AVS3"),
        ("V_MPEG1", "MPEG-1"),
        ("V_THEORA", "Theora"),
        ("V_PRORES", "ProRes"),
        ("V_FFV1", "FFV1"),
        ("V_MS/VFW/FOURCC", "VfW"),
        ("V_UNCOMPRESSED", "Uncompressed"),
        ("V_REAL/", "RealVideo"),
        ("A_AAC", "AAC"),
        ("A_AC3", "AC-3"),
        ("A_EAC3", "E-AC-3"),
        ("A_AC4", "AC-4"),
        ("A_DTS", "DTS"),
        ("A_TRUEHD", "TrueHD"),
        ("A_MLP", "MLP"),
        ("A_OPUS", "Opus"),
        ("A_VORBIS", "Vorbis"),
        ("A_FLAC", "FLAC"),
        ("A_ALAC", "ALAC"),
        ("A_MPEG/L3", "MP3"),
        ("A_MPEG/L2", "MP2"),
        ("A_MPEG/L1", "MP1"),
        ("A_PCM/FLOAT", "PCM (float)"),
        ("A_PCM/", "PCM"),
        ("A_WAVPACK4", "WavPack"),
        ("A_TTA1", "TTA"),
        ("A_MS/ACM", "ACM"),
        ("A_REAL/", "RealAudio"),
        ("S_TEXT/UTF8", "SRT"),
        ("S_TEXT/ASCII", "SRT"),
        ("S_TEXT/ASS", "ASS"),
        ("S_TEXT/SSA", "SSA"),
        ("S_ASS", "ASS"),
        ("S_SSA", "SSA"),
        ("S_TEXT/WEBVTT", "WebVTT"),
        ("D_WEBVTT/", "WebVTT"),
        ("S_TEXT/USF", "USF"),
        ("S_HDMV/PGS", "PGS"),
        ("S_HDMV/TEXTST", "TextST"),
        ("S_VOBSUB", "VobSub"),
        ("S_DVBSUB", "DVB"),
        ("S_ARIBSUB", "ARIB"),
        ("S_KATE", "Kate"),
        ("S_IMAGE/BMP", "Bitmap"),
    ];

    /// <summary>Short display name for a Matroska CodecID.</summary>
    public static string FormatName(string codecId)
    {
        foreach (var (prefix, name) in s_formats)
        {
            if (codecId.StartsWith(prefix, StringComparison.Ordinal))
                return name;
        }

        return codecId;
    }

    /// <summary>The compression FourCC of a V_MS/VFW/FOURCC CodecPrivate (BITMAPINFOHEADER biCompression), or null.</summary>
    public static string? VfwFourCc(ReadOnlySpan<byte> bitmapInfoHeader) =>
        bitmapInfoHeader.Length >= 20 ? Encoding.ASCII.GetString(bitmapInfoHeader.Slice(16, 4)) : null;

    /// <summary>A 40-byte BITMAPINFOHEADER (24 bits per pixel) for a V_MS/VFW/FOURCC track.</summary>
    public static byte[] BitmapInfoHeader(int width, int height, string fourCc)
    {
        var b = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(b, 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(14), 24);
        Encoding.ASCII.GetBytes(fourCc, b.AsSpan(16, 4));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), width * height * 3);
        return b;
    }

    /// <summary>Decodes "Profile@Level" from an avcC record (AVC CodecPrivate).</summary>
    public static string AvcProfileLevel(ReadOnlySpan<byte> avcC) => H264.ProfileLevel(avcC);

    /// <summary>Decodes "Profile@Level" from an hvcC record (HEVC CodecPrivate).</summary>
    public static string HevcProfileLevel(ReadOnlySpan<byte> hvcC) => Hevc.ProfileLevel(hvcC);

    /// <summary>Decodes a Dolby Vision decoder configuration record ('dvcC'/'dvvC').</summary>
    public static DolbyVisionInfo? DolbyVision(ReadOnlySpan<byte> record)
    {
        if (record.Length < 5)
            return null;

        var bits = (record[2] << 8) | record[3];
        return new DolbyVisionInfo(
            VersionMajor: record[0],
            VersionMinor: record[1],
            Profile: bits >> 9,
            Level: (bits >> 3) & 0x3F,
            RpuPresent: (bits & 0x4) != 0,
            ElPresent: (bits & 0x2) != 0,
            BlPresent: (bits & 0x1) != 0,
            BlSignalCompatibilityId: record[4] >> 4);
    }

    /// <summary>Common name of a channel count ("Stereo", "5.1", ...).</summary>
    public static string ChannelLayout(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        3 => "2.1",
        4 => "Quadraphonic",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => string.Empty,
    };
}
