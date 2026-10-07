using System.Buffers.Binary;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>Fields of a Dolby Vision RPU header (rpu_data_header) needed to describe the stream.</summary>
public sealed record DolbyVisionRpuHeader
{
    public int RpuFormat { get; init; }

    public int VdrRpuProfile { get; init; }

    public int VdrRpuLevel { get; init; }

    public bool BlVideoFullRange { get; init; }

    public int BlBitDepth { get; init; }

    public int ElBitDepth { get; init; }

    public int VdrBitDepth { get; init; }

    public bool ElSpatialResamplingFilter { get; init; }

    public bool DisableResidual { get; init; }

    /// <summary>The RPU describes an enhancement layer (residual) on top of the base layer.</summary>
    public bool HasEnhancementLayer => ElSpatialResamplingFilter && !DisableResidual;
}

/// <summary>What was found in the video bitstream, and the configuration record rebuilt from it.</summary>
public sealed record DolbyVisionDetection(
    int Profile,
    int Level,
    int BlSignalCompatibilityId,
    bool RpuPresent,
    bool ElPresent,
    bool BlPresent,
    byte[] ConfigurationRecord)
{
    /// <summary>"8.1", "5", "7.6", "10.1"…</summary>
    public string ProfileName => Profile == 5 ? "5" : $"{Profile}.{BlSignalCompatibilityId}";

    public DolbyVisionInfo Info => DolbyVision.ParseConfigurationRecord(ConfigurationRecord);
}

/// <summary>
/// Dolby Vision helpers: RPU detection in HEVC/AV1 samples and the DOVIDecoderConfigurationRecord
/// (<c>dvcC</c>/<c>dvvC</c>/<c>dvwC</c> in MP4, BlockAdditionMapping in Matroska) rebuilt from the base layer and
/// the RPU, following Dolby's ISO base media file format specification and the logic of dovi_tool and mkvtoolnix.
/// </summary>
public static class DolbyVision
{
    /// <summary>First payload byte of an RPU (rpu_nal_prefix).</summary>
    public const byte RpuNalPrefix = 0x19;

    /// <summary>HEVC NAL unit type carrying the RPU (UNSPEC62).</summary>
    public const int HevcRpuNalType = 62;

    /// <summary>HEVC NAL unit type carrying an in-band enhancement layer (UNSPEC63).</summary>
    public const int HevcElNalType = 63;

    /// <summary>Size of the configuration record.</summary>
    public const int RecordSize = 24;

