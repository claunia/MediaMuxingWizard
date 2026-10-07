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
        _ => null,
    };

    /// <summary>True for the codecs whose configuration is their sample entry.</summary>
    public static bool IsEntryCodec(CodecType codec) =>
        codec is CodecType.Vc1 or CodecType.H263 or CodecType.Dirac or CodecType.Dnxhd or CodecType.AmrNb or CodecType.AmrWb;

    /// <summary>The type of a sample entry (size, type, fields, boxes); null when it is too short.</summary>
    public static string? EntryType(ReadOnlySpan<byte> entry) => entry.Length >= 8 ? Encoding.ASCII.GetString(entry.Slice(4, 4)) : null;
}
