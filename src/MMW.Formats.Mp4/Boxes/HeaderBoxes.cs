using System.Buffers.Binary;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>Accessors for fields of <c>tkhd</c>, <c>mdhd</c>, <c>mvhd</c> and <c>hdlr</c> payloads.</summary>
public static class HeaderBoxes
{
    public const int TrackEnabled = 1;
    public const int TrackInMovie = 2;
    public const int TrackInPreview = 4;

    // ----- tkhd -----

    public static int TkhdFlags(Box tkhd) => (tkhd.Payload[1] << 16) | (tkhd.Payload[2] << 8) | tkhd.Payload[3];

    public static void SetTkhdFlags(Box tkhd, int flags)
    {
        tkhd.Payload[1] = (byte)(flags >> 16);
        tkhd.Payload[2] = (byte)(flags >> 8);
        tkhd.Payload[3] = (byte)flags;
    }

    private static int TkhdBase(Box tkhd) => tkhd.Payload[0] == 1 ? 12 : 0;

    public static uint TkhdTrackId(Box tkhd) => BinaryPrimitives.ReadUInt32BigEndian(tkhd.Payload.AsSpan(12 + (tkhd.Payload[0] == 1 ? 8 : 0)));

    public static void SetTkhdTrackId(Box tkhd, uint id) =>
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.Payload.AsSpan(12 + (tkhd.Payload[0] == 1 ? 8 : 0)), id);

    public static short TkhdAlternateGroup(Box tkhd) => BinaryPrimitives.ReadInt16BigEndian(tkhd.Payload.AsSpan(34 + TkhdBase(tkhd)));

    public static void SetTkhdAlternateGroup(Box tkhd, short group) =>
        BinaryPrimitives.WriteInt16BigEndian(tkhd.Payload.AsSpan(34 + TkhdBase(tkhd)), group);

    /// <summary>Volume as a linear factor (8.8 fixed point in the file).</summary>
    public static double TkhdVolume(Box tkhd) => BinaryPrimitives.ReadInt16BigEndian(tkhd.Payload.AsSpan(36 + TkhdBase(tkhd))) / 256.0;

    public static void SetTkhdVolume(Box tkhd, double volume) =>
        BinaryPrimitives.WriteInt16BigEndian(tkhd.Payload.AsSpan(36 + TkhdBase(tkhd)), (short)Math.Clamp(Math.Round(volume * 256), short.MinValue, short.MaxValue));

    public static (double Width, double Height) TkhdSize(Box tkhd)
    {
        var b = TkhdBase(tkhd);
        return (BinaryPrimitives.ReadUInt32BigEndian(tkhd.Payload.AsSpan(76 + b)) / 65536.0,
            BinaryPrimitives.ReadUInt32BigEndian(tkhd.Payload.AsSpan(80 + b)) / 65536.0);
    }

    public static void SetTkhdSize(Box tkhd, double width, double height)
    {
        var b = TkhdBase(tkhd);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.Payload.AsSpan(76 + b), (uint)Math.Round(width * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.Payload.AsSpan(80 + b), (uint)Math.Round(height * 65536));
    }

    // ----- mdhd -----

    public static uint MdhdTimescale(Box mdhd) => BinaryPrimitives.ReadUInt32BigEndian(mdhd.Payload.AsSpan(mdhd.Payload[0] == 1 ? 20 : 12));

    public static ulong MdhdDuration(Box mdhd) => mdhd.Payload[0] == 1
        ? BinaryPrimitives.ReadUInt64BigEndian(mdhd.Payload.AsSpan(24))
        : BinaryPrimitives.ReadUInt32BigEndian(mdhd.Payload.AsSpan(16));

    public static ushort MdhdLanguage(Box mdhd) => BinaryPrimitives.ReadUInt16BigEndian(mdhd.Payload.AsSpan(mdhd.Payload[0] == 1 ? 32 : 20));

    public static void SetMdhdLanguage(Box mdhd, ushort packed) =>
        BinaryPrimitives.WriteUInt16BigEndian(mdhd.Payload.AsSpan(mdhd.Payload[0] == 1 ? 32 : 20), packed);

    // ----- mvhd -----

    public static uint MvhdTimescale(Box mvhd) => BinaryPrimitives.ReadUInt32BigEndian(mvhd.Payload.AsSpan(mvhd.Payload[0] == 1 ? 20 : 12));

    public static ulong MvhdDuration(Box mvhd) => mvhd.Payload[0] == 1
        ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.Payload.AsSpan(24))
        : BinaryPrimitives.ReadUInt32BigEndian(mvhd.Payload.AsSpan(16));

    public static uint MvhdNextTrackId(Box mvhd) => BinaryPrimitives.ReadUInt32BigEndian(mvhd.Payload.AsSpan(mvhd.Payload.Length - 4));

    public static void SetMvhdNextTrackId(Box mvhd, uint id) => BinaryPrimitives.WriteUInt32BigEndian(mvhd.Payload.AsSpan(mvhd.Payload.Length - 4), id);

    // ----- hdlr -----

    public static string HdlrType(Box hdlr) => hdlr.Payload.Length >= 12 ? Box.Latin1.GetString(hdlr.Payload, 8, 4) : string.Empty;

    // ----- language -----

    /// <summary>Packed ISO 639-2/T code from <c>mdhd</c>, or a Macintosh language code (QuickTime).</summary>
    public static string UnpackLanguage(ushort packed)
    {
        if (packed == 0x7FFF || packed == 0)
            return "und";
        if (packed < 0x400)
            return MacLanguage(packed);

        Span<char> c = stackalloc char[3];
        c[0] = (char)(((packed >> 10) & 0x1F) + 0x60);
        c[1] = (char)(((packed >> 5) & 0x1F) + 0x60);
        c[2] = (char)((packed & 0x1F) + 0x60);
        foreach (var ch in c)
        {
            if (ch is < 'a' or > 'z')
                return "und";
        }

        return new string(c);
    }

    public static ushort PackLanguage(string iso639_2T)
    {
        if (iso639_2T.Length != 3)
            iso639_2T = "und";
        iso639_2T = iso639_2T.ToLowerInvariant();
        return (ushort)(((iso639_2T[0] - 0x60) << 10) | ((iso639_2T[1] - 0x60) << 5) | (iso639_2T[2] - 0x60));
    }

    private static string MacLanguage(ushort code) => code switch
    {
        0 => "eng", 1 => "fra", 2 => "deu", 3 => "ita", 4 => "nld", 5 => "swe", 6 => "spa", 7 => "dan", 8 => "por",
        9 => "nor", 10 => "heb", 11 => "jpn", 12 => "ara", 13 => "fin", 14 => "ell", 15 => "isl", 16 => "mlt",
        17 => "tur", 18 => "hrv", 19 => "zho", 20 => "urd", 21 => "hin", 22 => "tha", 23 => "kor", 24 => "lit",
        25 => "pol", 26 => "hun", 27 => "est", 28 => "lav", 30 => "fao", 31 => "fas", 32 => "rus", 33 => "zho",
        34 => "nld", 35 => "gle", 36 => "sqi", 37 => "ron", 38 => "ces", 39 => "slk", 40 => "slv", 41 => "yid",
        42 => "srp", 43 => "mkd", 44 => "bul", 45 => "ukr", 46 => "bel", 47 => "uzb", 48 => "kaz",
        _ => "und",
    };
}
