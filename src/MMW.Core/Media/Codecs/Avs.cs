using System.Globalization;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>The generation of a Chinese AVS video standard.</summary>
public enum AvsGeneration
{
    /// <summary>AVS1-P2 (GB/T 20090.2) and AVS+ (GY/T 257.1).</summary>
    Avs1 = 1,

    /// <summary>AVS2-P2 (GB/T 33475.2, IEEE 1857.4).</summary>
    Avs2 = 2,

    /// <summary>AVS3-P2 (T/AI 109.2, IEEE 1857.10).</summary>
    Avs3 = 3,
}

/// <summary>A sequence header with the sequence-level extensions that follow it.</summary>
public sealed record AvsSequence
{
    public AvsGeneration Generation { get; init; }

    public int ProfileId { get; init; }

    public int LevelId { get; init; }

    public bool Progressive { get; init; } = true;

    public bool FieldCoded { get; init; }

    /// <summary>Coded size (horizontal_size × vertical_size).</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Displayed size (sequence display extension), or the coded size.</summary>
    public int DisplayWidth { get; init; }

    public int DisplayHeight { get; init; }

    /// <summary>chroma_format: 1 = 4:2:0, 2 = 4:2:2.</summary>
    public int ChromaFormat { get; init; } = 1;

    public int BitDepth { get; init; } = 8;

    public int AspectRatio { get; init; }

    public double FrameRate { get; init; }

    public bool LowDelay { get; init; }

    public bool TemporalIdEnabled { get; init; }

    /// <summary>output_reorder_delay (AVS2/AVS3, not low delay): pictures between decoding and output.</summary>
    public int OutputReorderDelay { get; init; }

    /// <summary>AVS2 background (scene) pictures enabled: picture headers carry extra flags.</summary>
    public bool BackgroundPictures { get; init; }

    /// <summary>The colour description, in ITU-T H.273 code points.</summary>
    public ColorInfo Color { get; init; } = ColorInfo.Unspecified;

    /// <summary>Mastering display and content light level (extension 1010), or null.</summary>
    public HdrInfo? Hdr { get; init; }
}

/// <summary>What a picture header tells about the picture's order.</summary>
/// <param name="Intra">An I picture (0xB3).</param>
/// <param name="OrderIndex">
/// AVS1 picture_distance (display order) or AVS2 coding_order / AVS3 decode_order_index (decoding order), modulo 256.
/// </param>
/// <param name="OutputDelay">AVS2/AVS3 picture_output_delay (0 for low-delay sequences and AVS1).</param>
public readonly record struct AvsPicture(bool Intra, int OrderIndex, int OutputDelay);

/// <summary>
/// AVS1, AVS2 and AVS3 video bitstream helpers. The three share the start code syntax (00 00 01 xx): sequence header
/// 0xB0, sequence end 0xB1, user data 0xB2, intra picture 0xB3, extension 0xB5, inter picture 0xB6, video edit 0xB7,
/// slices 0x00–0x8F; there are no emulation prevention bytes.
/// </summary>
public static class Avs
{
    public const byte SequenceHeader = 0xB0;
    public const byte SequenceEnd = 0xB1;
    public const byte UserData = 0xB2;
    public const byte IntraPicture = 0xB3;
    public const byte Extension = 0xB5;
    public const byte InterPicture = 0xB6;
    public const byte VideoEdit = 0xB7;

    private const int SequenceDisplayExtension = 0b0010;
    private const int HdrDynamicMetadataExtension = 0b0101;

    /// <summary>hdr_dynamic_metadata_type of HDR Vivid (as the DVB / UWA test streams carry it).</summary>
    private const int HdrVividMetadataType = 5;
    private const int MasteringDisplayExtension = 0b1010;

    private static readonly double[] s_frameRates = [0, 24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60, 100, 120, 200, 240, 300];

    public static CodecType Codec(AvsGeneration generation) => generation switch
    {
        AvsGeneration.Avs1 => CodecType.Avs1,
        AvsGeneration.Avs2 => CodecType.Avs2,
        _ => CodecType.Avs3,
    };

    public static AvsGeneration? Generation(CodecType codec) => codec switch
    {
        CodecType.Avs1 => AvsGeneration.Avs1,
        CodecType.Avs2 => AvsGeneration.Avs2,
        CodecType.Avs3 => AvsGeneration.Avs3,
        _ => null,
    };

