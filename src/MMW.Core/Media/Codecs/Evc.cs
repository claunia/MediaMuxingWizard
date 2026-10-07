using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>Fields of an MPEG-5 EVC sequence parameter set.</summary>
public sealed record EvcSps
{
    public int Id { get; init; }

    /// <summary>profile_idc: 0 Baseline, 1 Main, 2 Baseline Still Picture, 3 Main Still Picture.</summary>
    public int ProfileIdc { get; init; }

    /// <summary>level_idc (30 × level, as in HEVC).</summary>
    public int LevelIdc { get; init; }

    public int ChromaFormatIdc { get; init; } = 1;

    public int BitDepthLuma { get; init; } = 8;

    public int BitDepthChroma { get; init; } = 8;

    /// <summary>Coded picture size in luma samples (before cropping).</summary>
    public int MaxWidth { get; init; }

    public int MaxHeight { get; init; }

    /// <summary>Displayed size: the coded size minus the cropping window (in chroma sample units).</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>sps_mmvd_flag (the slice header then signals mmvd_group_enable_flag for P/B slices).</summary>
    public bool Mmvd { get; init; }

    /// <summary>sps_alf_flag (the slice header then carries the ALF parameters).</summary>
    public bool Alf { get; init; }

    /// <summary>sps_pocs_flag: slices carry their picture order count LSB; otherwise it follows from the sub-GOP structure.</summary>
    public bool Pocs { get; init; }

    public int Log2MaxPocLsb { get; init; }

    public int Log2SubGopLength { get; init; }

    public int SarWidth { get; init; } = 1;

    public int SarHeight { get; init; } = 1;

    public uint NumUnitsInTick { get; init; }

    public uint TimeScale { get; init; }

    public Model.ColorInfo Color { get; init; } = Model.ColorInfo.Unspecified;

    public double FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? (double)TimeScale / NumUnitsInTick : 0;
}

/// <summary>The fields of an EVC picture parameter set that slice headers depend on.</summary>
public sealed record EvcPps(int Id, int SpsId, bool SingleTileInPicture, int TileIdLength, bool ArbitrarySlicePresent, int FirstTileId);

/// <summary>
/// MPEG-5 Essential Video Coding (ISO/IEC 23094-1) bitstream and decoder configuration record ('evcC', ISO/IEC 14496-15)
/// helpers. EVC NAL units are always length-prefixed (no start codes) and carry no emulation prevention bytes.
/// </summary>
public static class Evc
{
    public const int NalNonIdr = 0;
    public const int NalIdr = 1;
    public const int NalSps = 24;
    public const int NalPps = 25;
    public const int NalAps = 26;
    public const int NalFiller = 27;
    public const int NalSei = 28;

    private const int SliceB = 0;
    private const int SliceP = 1;

    /// <summary>nal_unit_type of an EVC NAL unit (nal_unit_type_plus1 − 1 in the first header byte), or -1.</summary>
    public static int NalType(ReadOnlySpan<byte> nal) => nal.Length > 1 ? ((nal[0] >> 1) & 0x3F) - 1 : -1;

    /// <summary>nuh_temporal_id of a NAL unit.</summary>
    public static int TemporalId(ReadOnlySpan<byte> nal) => nal.Length > 1 ? ((nal[0] & 1) << 2) | (nal[1] >> 6) : 0;

    /// <summary>Coded slices: IDR and non-IDR (types 2–23 are reserved VCL types).</summary>
    public static bool IsVcl(int type) => type is >= 0 and <= 23;

    public static bool IsParameterSet(int type) => type is NalSps or NalPps or NalAps;

