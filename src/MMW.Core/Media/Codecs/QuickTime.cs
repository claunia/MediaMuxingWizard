using System.Buffers.Binary;
using System.Text;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// Codecs stored by their QuickTime / ISO sample description (stsd entry), in MP4 and, as mkvmerge does, in Matroska's
/// QuickTime compatibility mode (V_QUICKTIME / A_QUICKTIME, the whole entry as CodecPrivate).
/// </summary>
public static class QuickTime
{
    /// <summary>The codec of a sample entry type this application describes; null for others.</summary>
    public static CodecType? CodecFor(string entryType) => entryType switch
    {
        "vc-1" => CodecType.Vc1,
        "s263" or "h263" or "H263" => CodecType.H263,
        "drac" => CodecType.Dirac,
        "AVdn" or "AVdh" => CodecType.Dnxhd,
        "samr" => CodecType.AmrNb,
        "sawb" => CodecType.AmrWb,
        "ac-4" => CodecType.Ac4,
        "mhm1" or "mhm2" or "mha1" or "mha2" => CodecType.MpegH,
        _ => null,
    };

    /// <summary>True for the codecs whose configuration is their sample entry.</summary>
    public static bool IsEntryCodec(CodecType codec) =>
        codec is CodecType.Vc1 or CodecType.H263 or CodecType.Dirac or CodecType.Dnxhd or CodecType.AmrNb or CodecType.AmrWb or CodecType.Ac4 or
            CodecType.MpegH;

    /// <summary>
    /// An ISO AudioSampleEntry (version 0: channel count, 16-bit sample size, 16.16 sample rate) of <paramref name="type"/>
    /// holding the given boxes (each with its header), for codecs whose source has no sample entry (Matroska A_AC4,
    /// MPEG-TS, raw streams).
    /// </summary>
    public static byte[] AudioEntry(string type, int channels, int sampleRate, params byte[][] boxes)
    {
        var length = 36 + boxes.Sum(b => b.Length);
        var e = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(e, length);
        Encoding.ASCII.GetBytes(type.AsSpan(0, 4), e.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(14), 1); // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(24), (ushort)channels);
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(26), 16);
        BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(32), sampleRate <= ushort.MaxValue ? (uint)sampleRate << 16 : 0);
        var at = 36;
        foreach (var box in boxes)
        {
            box.CopyTo(e, at);
            at += box.Length;
        }

        return e;
    }

    /// <summary>A box (size, type, payload).</summary>
    public static byte[] Box(string type, ReadOnlySpan<byte> payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(b, b.Length);
        Encoding.ASCII.GetBytes(type.AsSpan(0, 4), b.AsSpan(4, 4));
        payload.CopyTo(b.AsSpan(8));
        return b;
    }

    /// <summary>The payload of the first box of <paramref name="type"/> directly inside a sample entry, or null.</summary>
    public static byte[]? EntryBox(ReadOnlySpan<byte> entry, string type)
    {
        var kind = EntryType(entry);
        var start = kind is null ? -1 : FirstBoxOffset(entry, kind);
        for (var at = start; at >= 0 && at + 8 <= entry.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[at..]);
            if (size < 8 || at + size > entry.Length)
                break;
            if (Encoding.ASCII.GetString(entry.Slice(at + 4, 4)) == type)
                return entry.Slice(at + 8, size - 8).ToArray();
            at += size;
        }

        return null;
    }

    /// <summary>Where the boxes of a sample entry start: after the visual (86) or audio (36, or 52/72 for QuickTime v1/v2) fields.</summary>
    private static int FirstBoxOffset(ReadOnlySpan<byte> entry, string type)
    {
        if (CodecFor(type) is CodecType.Vc1 or CodecType.H263 or CodecType.Dirac or CodecType.Dnxhd)
            return 86;
        if (entry.Length < 36)
            return -1;
        return BinaryPrimitives.ReadUInt16BigEndian(entry[16..]) switch
        {
            1 when entry.Length >= 52 && !LooksLikeBox(entry, 36) => 52,
            2 => 72,
            _ => 36,
        };
    }

    private static bool LooksLikeBox(ReadOnlySpan<byte> entry, int at) =>
        at + 8 <= entry.Length && BinaryPrimitives.ReadUInt32BigEndian(entry[at..]) is >= 8 and var size && at + size <= entry.Length &&
        entry.Slice(at + 4, 4).ToArray().All(c => c is >= 0x20 and < 0x7F);

    /// <summary>The type of a sample entry (size, type, fields, boxes); null when it is too short.</summary>
    public static string? EntryType(ReadOnlySpan<byte> entry) => entry.Length >= 8 ? Encoding.ASCII.GetString(entry.Slice(4, 4)) : null;
}