    /// <summary>The start-code delimited units of <paramref name="data"/> (each from its 00 00 01 prefix to the next one).</summary>
    public static List<Range> SplitUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<Range>();
        var start = -1;
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                continue;
            if (start >= 0)
                units.Add(start..TrimZeros(data, start, i));
            start = i;
            i += 2;
        }

        if (start >= 0)
            units.Add(start..TrimZeros(data, start, data.Length));
        return units;
    }

    private static int TrimZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start + 4 && data[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>The start code value of a unit (its fourth byte), or -1.</summary>
    public static int StartCode(ReadOnlySpan<byte> unit) => unit.Length > 3 ? unit[3] : -1;

    public static bool IsPicture(int startCode) => startCode is IntraPicture or InterPicture;

    /// <summary>The sequence header unit (start code included) in <paramref name="data"/>, or empty.</summary>
    public static ReadOnlySpan<byte> FindSequenceHeader(ReadOnlySpan<byte> data)
    {
        foreach (var range in SplitUnits(data))
        {
            if (StartCode(data[range]) == SequenceHeader)
                return data[range];
        }

        return [];
    }

    /// <summary>
    /// Parses the first sequence header in <paramref name="data"/> and the sequence display and mastering display
    /// extensions after it; null when there is none or it is malformed.
    /// </summary>
    public static AvsSequence? ParseSequence(AvsGeneration generation, ReadOnlySpan<byte> data)
    {
        AvsSequence? sequence = null;
        foreach (var range in SplitUnits(data))
        {
            var unit = data[range];
            var code = StartCode(unit);
            if (sequence is null)
            {
                if (code == SequenceHeader)
                    sequence = ParseSequenceHeader(generation, unit);
                continue;
            }

            if (code == Extension)
                sequence = ApplyExtension(sequence, unit);
            else if (code != UserData)
                break;
        }

        return sequence;
    }

    /// <summary>Parses a sequence header unit (start code included); null when it is malformed.</summary>
    public static AvsSequence? ParseSequenceHeader(AvsGeneration generation, ReadOnlySpan<byte> unit)
    {
        if (unit.Length < 8 || StartCode(unit) != SequenceHeader)
            return null;
        try
        {
            return generation switch
            {
                AvsGeneration.Avs1 => ParseAvs1(unit[4..]),
                AvsGeneration.Avs2 => ParseAvs2(unit[4..]),
                _ => ParseAvs3(unit[4..]),
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static AvsSequence ParseAvs1(ReadOnlySpan<byte> payload)
    {
        var r = new BitReader(payload);
        var profile = (int)r.Read(8);
        var level = (int)r.Read(8);
        var progressive = r.Flag();
        var width = (int)r.Read(14);
        var height = (int)r.Read(14);
        var chroma = (int)r.Read(2);
        var precision = (int)r.Read(3);
        var aspect = (int)r.Read(4);
        var rate = (int)r.Read(4);
        r.Skip(18 + 1 + 12); // bit_rate_lower, marker_bit, bit_rate_upper
        var lowDelay = r.Flag();
        return new AvsSequence
        {
            Generation = AvsGeneration.Avs1,
            ProfileId = profile,
            LevelId = level,
            Progressive = progressive,
            Width = width,
            Height = height,
            DisplayWidth = width,
            DisplayHeight = height,
            ChromaFormat = chroma,
            BitDepth = precision == 2 ? 10 : 8,
            AspectRatio = aspect,
            FrameRate = FrameRate(rate),
            LowDelay = lowDelay,
        };
    }

    private static AvsSequence ParseAvs2(ReadOnlySpan<byte> payload)
    {
        var r = new BitReader(payload);
        var profile = (int)r.Read(8);
        var level = (int)r.Read(8);
        var progressive = r.Flag();
        var fieldCoded = r.Flag();
        var width = (int)r.Read(14);
        var height = (int)r.Read(14);
        var chroma = (int)r.Read(2);
        var precision = (int)r.Read(3); // sample_precision
        if (profile == 0x22)
            precision = (int)r.Read(3); // encoding_precision (Main 10)
        var aspect = (int)r.Read(4);
        var rate = (int)r.Read(4);
        r.Skip(18 + 1 + 12); // bit_rate_lower, marker_bit, bit_rate_upper
        var lowDelay = r.Flag();
        r.Skip(1); // marker_bit
        var temporalId = r.Flag();
        r.Skip(18 + 3); // bbv_buffer_size, lcu_size
        if (r.Flag() && r.Flag()) // weight_quant_enable_flag, load_seq_weight_quant_data_flag
        {
            for (var i = 0; i < 16 + 64; i++)
                r.Ue(); // weight_quant_coeff
        }

        var background = !r.Flag(); // background_picture_disable
        r.Skip(10 + 1); // mhpskip … pmvr enable flags, marker_bit
        var rps = (int)r.Read(6);
        for (var i = 0; i < rps; i++)
        {
            r.Skip(1); // refered_by_others
            r.Skip(6 * (int)r.Read(3)); // reference delta COIs
            r.Skip(6 * (int)r.Read(3)); // removed picture delta COIs
            r.Skip(1); // marker_bit
        }

        var reorder = lowDelay ? 0 : (int)r.Read(5);
        return new AvsSequence
        {
            Generation = AvsGeneration.Avs2,
            ProfileId = profile,
            LevelId = level,
            Progressive = progressive,
            FieldCoded = fieldCoded,
            Width = width,
            Height = height,
            DisplayWidth = width,
            DisplayHeight = height,
            ChromaFormat = chroma,
            BitDepth = precision == 2 ? 10 : 8,
            AspectRatio = aspect,
            FrameRate = FrameRate(rate),
            LowDelay = lowDelay,
            TemporalIdEnabled = temporalId,
            OutputReorderDelay = reorder,
            BackgroundPictures = background,
        };
    }

    private static AvsSequence ParseAvs3(ReadOnlySpan<byte> payload)
    {
        var r = new BitReader(payload);
        var profile = (int)r.Read(8);
        var level = (int)r.Read(8);
        var progressive = r.Flag();
        var fieldCoded = r.Flag();
        r.Skip(2 + 1); // library_stream_flag, library_picture_enable_flag, marker_bit
        var width = (int)r.Read(14);
        r.Skip(1);
        var height = (int)r.Read(14);
        var chroma = (int)r.Read(2);
        var precision = (int)r.Read(3); // sample_precision
        if (profile is 0x22 or 0x32)
            precision = (int)r.Read(3); // encoding_precision (10-bit profiles)
        r.Skip(1);
        var aspect = (int)r.Read(4);
        var rate = (int)r.Read(4);
        r.Skip(1 + 18 + 1 + 12); // marker_bit, bit_rate_lower, marker_bit, bit_rate_upper
        var lowDelay = r.Flag();
        var temporalId = r.Flag();
        r.Skip(1 + 18 + 1 + 4); // marker_bit, bbv_buffer_size, marker_bit, max_dpb_minus1
        r.Skip(1); // rpl1_index_exist_flag
        var rpl1SameAsRpl0 = r.Flag();
        r.Skip(1); // marker_bit
        for (var list = 0; list < (rpl1SameAsRpl0 ? 1 : 2); list++)
        {
            var count = r.Ue();
            for (var i = 0; i < count; i++)
                SkipAvs3RefPicList(ref r);
        }

        r.Ue();
        r.Ue(); // num_ref_default_active_minus1[0..1]
        var reorder = 0;
        if (profile is 0x20 or 0x22 && !lowDelay)
            reorder = ReadAvs3ReorderDelay(ref r);
        return new AvsSequence
        {
            Generation = AvsGeneration.Avs3,
            ProfileId = profile,
            LevelId = level,
            Progressive = progressive,
            FieldCoded = fieldCoded,
            Width = width,
            Height = height,
            DisplayWidth = width,
            DisplayHeight = height,
            ChromaFormat = chroma,
            BitDepth = precision == 2 ? 10 : 8,
            AspectRatio = aspect,
            FrameRate = FrameRate(rate),
            LowDelay = lowDelay,
            TemporalIdEnabled = temporalId,
            OutputReorderDelay = reorder,
        };
    }

    /// <summary>
    /// The tool flags of a Main / Main 10 AVS3 sequence header, up to output_reorder_delay (the High profiles add
    /// tools; their reorder delay only offsets the presentation order, so it is not needed).
    /// </summary>
    private static int ReadAvs3ReorderDelay(ref BitReader r)
    {
        r.Skip(3 + 2 + 2 + 3 + 3 + 3 + 2 + 1); // CU and partition sizes, marker_bit
        if (r.Flag() && r.Flag()) // weight_quant_enable_flag, load_seq_weight_quant_data_flag
        {
            for (var i = 0; i < 16 + 64; i++)
                r.Ue();
        }

        r.Skip(6); // secondary transform, SAO, ALF, affine, SMVD, IPCM
        var amvr = r.Flag();
        var hmvp = (int)r.Read(4);
        r.Skip(1); // umve_enable_flag
        if (amvr && hmvp > 0)
            r.Skip(1); // emvr_enable_flag
        r.Skip(2 + 1); // ipf_enable_flag, tscpm_enable_flag, marker_bit
        if (r.Flag()) // dt_intra_enable_flag
            r.Skip(2);
        r.Skip(1); // position_based_transform_enable_flag
        return (int)r.Read(5);
    }

    private static void SkipAvs3RefPicList(ref BitReader r)
    {
        var entries = r.Ue();
        for (var i = 0; i < entries; i++)
        {
            var delta = r.Ue();
            if (i == 0 || delta != 0)
                r.Skip(1); // sign
        }
    }

    private static double FrameRate(int code) => code > 0 && code < s_frameRates.Length ? s_frameRates[code] : 0;

    /// <summary>Applies a sequence-level extension unit (sequence display, mastering display) to <paramref name="sequence"/>.</summary>
    private static AvsSequence ApplyExtension(AvsSequence sequence, ReadOnlySpan<byte> unit)
    {
        if (unit.Length < 5)
            return sequence;
        try
        {
            var r = new BitReader(unit[4..]);
            switch ((int)r.Read(4))
            {
                case SequenceDisplayExtension:
                {
                    r.Skip(3); // video_format
                    var full = r.Flag(); // sample_range
                    var color = sequence.Color;
                    if (r.Flag()) // colour_description
                    {
                        int primaries = (int)r.Read(8), transfer = (int)r.Read(8), matrix = (int)r.Read(8);
                        color = sequence.Generation == AvsGeneration.Avs1
                            ? new ColorInfo(primaries, transfer, matrix, full) // MPEG-2 code points, as H.273
                            : MapColor(primaries, transfer, matrix, full);
                    }

                    var displayWidth = (int)r.Read(14);
                    r.Skip(1);
                    var displayHeight = (int)r.Read(14);
                    return sequence with
                    {
                        Color = color,
                        DisplayWidth = displayWidth > 0 ? displayWidth : sequence.Width,
                        DisplayHeight = displayHeight > 0 ? displayHeight : sequence.Height,
                    };
                }

                case MasteringDisplayExtension:
                    return sequence with { Hdr = ParseMasteringDisplay(ref r) };
                default:
                    return sequence;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return sequence;
        }
    }

    /// <summary>
    /// mastering_display_and_content_metadata_extension (after its extension_id): primaries in G, B, R order and the
    /// white point in 0.00002 units, maximum luminance in cd/m², minimum in 0.0001 cd/m², then MaxCLL and MaxFALL, each
    /// value followed by a marker bit.
    /// </summary>
    private static HdrInfo ParseMasteringDisplay(ref BitReader r)
    {
        var v = new int[12];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (int)r.Read(16);
            r.Skip(1);
        }

        const double unit = 0.00002;
        (double, double) P(int i) => (v[i] * unit, v[i + 1] * unit);
        return new HdrInfo
        {
            DisplayPrimaries = [P(4), P(0), P(2)], // R, G, B
            WhitePoint = P(6),
            MaxLuminance = v[8],
            MinLuminance = v[9] * 0.0001,
            MaxCll = v[10] > 0 ? v[10] : null,
            MaxFall = v[11] > 0 ? v[11] : null,
        };
    }

    /// <summary>AVS2/AVS3 colour code points in ITU-T H.273 terms (as FFmpeg maps them).</summary>
    public static ColorInfo MapColor(int primaries, int transfer, int matrix, bool fullRange)
    {
        var p = primaries is >= 1 and <= 9 ? primaries : 2;
        var t = transfer switch
        {
            >= 1 and <= 10 => transfer,
            11 => 15, // BT.2020 12-bit
            12 => 16, // SMPTE ST 2084 (PQ)
            14 => 18, // ARIB STD-B67 (HLG)
            _ => 2,
        };
        var m = matrix switch
        {
            >= 1 and <= 7 => matrix,
            8 => 9, // BT.2020 non-constant luminance
            9 => 10, // BT.2020 constant luminance
            _ => 2,
        };
        return new ColorInfo(p, t, m, fullRange);
    }

    /// <summary>
    /// Parses a picture header unit (0xB3 or 0xB6, start code included) up to the fields that give the picture's
    /// order; null when it is malformed.
    /// </summary>
    public static AvsPicture? ParsePictureHeader(AvsSequence sequence, ReadOnlySpan<byte> unit)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var code = StartCode(unit);
        if (!IsPicture(code))
            return null;
        var intra = code == IntraPicture;
        try
        {
            var r = new BitReader(unit[4..]);
            return sequence.Generation switch
            {
                AvsGeneration.Avs1 => ParseAvs1Picture(ref r, sequence, intra),
                AvsGeneration.Avs2 => ParseAvs2Picture(ref r, sequence, intra),
                _ => ParseAvs3Picture(ref r, sequence, intra),
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static AvsPicture ParseAvs1Picture(ref BitReader r, AvsSequence sequence, bool intra)
    {
        r.Skip(16); // bbv_delay
        if (sequence.ProfileId == 0x48)
            r.Skip(1 + 7); // AVS+ (broadcasting profile): marker_bit, bbv_delay_extension
        if (intra)
        {
            if (r.Flag()) // time_code_flag
                r.Skip(24);
            // Streams of the first revision have no marker bit here (as FFmpeg tells them apart).
            var peek = r;
            var nine = peek.Read(9);
            var revised = sequence.LowDelay || (nine & 1) == 0;
            if (!revised)
            {
                peek = r;
                revised = (peek.Read(11) & 3) != 0;
            }

            if (revised)
                r.Skip(1); // marker_bit
        }
        else
        {
            r.Skip(2); // picture_coding_type
        }

        return new AvsPicture(intra, (int)r.Read(8), 0);
    }

    private static AvsPicture ParseAvs2Picture(ref BitReader r, AvsSequence sequence, bool intra)
    {
        r.Skip(32); // bbv_delay
        if (intra)
        {
            if (r.Flag()) // time_code_flag
                r.Skip(24);
            if (sequence.BackgroundPictures && r.Flag()) // background_picture_flag
                r.Skip(1); // background_picture_output_flag
        }
        else
        {
            var type = (int)r.Read(2); // picture_coding_type: 1 P, 2 B, 3 F
            if (sequence.BackgroundPictures && type is 1 or 3)
            {
                var backgroundPred = type == 1 && r.Flag();
                if (!backgroundPred)
                    r.Skip(1); // background_reference_enable
            }
        }

        var coi = (int)r.Read(8);
        if (sequence.TemporalIdEnabled)
            r.Skip(3);
        var delay = sequence.LowDelay ? 0 : (int)r.Ue();
        return new AvsPicture(intra, coi, delay);
    }

    private static AvsPicture ParseAvs3Picture(ref BitReader r, AvsSequence sequence, bool intra)
    {
        if (!intra)
            r.Skip(1); // random_access_decodable_flag
        r.Skip(32); // bbv_delay
        if (!intra)
            r.Skip(2); // picture_coding_type
        else if (r.Flag()) // time_code_flag
            r.Skip(24);
        var doi = (int)r.Read(8);
        if (sequence.TemporalIdEnabled)
            r.Skip(3);
        var delay = sequence.LowDelay ? 0 : (int)r.Ue();
        return new AvsPicture(intra, doi, delay);
    }

    /// <summary>
    /// Presentation order of pictures in decoding order. AVS1 uses picture_distance, which encoders restart at an I
    /// picture (a new GOP): an I picture whose distance goes back starts after the latest picture so far. AVS2/AVS3 unwrap
    /// the decoding order index and add the output delay minus the sequence's reorder delay (as the reference decoders do).
    /// </summary>
    public sealed class OrderCounter
    {
        private int _cycles;
        private int _previous = -1;
        private long _base;
        private long _latest = -1;

        public long Next(AvsSequence sequence, AvsPicture picture)
        {
            ArgumentNullException.ThrowIfNull(sequence);
            if (sequence.Generation == AvsGeneration.Avs1)
            {
                if (_previous >= 0)
                {
                    if (picture.Intra && picture.OrderIndex < _previous && _previous - picture.OrderIndex <= 128)
                    {
                        _base = _latest + 1 - picture.OrderIndex; // a new GOP restarting its distances
                        _cycles = 0;
                    }
                    else if (picture.OrderIndex < _previous && _previous - picture.OrderIndex > 128)
                    {
                        _cycles++; // modulo 256 wrap
                    }
                    else if (picture.OrderIndex > _previous && picture.OrderIndex - _previous > 128)
                    {
                        _cycles--;
                    }
                }

                _previous = picture.OrderIndex;
                var order = _base + _cycles * 256L + picture.OrderIndex;
                _latest = Math.Max(_latest, order);
                return order;
            }

            if (_previous >= 0 && picture.OrderIndex < _previous)
                _cycles++;
            _previous = picture.OrderIndex;
            return _cycles * 256L + picture.OrderIndex + picture.OutputDelay - sequence.OutputReorderDelay;
        }
    }

    /// <summary>
    /// True when <paramref name="data"/> (a picture's units) carries HDR Vivid (CUVA / T/UWA 005.1) metadata: an AVS2/AVS3
    /// HDR picture extension (extension_id 0101) of hdr_dynamic_metadata_type 5, whose bytes are the T.35 message
    /// (country 0x26, provider 0x0004, oriented code 0x0005) as HEVC carries it.
    /// </summary>
    public static bool HasHdrDynamicMetadata(ReadOnlySpan<byte> data)
    {
        var afterPicture = false;
        foreach (var range in SplitUnits(data))
        {
            var unit = data[range];
            var code = StartCode(unit);
            if (IsPicture(code))
                afterPicture = true;
            else if (code == SequenceHeader)
                afterPicture = false;
            else if (afterPicture && code == Extension && unit.Length > 4 && unit[4] >> 4 == HdrDynamicMetadataExtension &&
                     (unit[4] & 0xF) == HdrVividMetadataType)
                return true;
        }

        return false;
    }

    /// <summary>Profile name of a profile_id.</summary>
    public static string ProfileName(AvsGeneration generation, int profileId) => (generation, profileId) switch
    {
        (AvsGeneration.Avs1, 0x20) => "Jizhun",
        (AvsGeneration.Avs1, 0x48) => "Guangdian (AVS+)",
        (AvsGeneration.Avs1, 0x24) => "Shenzhan",
        (AvsGeneration.Avs1, 0x28) => "Yidong",
        (AvsGeneration.Avs2, 0x12) => "Main Picture",
        (AvsGeneration.Avs2, 0x20) => "Main",
        (AvsGeneration.Avs2, 0x22) => "Main 10",
        (AvsGeneration.Avs3, 0x20) => "Main",
        (AvsGeneration.Avs3, 0x22) => "Main 10",
        (AvsGeneration.Avs3, 0x30) => "High",
        (AvsGeneration.Avs3, 0x32) => "High 10",
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Label_Profile, "0x" + profileId.ToString("X2", CultureInfo.InvariantCulture)),
    };

    /// <summary>"Main 10@L6.2.60" (AVS2/AVS3 levels name the picture size class, its variant and the frame rate class).</summary>
    public static string ProfileLevel(AvsGeneration generation, int profileId, int levelId)
    {
        var profile = ProfileName(generation, profileId);
        if (levelId == 0)
            return profile;
        if (generation == AvsGeneration.Avs1)
            return profile + "@L" + string.Create(CultureInfo.InvariantCulture, $"{levelId >> 4}.{levelId & 0xF}");
        var size = (levelId >> 4) switch
        {
            1 => "2",
            2 => "4",
            4 => "6",
            5 => "8",
            6 => "10",
            _ => null,
        };
        var low = levelId & 0xF;
        if (size is null || low > 0xB || (low & 1) == 1 && size is "2" or "4")
            return profile + ", " + string.Format(CultureInfo.CurrentCulture, Strings.Label_Level, "0x" + levelId.ToString("X2", CultureInfo.InvariantCulture));
        var variant = size is "2" or "4" ? 0 : low & 2;
        var rate = size is "2" ? (low >> 1) switch { 0 => 15, 1 => 30, _ => 60 }
            : size is "4" ? (low >> 1 == 0 ? 30 : 60)
            : (low >> 2) switch { 0 => 30, 1 => 60, _ => 120 };
        return profile + string.Create(CultureInfo.InvariantCulture, $"@L{size}.{variant}.{rate}");
    }

    /// <summary>The AVS3 decoder configuration record ('av3c', as GPAC reads and writes it) for a sequence header unit.</summary>
    public static byte[] BuildAv3C(ReadOnlySpan<byte> sequenceHeader, int libraryDependencyIdc = 0)
    {
        byte[] record = [1, (byte)(sequenceHeader.Length >> 8), (byte)sequenceHeader.Length, .. sequenceHeader, (byte)(0xFC | (libraryDependencyIdc & 3))];
        return record;
    }

    /// <summary>The sequence header stored in an 'av3c' record, or empty.</summary>
    public static ReadOnlySpan<byte> SequenceHeaderOfAv3C(ReadOnlySpan<byte> av3c)
    {
        if (av3c.Length < 3 || av3c[0] != 1)
            return [];
        var length = (av3c[1] << 8) | av3c[2];
        return length <= av3c.Length - 3 ? av3c.Slice(3, length) : [];
    }
}