    /// <summary>Parses the RPU header from an RPU payload (starting with the 0x19 prefix, emulation prevention removed).</summary>
    public static DolbyVisionRpuHeader? ParseRpuHeader(ReadOnlySpan<byte> rpu)
    {
        if (rpu.Length < 8 || rpu[0] != RpuNalPrefix)
            return null;
        try
        {
            var r = new BitReader(rpu[1..]);
            if (r.Read(6) != 2) // rpu_type
                return null;
            var format = (int)r.Read(11);
            var profile = (int)r.Read(4);
            var level = (int)r.Read(4);
            if (!r.Flag()) // vdr_seq_info_present_flag: the header that matters only comes with sequence info
                return null;
            r.Skip(1); // chroma_resampling_explicit_filter_flag
            var coefficientType = r.Read(2);
            if (coefficientType == 0)
                r.Ue(); // coefficient_log2_denom
            r.Skip(2); // vdr_rpu_normalized_idc
            var fullRange = r.Flag();
            int bl = 0, el = 0, vdr = 0;
            bool elResampling = false, disableResidual = true;
            if ((format & 0x700) == 0)
            {
                bl = (int)r.Ue() + 8;
                el = (int)(r.Ue() & 0xFF) + 8;
                vdr = (int)r.Ue() + 8;
                r.Skip(1); // spatial_resampling_filter_flag
                r.Skip(3); // reserved
                elResampling = r.Flag();
                disableResidual = r.Flag();
            }

            return new DolbyVisionRpuHeader
            {
                RpuFormat = format,
                VdrRpuProfile = profile,
                VdrRpuLevel = level,
                BlVideoFullRange = fullRange,
                BlBitDepth = bl,
                ElBitDepth = el,
                VdrBitDepth = vdr,
                ElSpatialResamplingFilter = elResampling,
                DisableResidual = disableResidual,
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Dolby Vision profile (4, 5, 7 or 8) described by an RPU header; 0 when unknown.</summary>
    public static int Profile(DolbyVisionRpuHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.VdrRpuProfile == 0 && header.BlVideoFullRange)
            return 5;
        if (header.VdrRpuProfile != 1)
            return 0;
        if (header.HasEnhancementLayer)
            return header.VdrBitDepth == 12 ? 7 : 4;
        return 8;
    }

    /// <summary>bl_signal_compatibility_id for a profile and the base layer's colour description.</summary>
    public static int CompatibilityId(int profile, ColorInfo color) => profile switch
    {
        4 => 2,
        5 => 0,
        7 => 6,
        9 => 2,
        8 or 10 when color.Primaries == 9 && color.Matrix is 9 or 10 && color.Transfer == 16 => 1, // HDR10
        8 or 10 when color.Primaries == 9 && color.Matrix is 9 or 10 && color.Transfer is 14 or 18 => 4, // HLG (18, or 14 with HLG in the alternative transfer SEI; there is no 8.0)
        8 or 10 when color.Primaries == 9 && color.Matrix is 9 or 10 => 0,
        8 or 10 when color.IsSpecified => 2, // BT.709 / SDR
        _ => 0,
    };

    /// <summary>Dolby Vision level (1–13) from the luma sample rate.</summary>
    public static int Level(int width, int height, double frameRate)
    {
        if (width <= 0 || height <= 0 || frameRate <= 0)
            return 0;
        var pps = (ulong)Math.Ceiling(frameRate * width * height);
        return pps switch
        {
            <= 22_118_400 => 1,
            <= 27_648_000 => 2,
            <= 49_766_400 => 3,
            <= 62_208_000 => 4,
            <= 124_416_000 => 5,
            <= 199_065_600 => 6,
            <= 248_832_000 => 7,
            <= 398_131_200 => 8,
            <= 497_664_000 => 9,
            <= 995_328_000 when width <= 3840 => 10,
            <= 995_328_000 => 11,
            <= 1_990_656_000 => 12,
            _ => 13,
        };
    }

    /// <summary>Builds the 24-byte DOVIDecoderConfigurationRecord (version 1.0).</summary>
    public static byte[] BuildConfigurationRecord(int profile, int level, bool rpu, bool el, bool bl, int compatibilityId)
    {
        var record = new byte[RecordSize];
        record[0] = 1; // dv_version_major
        record[1] = 0; // dv_version_minor
        var bits = ((profile & 0x7F) << 9) | ((level & 0x3F) << 3) | (rpu ? 4 : 0) | (el ? 2 : 0) | (bl ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), (ushort)bits);
        record[4] = (byte)((compatibilityId & 0x0F) << 4); // + 28 reserved bits, then 4×32 reserved bits
        return record;
    }

    /// <summary>A copy of <paramref name="record"/> with dv_level replaced.</summary>
    public static byte[] WithLevel(ReadOnlySpan<byte> record, int level)
    {
        if (record.Length < 5)
            throw new InvalidDataException(Strings.Error_DolbyVisionRecordTooShort);
        var copy = record.ToArray();
        var bits = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(2));
        bits = (ushort)((bits & ~(0x3F << 3)) | ((level & 0x3F) << 3));
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), bits);
        return copy;
    }

    /// <summary>Parses a DOVIDecoderConfigurationRecord.</summary>
    public static DolbyVisionInfo ParseConfigurationRecord(ReadOnlySpan<byte> p)
    {
        if (p.Length < 5)
            throw new InvalidDataException(Strings.Error_DolbyVisionRecordTooShort);
        var profile = p[2] >> 1;
        var level = ((p[2] & 1) << 5) | (p[3] >> 3);
        return new DolbyVisionInfo(p[0], p[1], profile, level, (p[3] & 4) != 0, (p[3] & 2) != 0, (p[3] & 1) != 0, p[4] >> 4);
    }

    /// <summary>MP4 box type for a record: dvcC (profiles ≤ 7), dvvC (8–10) or dvwC (above).</summary>
    public static string Mp4BoxType(int profile) => profile <= 7 ? "dvcC" : profile <= 10 ? "dvvC" : "dvwC";

    /// <summary>
    /// MP4 sample entry type for a Dolby Vision stream whose base codec uses <paramref name="entryType"/> (the base
    /// type or its Dolby Vision variant), per "Dolby Vision Streams Within the ISO Base Media File Format": dvh1, dvhe,
    /// dva1 or dvav only for the profiles without a cross-compatible base layer (1, 3 and 5), and the base codec's type
    /// otherwise. AV1 profile 10 without a compatible base layer (bl_signal_compatibility_id 0) should be dav1, but
    /// FFmpeg and the players built on it cannot read that sample entry, so it is only used when
    /// <paramref name="av1UsesDav1"/>; av01 with the dvvC box is what those files carry in practice. Without a record
    /// the base type is returned.
    /// </summary>
    public static string Mp4SampleEntryType(string entryType, DolbyVisionInfo? info, bool av1UsesDav1 = false)
    {
        ArgumentNullException.ThrowIfNull(entryType);
        var baseType = entryType switch
        {
            "dvh1" => "hvc1",
            "dvhe" => "hev1",
            "dva1" => "avc1",
            "dvav" => "avc3",
            "dav1" => "av01",
            _ => entryType,
        };
        if (info is null)
            return baseType;
        if (info.Profile is 1 or 3 or 5)
        {
            return baseType switch
            {
                "hvc1" => "dvh1",
                "hev1" => "dvhe",
                "avc1" => "dva1",
                "avc3" => "dvav",
                _ => baseType,
            };
        }

        return baseType == "av01" && info.Profile == 10 && info.BlSignalCompatibilityId == 0 && av1UsesDav1 ? "dav1" : baseType;
    }

    /// <summary>
    /// Whether the sample entry needs an hvcE/avcE box: a single track carrying base layer, enhancement layer and RPU
    /// (e.g. profile 7 FEL/MEL muxed into one track).
    /// </summary>
    public static bool NeedsEnhancementLayerConfig(DolbyVisionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.BlPresent && info.ElPresent && info.RpuPresent && info.Profile is not (10 or > 10);
    }

    /// <summary>
    /// Whether the record describes an enhancement-layer-only track (dual-track storage): it needs a 'vdep' track
    /// reference to the base layer track.
    /// </summary>
    public static bool IsEnhancementLayerTrack(DolbyVisionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return !info.BlPresent && info.ElPresent;
    }

    /// <summary>
    /// ftyp compatible brands for a file with a Dolby Vision track: dby1, plus db1p (HDR10-compatible), db2g
    /// (SDR-compatible), db4g (BT.2020 SDR) or db4h (HLG) by the base layer's compatibility.
    /// </summary>
    public static IReadOnlyList<string> Mp4Brands(DolbyVisionInfo info, ColorInfo color)
    {
        ArgumentNullException.ThrowIfNull(info);
        var brands = new List<string> { "dby1" };
        switch (info.BlSignalCompatibilityId)
        {
            case 1:
                brands.Add("db1p");
                break;
            case 2:
                brands.Add("db2g");
                break;
            case 4 when color.Transfer == 14:
                brands.Add("db4g");
                break;
            case 4 when color.Transfer == 18:
                brands.Add("db4h");
                break;
        }

        return brands;
    }

    /// <summary>
    /// Looks for Dolby Vision in HEVC samples (length-prefixed NAL units): RPU (UNSPEC62) and enhancement layer
    /// (UNSPEC63) NAL units. Returns the first RPU header with sequence information, and whether an EL was seen.
    /// </summary>
    public static (DolbyVisionRpuHeader? Header, bool RpuSeen, bool ElSeen) ScanHevc(IEnumerable<ReadOnlyMemory<byte>> samples, int nalLengthSize)
    {
        ArgumentNullException.ThrowIfNull(samples);
        DolbyVisionRpuHeader? header = null;
        bool rpuSeen = false, elSeen = false;
        foreach (var sample in samples)
        {
            var data = sample.Span;
            foreach (var range in NalUnits.SplitLengthPrefixed(data, nalLengthSize))
            {
                var nal = data[range];
                switch (NalUnits.HevcType(nal))
                {
                    case HevcRpuNalType when nal.Length > 3:
                        rpuSeen = true;
                        header ??= ParseRpuHeader(NalUnits.ToRbsp(nal[2..]));
                        break;
                    case HevcElNalType:
                        elSeen = true;
                        break;
                }
            }

            if (header is not null && (elSeen || header.HasEnhancementLayer == false))
                break;
        }

        return (header, rpuSeen, elSeen);
    }

    /// <summary>
    /// Looks for Dolby Vision in AV1 samples: a metadata OBU of type ITU-T T.35 with the Dolby provider code
    /// (country 0xB5, provider 0x003B, provider-oriented code 0x00000800). The RPU inside its EMDF container is
    /// unwrapped and its header parsed when possible.
    /// </summary>
    public static (bool Found, DolbyVisionRpuHeader? Header) ScanAv1(IEnumerable<ReadOnlyMemory<byte>> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var found = false;
        foreach (var sample in samples)
        {
            var data = sample.Span;
            var pos = 0;
            while (pos < data.Length)
            {
                var header = data[pos];
                var type = (header >> 3) & 0x0F;
                var hasExtension = (header & 0x04) != 0;
                var hasSize = (header & 0x02) != 0;
                var p = pos + 1 + (hasExtension ? 1 : 0);
                long size;
                if (hasSize)
                {
                    if (!Leb128(data, ref p, out size))
                        break;
                }
                else
                {
                    size = data.Length - p;
                }

                if (p + size > data.Length)
                    break;
                if (type == 5) // OBU_METADATA
                {
                    var payload = data.Slice(p, (int)size);
                    var q = 0;
                    if (Leb128(payload, ref q, out var metadataType) && metadataType == 4 && payload.Length >= q + 7 &&
                        payload[q] == 0xB5 && BinaryPrimitives.ReadUInt16BigEndian(payload[(q + 1)..]) == 0x003B &&
                        BinaryPrimitives.ReadUInt32BigEndian(payload[(q + 3)..]) == 0x00000800)
                    {
                        found = true;
                        if (UnwrapAv1Rpu(payload[(q + 7)..]) is { } rpu && ParseRpuHeader(rpu) is { } parsed)
                            return (true, parsed);
                    }
                }

                pos = p + (int)size;
            }
        }

        return (found, null);
    }

    /// <summary>Extracts the RPU (with its 0x19 prefix) from the EMDF container of an AV1 T.35 payload.</summary>
    public static byte[]? UnwrapAv1Rpu(ReadOnlySpan<byte> emdf)
    {
        try
        {
            var r = new BitReader(emdf);
            if (r.Read(2) != 0 || r.Read(3) != 6 || r.Read(5) != 31) // emdf_version, key_id, emdf_payload_id
                return null;
            if (VariableBits(ref r, 5) != 225) // emdf_payload_id_ext
                return null;
            if (r.Read(4) != 0 || !r.Flag()) // smploffste, duratione, groupide, codecdatae; discard_unknown_payload
                return null;
            var size = VariableBits(ref r, 8);
            if (size <= 0 || size > emdf.Length)
                return null;
            var rpu = new byte[size + 1];
            rpu[0] = RpuNalPrefix;
            for (var i = 1; i <= size; i++)
                rpu[i] = (byte)r.Read(8);
            return rpu;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static long VariableBits(ref BitReader r, int bits)
    {
        long value = 0;
        while (true)
        {
            value += r.Read(bits);
            if (!r.Flag())
                return value;
            value = (value << bits) + (1L << bits);
        }
    }

    /// <summary>Snaps a measured frame rate to the nearest common rate (within 0.5%), so levels do not depend on rounding.</summary>
    public static double NominalFrameRate(double fps)
    {
        double[] rates = [24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 48000.0 / 1001, 48, 50, 60000.0 / 1001, 60, 100, 120000.0 / 1001, 120];
        foreach (var rate in rates)
        {
            if (Math.Abs(fps - rate) / rate < 0.005)
                return rate;
        }

        return fps;
    }

    /// <summary>
    /// Builds the configuration record for a stream: HEVC from its RPU header, AV1 always profile 10.
    /// Level comes from the base layer's resolution and frame rate, compatibility from its colour description.
    /// </summary>
    public static DolbyVisionDetection? Describe(CodecType codec, DolbyVisionRpuHeader? header, bool elInBand, int width, int height, double frameRate, ColorInfo color)
    {
        int profile;
        int compat;
        var el = false;
        switch (codec)
        {
            case CodecType.Hevc when header is not null:
                profile = Profile(header);
                if (profile == 0)
                    return null;
                compat = CompatibilityId(profile, color);
                if (header.HasEnhancementLayer && compat == 2 && profile != 4)
                    profile = 4; // SDR base layer with an EL is profile 4
                el = profile is 4 or 7;
                if (el && !elInBand)
                    el = false; // dual-track profile 7: the EL is elsewhere; this track carries BL + RPU only
                break;
            case CodecType.Av1:
                // AV1 is always profile 10; the RPU tells whether the base layer is cross-compatible (like 8.x) or
                // a full-range IPT signal (10.0, like profile 5).
                profile = 10;
                compat = header is not null
                    ? Profile(header) == 5 ? 0 : CompatibilityId(8, color)
                    : color.IsSpecified ? CompatibilityId(10, color) : 0;
                break;
            default:
                return null;
        }

        var level = Level(width, height, NominalFrameRate(frameRate));
        var record = BuildConfigurationRecord(profile, level, rpu: true, el, bl: true, compat);
        return new DolbyVisionDetection(profile, level, compat, true, el, true, record);
    }

    internal static bool Leb128(ReadOnlySpan<byte> data, ref int pos, out long value)
    {
        value = 0;
        for (var i = 0; i < 8; i++)
        {
            if (pos >= data.Length)
                return false;
            var b = data[pos++];
            value |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                return true;
        }

        return false;
    }
}