    /// <summary>Name of a profile_idc.</summary>
    public static string ProfileName(int profileIdc) => profileIdc switch
    {
        0 => "Baseline",
        1 => "Main",
        2 => "Baseline Still Picture",
        3 => "Main Still Picture",
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Label_Profile, profileIdc),
    };

    /// <summary>"Main@L4.1".</summary>
    public static string ProfileLevel(int profileIdc, int levelIdc) =>
        levelIdc == 0
            ? ProfileName(profileIdc)
            : string.Create(CultureInfo.InvariantCulture, $"{ProfileName(profileIdc)}@L{levelIdc / 30}.{levelIdc % 30 / 3}");

    /// <summary>Profile and level of an 'evcC' record, e.g. "Baseline@L4.1"; empty when the record is invalid.</summary>
    public static string ProfileLevel(ReadOnlySpan<byte> evcC) => evcC.Length >= 3 && evcC[0] == 1 ? ProfileLevel(evcC[1], evcC[2]) : string.Empty;

    /// <summary>NAL unit length field size of the samples described by an 'evcC' record (4 when unknown).</summary>
    public static int LengthSize(ReadOnlySpan<byte> evcC) => evcC.Length > 16 ? (evcC[16] & 3) + 1 : 4;

    /// <summary>A parsed EVCDecoderConfigurationRecord.</summary>
    public sealed record EvcConfig(
        int ProfileIdc, int LevelIdc, uint ToolsetIdcH, uint ToolsetIdcL, int ChromaFormatIdc, int BitDepthLuma, int BitDepthChroma,
        int Width, int Height, int LengthSize, IReadOnlyList<(int Type, bool Complete, byte[] Nal)> Nals);

    /// <summary>Parses an EVCDecoderConfigurationRecord (the 'evcC' payload).</summary>
    /// <exception cref="InvalidDataException">The record is truncated or of an unknown version.</exception>
    public static EvcConfig ParseEvcC(ReadOnlySpan<byte> evcC)
    {
        if (evcC.Length < 18 || evcC[0] != 1)
            throw new InvalidDataException(Strings.Error_InvalidEvcC);
        try
        {
            var nals = new List<(int, bool, byte[])>();
            var pos = 18;
            for (var a = 0; a < evcC[17]; a++)
            {
                var head = evcC[pos];
                var count = (evcC[pos + 1] << 8) | evcC[pos + 2];
                pos += 3;
                for (var i = 0; i < count; i++)
                {
                    var length = (evcC[pos] << 8) | evcC[pos + 1];
                    nals.Add((head & 0x3F, (head & 0x80) != 0, evcC.Slice(pos + 2, length).ToArray()));
                    pos += 2 + length;
                }
            }

            return new EvcConfig(
                evcC[1], evcC[2], ReadUInt32(evcC[3..]), ReadUInt32(evcC[7..]), evcC[11] >> 6, ((evcC[11] >> 3) & 7) + 8, (evcC[11] & 7) + 8,
                (evcC[12] << 8) | evcC[13], (evcC[14] << 8) | evcC[15], (evcC[16] & 3) + 1, nals);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Strings.Error_TruncatedEvcC, ex);
        }
    }

    /// <summary>
    /// Builds an EVCDecoderConfigurationRecord for 4-byte length-prefixed samples from parameter sets; the profile,
    /// level, toolsets, format and size come from the first SPS.
    /// </summary>
    /// <exception cref="InvalidDataException">There is no SPS, or it cannot be parsed.</exception>
    public static byte[] BuildEvcC(IReadOnlyList<byte[]> sps, IReadOnlyList<byte[]> pps, IReadOnlyList<byte[]>? aps = null)
    {
        ArgumentNullException.ThrowIfNull(sps);
        ArgumentNullException.ThrowIfNull(pps);
        if (sps.Count == 0)
            throw new InvalidDataException(Strings.Error_EvcNeedsSps);
        var info = ParseSps(sps[0]);
        var toolsets = Toolsets(sps[0]);
        var o = new List<byte>
        {
            1, (byte)info.ProfileIdc, (byte)info.LevelIdc,
            (byte)(toolsets.High >> 24), (byte)(toolsets.High >> 16), (byte)(toolsets.High >> 8), (byte)toolsets.High,
            (byte)(toolsets.Low >> 24), (byte)(toolsets.Low >> 16), (byte)(toolsets.Low >> 8), (byte)toolsets.Low,
            (byte)((info.ChromaFormatIdc << 6) | ((info.BitDepthLuma - 8) << 3) | (info.BitDepthChroma - 8)),
            (byte)(info.MaxWidth >> 8), (byte)info.MaxWidth, (byte)(info.MaxHeight >> 8), (byte)info.MaxHeight,
            3, // reserved '000000', lengthSizeMinusOne = 3
        };
        var arrays = new[] { (NalSps, sps), (NalPps, pps), (NalAps, aps ?? []) }.Where(a => a.Item2.Count > 0).ToList();
        o.Add((byte)arrays.Count);
        foreach (var (type, nals) in arrays)
        {
            o.Add((byte)(0x80 | type)); // array_completeness
            o.Add((byte)(nals.Count >> 8));
            o.Add((byte)nals.Count);
            foreach (var nal in nals)
            {
                o.Add((byte)(nal.Length >> 8));
                o.Add((byte)nal.Length);
                o.AddRange(nal);
            }
        }

        return [.. o];
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    private static (uint High, uint Low) Toolsets(ReadOnlySpan<byte> sps)
    {
        var r = new BitReader(sps[2..]);
        r.Ue();
        r.Skip(16);
        return (r.Read(32), r.Read(32));
    }

    /// <summary>Parses a sequence parameter set NAL unit (with its 2-byte header) up to and including the VUI.</summary>
    /// <exception cref="InvalidDataException">The SPS is malformed.</exception>
    public static EvcSps ParseSps(ReadOnlySpan<byte> nal)
    {
        try
        {
            return ParseSpsCore(nal);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Strings.Error_TruncatedEvcSps, ex);
        }
    }

    private static EvcSps ParseSpsCore(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 3)
            throw new InvalidDataException(Strings.Error_TruncatedEvcSps);
        var r = new BitReader(nal[2..]);
        var id = (int)r.Ue();
        var profile = (int)r.Read(8);
        var level = (int)r.Read(8);
        r.Skip(64); // toolset_idc_h, toolset_idc_l
        var chroma = (int)r.Ue();
        if (chroma > 3)
            throw new InvalidDataException(Strings.Error_InvalidEvcChromaFormat);
        var width = (int)r.Ue();
        var height = (int)r.Ue();
        var depthLuma = (int)r.Ue() + 8;
        var depthChroma = (int)r.Ue() + 8;
        if (r.Flag()) // sps_btt_flag
        {
            for (var i = 0; i < 5; i++)
                r.Ue();
        }

        if (r.Flag()) // sps_suco_flag
        {
            r.Ue();
            r.Ue();
        }

        var mmvd = false;
        if (r.Flag()) // sps_admvp_flag
        {
            r.Skip(3); // sps_affine_flag, sps_amvr_flag, sps_dmvr_flag
            mmvd = r.Flag();
            r.Skip(1); // sps_hmvp_flag
        }

        if (r.Flag() && r.Flag()) // sps_eipd_flag, sps_ibc_flag
            r.Ue();
        if (r.Flag()) // sps_cm_init_flag
            r.Skip(1); // sps_adcc_flag
        if (r.Flag()) // sps_iqt_flag
            r.Skip(1); // sps_ats_flag
        r.Skip(1); // sps_addb_flag
        var alf = r.Flag();
        r.Skip(1); // sps_htdf_flag
        var rpl = r.Flag();
        var pocs = r.Flag();
        r.Skip(2); // sps_dquant_flag, sps_dra_flag
        var log2MaxPocLsb = pocs ? (int)r.Ue() + 4 : 0;
        var log2SubGop = 0;
        if (!rpl || !pocs)
        {
            log2SubGop = (int)r.Ue();
            if (log2SubGop == 0)
                r.Ue(); // log2_ref_pic_gap_length
        }

        if (!rpl)
        {
            r.Ue(); // max_num_tid0_ref_pics
        }
        else
        {
            r.Ue(); // sps_max_dec_pic_buffering_minus1
            r.Skip(1); // long_term_ref_pics_flag
            var rpl1SameAsRpl0 = r.Flag();
            var lists = r.Ue();
            for (var i = 0; i < lists; i++)
                SkipRefPicListStruct(ref r);
            if (!rpl1SameAsRpl0)
            {
                lists = r.Ue();
                for (var i = 0; i < lists; i++)
                    SkipRefPicListStruct(ref r);
            }
        }

        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (r.Flag()) // picture_cropping_flag
        {
            cropLeft = (int)r.Ue();
            cropRight = (int)r.Ue();
            cropTop = (int)r.Ue();
            cropBottom = (int)r.Ue();
        }

        if (chroma != 0 && r.Flag()) // chroma_qp_table_present_flag
        {
            var same = r.Flag();
            r.Skip(1); // global_offset_flag
            for (var i = 0; i < (same ? 1 : 2); i++)
            {
                var points = r.Ue() + 1;
                for (var j = 0; j < points; j++)
                {
                    r.Skip(6); // delta_qp_in_val_minus1
                    r.Se(); // delta_qp_out_val
                }
            }
        }

        int sarW = 1, sarH = 1;
        uint units = 0, scale = 0;
        var color = Model.ColorInfo.Unspecified;
        if (r.Flag()) // vui_parameters_present_flag
        {
            if (r.Flag()) // aspect_ratio_info_present_flag
            {
                var idc = (int)r.Read(8);
                if (idc == 255)
                {
                    sarW = (int)r.Read(16);
                    sarH = (int)r.Read(16);
                }
                else if (H264.SampleAspectRatio(idc) is { } sar)
                {
                    (sarW, sarH) = sar;
                }
            }

            if (r.Flag()) // overscan_info_present_flag
                r.Skip(1);
            if (r.Flag()) // video_signal_type_present_flag
            {
                r.Skip(3); // video_format
                var full = r.Flag();
                if (r.Flag()) // colour_description_present_flag
                {
                    var primaries = (int)r.Read(8);
                    var transfer = (int)r.Read(8);
                    var matrix = (int)r.Read(8);
                    color = new Model.ColorInfo(primaries, transfer, matrix, full);
                }
            }

            if (r.Flag()) // chroma_loc_info_present_flag
            {
                r.Ue();
                r.Ue();
            }

            r.Skip(2); // neutral_chroma_indication_flag, field_seq_flag
            if (r.Flag()) // timing_info_present_flag
            {
                units = r.Read(32);
                scale = r.Read(32);
            }
        }

        var subWidthC = chroma is 1 or 2 ? 2 : 1;
        var subHeightC = chroma == 1 ? 2 : 1;
        if (sarW <= 0 || sarH <= 0)
            (sarW, sarH) = (1, 1);
        return new EvcSps
        {
            Id = id,
            ProfileIdc = profile,
            LevelIdc = level,
            ChromaFormatIdc = chroma,
            BitDepthLuma = depthLuma,
            BitDepthChroma = depthChroma,
            MaxWidth = width,
            MaxHeight = height,
            Width = width - subWidthC * (cropLeft + cropRight),
            Height = height - subHeightC * (cropTop + cropBottom),
            Mmvd = mmvd,
            Alf = alf,
            Pocs = pocs,
            Log2MaxPocLsb = log2MaxPocLsb,
            Log2SubGopLength = log2SubGop,
            SarWidth = sarW,
            SarHeight = sarH,
            NumUnitsInTick = units,
            TimeScale = scale,
            Color = color,
        };
    }

    /// <summary>Skips a ref_pic_list_struct() of the SPS.</summary>
    private static void SkipRefPicListStruct(ref BitReader r)
    {
        var entries = r.Ue();
        for (var i = 0; i < entries; i++)
        {
            if (r.Ue() != 0) // delta_poc_st
                r.Skip(1); // strp_entry_sign_flag
        }
    }

    /// <summary>Parses the fields of a picture parameter set NAL unit (with its header) that slice headers depend on.</summary>
    /// <exception cref="InvalidDataException">The PPS is malformed.</exception>
    public static EvcPps ParsePps(ReadOnlySpan<byte> nal)
    {
        try
        {
            var r = new BitReader(nal[2..]);
            var id = (int)r.Ue();
            var spsId = (int)r.Ue();
            r.Ue();
            r.Ue(); // num_ref_idx_default_active_minus1[0..1]
            r.Ue(); // additional_lt_poc_lsb_len
            r.Skip(1); // rpl1_idx_present_flag
            var singleTile = r.Flag();
            int columns = 1, rows = 1;
            if (!singleTile)
            {
                columns = (int)r.Ue() + 1;
                rows = (int)r.Ue() + 1;
                if (!r.Flag()) // uniform_tile_spacing_flag
                {
                    for (var i = 0; i < columns - 1 + rows - 1; i++)
                        r.Ue();
                }

                r.Skip(1); // loop_filter_across_tiles_enabled_flag
                r.Ue(); // tile_offset_len_minus1
            }

            var tileIdLength = (int)r.Ue() + 1;
            var firstTileId = 0;
            if (r.Flag()) // explicit_tile_id_flag
            {
                for (var i = 0; i < rows * columns; i++)
                {
                    var tileId = (int)r.Read(tileIdLength);
                    if (i == 0)
                        firstTileId = tileId;
                }
            }

            if (r.Flag()) // pic_dra_enabled_flag
                r.Skip(5);
            var arbitrary = r.Flag();
            return new EvcPps(id, spsId, singleTile, tileIdLength, arbitrary, firstTileId);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Strings.Error_TruncatedEvcPps, ex);
        }
    }

    /// <summary>What a slice header tells about its picture.</summary>
    /// <param name="FirstSliceOfPicture">The slice starts with the picture's first tile.</param>
    /// <param name="PocLsb">slice_pic_order_cnt_lsb, or -1 when not signalled (IDR, or sps_pocs_flag off).</param>
    public readonly record struct EvcSliceInfo(EvcSps Sps, bool FirstSliceOfPicture, int PocLsb);

    /// <summary>Parses the start of a slice (IDR or non-IDR NAL unit) up to its picture order count LSB.</summary>
    /// <exception cref="InvalidDataException">The slice is malformed or its PPS / SPS is unknown.</exception>
    public static EvcSliceInfo ParseSliceHeader(ReadOnlySpan<byte> nal, IReadOnlyDictionary<int, EvcSps> spss, IReadOnlyDictionary<int, EvcPps> ppss)
    {
        ArgumentNullException.ThrowIfNull(spss);
        ArgumentNullException.ThrowIfNull(ppss);
        try
        {
            var type = NalType(nal);
            var r = new BitReader(nal[2..Math.Min(nal.Length, 64)]);
            var ppsId = (int)r.Ue();
            if (!ppss.TryGetValue(ppsId, out var pps) || !spss.TryGetValue(pps.SpsId, out var sps))
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownEvcPps, ppsId));

            var first = true;
            var singleTileInSlice = true;
            if (!pps.SingleTileInPicture)
            {
                singleTileInSlice = r.Flag();
                first = (int)r.Read(pps.TileIdLength) == pps.FirstTileId;
            }

            if (!singleTileInSlice)
            {
                var arbitrary = pps.ArbitrarySlicePresent && r.Flag();
                if (!arbitrary)
                {
                    r.Skip(pps.TileIdLength); // last_tile_id
                }
                else
                {
                    var tiles = r.Ue() + 1;
                    for (var i = 0; i < tiles; i++)
                        r.Ue(); // delta_tile_id_minus1
                }
            }

            var sliceType = (int)r.Ue();
            if (type == NalIdr)
                r.Skip(1); // no_output_of_prior_pics_flag
            if (sps.Mmvd && sliceType is SliceB or SliceP)
                r.Skip(1); // mmvd_group_enable_flag
            if (sps.Alf)
                SkipSliceAlf(ref r, sps.ChromaFormatIdc);

            var lsb = type != NalIdr && sps.Pocs ? (int)r.Read(sps.Log2MaxPocLsb) : -1;
            return new EvcSliceInfo(sps, first, lsb);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Strings.Error_TruncatedEvcSliceHeader, ex);
        }
    }

    /// <summary>Skips the slice header's ALF parameters (as the reference decoder reads them).</summary>
    private static void SkipSliceAlf(ref BitReader r, int chroma)
    {
        var chromaIdc = 0;
        if (r.Flag()) // slice_alf_enabled_flag
        {
            r.Skip(5 + 1); // slice_alf_luma_aps_id, slice_alf_map_flag
            chromaIdc = (int)r.Read(2);
            if (chromaIdc != 0 && chroma is 1 or 2)
                r.Skip(5); // slice_alf_chroma_aps_id
        }

        if (chroma == 3)
        {
            if (chromaIdc is 1 or 3)
                r.Skip(5 + 1); // slice_alf_chroma_aps_id, slice_alf_chroma_map_flag
            if (chromaIdc is 2 or 3)
                r.Skip(5 + 1); // slice_alf_chroma2_aps_id, slice_alf_chroma2_map_flag
        }
    }

    /// <summary>
    /// Picture order count derivation (ISO/IEC 23094-1 8.3.1, as the reference decoder implements it): from the slice's
    /// LSB when sps_pocs_flag is set, otherwise from the hierarchical sub-GOP position given by the temporal id.
    /// </summary>
    public sealed class PocCounter
    {
        private int _prevPoc;
        private int _prevDocOffset = -1;

        /// <summary>The picture order count of the next picture (in decoding order).</summary>
        public int Next(int nalType, int temporalId, EvcSliceInfo slice)
        {
            var sps = slice.Sps;
            if (nalType == NalIdr)
            {
                _prevPoc = 0;
                _prevDocOffset = -1;
                return 0;
            }

            if (sps.Pocs)
            {
                var max = 1 << sps.Log2MaxPocLsb;
                var prevLsb = _prevPoc & (max - 1);
                var prevMsb = _prevPoc - prevLsb;
                var lsb = slice.PocLsb;
                var msb = lsb < prevLsb && prevLsb - lsb >= max / 2 ? prevMsb + max
                    : lsb > prevLsb && lsb - prevLsb > max / 2 ? prevMsb - max
                    : prevMsb;
                if (temporalId == 0)
                    _prevPoc = msb + lsb;
                return msb + lsb;
            }

            var subGop = 1 << sps.Log2SubGopLength;
            if (temporalId == 0)
            {
                _prevPoc += subGop;
                _prevDocOffset = 0;
                return _prevPoc;
            }

            if (temporalId > (subGop > 1 ? 1 + Log2(subGop - 1) : 0))
                throw new InvalidDataException(Strings.Error_EvcTemporalIdBeyondSubGop);
            var docOffset = (_prevDocOffset + 1) % subGop;
            int expectedTid;
            if (docOffset == 0)
            {
                _prevPoc += subGop;
                expectedTid = 0;
            }
            else
            {
                expectedTid = 1 + Log2(docOffset);
            }

            while (temporalId != expectedTid)
            {
                docOffset = (docOffset + 1) % subGop;
                expectedTid = docOffset == 0 ? 0 : 1 + Log2(docOffset);
            }

            _prevDocOffset = docOffset;
            return _prevPoc + (int)(subGop * ((2.0 * docOffset + 1) / (1 << temporalId) - 2));
        }

        private static int Log2(int x) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);
    }
}
