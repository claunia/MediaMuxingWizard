using System.Globalization;
using System.Numerics;

namespace MMW.Core.Media.Codecs;

/// <summary>Fields of a VVC (H.266) sequence parameter set.</summary>
public sealed record VvcSps
{
    public int Id { get; init; }

    public int MaxSubLayersMinus1 { get; init; }

    /// <summary>general_profile_idc (1 = Main 10, 33 = Main 10 4:4:4 …); -1 when the SPS carries no profile_tier_level.</summary>
    public int ProfileIdc { get; init; } = -1;

    public bool HighTier { get; init; }

    /// <summary>general_level_idc (16 × major + 3 × minor).</summary>
    public int LevelIdc { get; init; }

    public int ChromaFormatIdc { get; init; } = 1;

    public int BitDepth { get; init; } = 8;

    /// <summary>Maximum picture size in luma samples (before cropping).</summary>
    public int MaxWidth { get; init; }

    public int MaxHeight { get; init; }

    /// <summary>Displayed size: the maximum size minus the conformance window.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public int Log2MaxPocLsb { get; init; }

    public int SarWidth { get; init; } = 1;

    public int SarHeight { get; init; } = 1;

    public uint NumUnitsInTick { get; init; }

    public uint TimeScale { get; init; }

    public bool FieldSequence { get; init; }

    public Model.ColorInfo Color { get; init; } = Model.ColorInfo.Unspecified;

    public double FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? (double)TimeScale / NumUnitsInTick : 0;
}

/// <summary>The fields of a VVC picture parameter set needed to find its SPS.</summary>
public sealed record VvcPps(int Id, int SpsId);

/// <summary>VVC (H.266, ISO/IEC 23090-3) bitstream and decoder configuration record ('vvcC', ISO/IEC 14496-15 §11) helpers.</summary>
public static class Vvc
{
    public const int NalTrail = 0;
    public const int NalStsa = 1;
    public const int NalRadl = 2;
    public const int NalRasl = 3;
    public const int NalIdrWithRadl = 7;
    public const int NalIdrNoLp = 8;
    public const int NalCra = 9;
    public const int NalGdr = 10;
    public const int NalOpi = 12;
    public const int NalDci = 13;
    public const int NalVps = 14;
    public const int NalSps = 15;
    public const int NalPps = 16;
    public const int NalPrefixAps = 17;
    public const int NalSuffixAps = 18;
    public const int NalPictureHeader = 19;
    public const int NalAud = 20;
    public const int NalEos = 21;
    public const int NalEob = 22;
    public const int NalSeiPrefix = 23;
    public const int NalSeiSuffix = 24;
    public const int NalFiller = 25;

    /// <summary>nal_unit_type of a VVC NAL unit (second header byte, top 5 bits), or -1.</summary>
    public static int NalType(ReadOnlySpan<byte> nal) => nal.Length > 1 ? nal[1] >> 3 : -1;

    /// <summary>TemporalId of a NAL unit.</summary>
    public static int TemporalId(ReadOnlySpan<byte> nal) => nal.Length > 1 ? (nal[1] & 7) - 1 : 0;

    public static bool IsVcl(int type) => type is >= 0 and <= 11;

    /// <summary>Intra random access point (IDR, CRA); GDR pictures are random access points as well but not IRAP.</summary>
    public static bool IsIrap(int type) => type is >= NalIdrWithRadl and <= NalCra;

    public static bool IsIdr(int type) => type is NalIdrWithRadl or NalIdrNoLp;

    public static bool IsRasl(int type) => type == NalRasl;

    /// <summary>Parameter sets and other non-VCL units that 'vvcC' may carry (VPS, SPS, PPS, APS, DCI, OPI, SEI).</summary>
    public static bool IsParameterSet(int type) => type is NalVps or NalSps or NalPps;

    /// <summary>True when the slice in a VCL NAL unit starts a picture: its picture header is in the slice header.</summary>
    /// <remarks>
    /// A picture either starts with a picture header NAL unit or with a slice whose sh_picture_header_in_slice_header_flag
    /// (the first bit of the slice header) is set.
    /// </remarks>
    public static bool HasPictureHeaderInSlice(ReadOnlySpan<byte> nal) => nal.Length > 2 && (nal[2] & 0x80) != 0;

    /// <summary>Name of a general_profile_idc (H.266 Table A.1).</summary>
    public static string ProfileName(int profileIdc) => profileIdc switch
    {
        1 => "Main 10",
        65 => "Main 10 Still Picture",
        17 => "Multilayer Main 10",
        81 => "Multilayer Main 10 Still Picture",
        33 => "Main 10 4:4:4",
        97 => "Main 10 4:4:4 Still Picture",
        49 => "Multilayer Main 10 4:4:4",
        113 => "Multilayer Main 10 4:4:4 Still Picture",
        2 => "Main 12",
        34 => "Main 12 4:4:4",
        35 => "Main 16 4:4:4",
        10 => "Main 12 Intra",
        42 => "Main 12 4:4:4 Intra",
        43 => "Main 16 4:4:4 Intra",
        66 => "Main 12 Still Picture",
        98 => "Main 12 4:4:4 Still Picture",
        99 => "Main 16 4:4:4 Still Picture",
        _ => "Profile " + profileIdc.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"Main 10@L5.1" (with " High" for the high tier); empty when <paramref name="profileIdc"/> is unknown (-1).</summary>
    public static string ProfileLevel(int profileIdc, int levelIdc, bool highTier)
    {
        if (profileIdc < 0)
            return string.Empty;
        var text = ProfileName(profileIdc);
        if (levelIdc > 0)
            text += levelIdc == 255 ? "@L15.5" : string.Create(CultureInfo.InvariantCulture, $"@L{levelIdc / 16}.{levelIdc % 16 / 3}");
        return highTier ? text + " High" : text;
    }

    /// <summary>Profile and level of a 'vvcC' record (without the FullBox header), e.g. "Main 10@L4.1"; empty when absent.</summary>
    public static string ProfileLevel(ReadOnlySpan<byte> vvcC)
    {
        try
        {
            var c = ParseVvcC(vvcC);
            return ProfileLevel(c.ProfileIdc, c.LevelIdc, c.HighTier);
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }

    /// <summary>A parsed 'vvcC' record.</summary>
    /// <param name="LengthSize">NAL unit length field size of the samples.</param>
    /// <param name="ProfileIdc">general_profile_idc, or -1 when the record has no profile_tier_level.</param>
    public sealed record VvcConfig(
        int LengthSize, int ProfileIdc, bool HighTier, int LevelIdc, int ChromaFormatIdc, int BitDepth, int MaxWidth, int MaxHeight,
        IReadOnlyList<(int Type, bool Complete, byte[] Nal)> Nals);

    /// <summary>Parses a VVCDecoderConfigurationRecord (the 'vvcC' payload after its FullBox header, or Matroska CodecPrivate).</summary>
    /// <exception cref="InvalidDataException">The record is truncated.</exception>
    public static VvcConfig ParseVvcC(ReadOnlySpan<byte> vvcC)
    {
        try
        {
            var h = ParseHeader(vvcC);
            var pos = h.ArraysOffset;
            var nals = new List<(int, bool, byte[])>();
            var arrays = vvcC[pos++];
            for (var a = 0; a < arrays; a++)
            {
                var head = vvcC[pos++];
                var type = head & 0x1F;
                var count = 1;
                if (type is not (NalDci or NalOpi))
                {
                    count = (vvcC[pos] << 8) | vvcC[pos + 1];
                    pos += 2;
                }

                for (var i = 0; i < count; i++)
                {
                    var length = (vvcC[pos] << 8) | vvcC[pos + 1];
                    pos += 2;
                    nals.Add((type, (head & 0x80) != 0, vvcC.Slice(pos, length).ToArray()));
                    pos += length;
                }
            }

            return h.Config with { Nals = nals };
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidDataException("Truncated vvcC record.", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Truncated vvcC record.", ex);
        }
    }

    /// <summary>
    /// Copy of a 'vvcC' record whose arrays have array_completeness set to <paramref name="complete"/>: set for 'vvc1'
    /// (all parameter sets in the record), clear for 'vvi1' (more of them in the samples).
    /// </summary>
    public static byte[] MarkArraysComplete(byte[] vvcC, bool complete)
    {
        ArgumentNullException.ThrowIfNull(vvcC);
        var copy = (byte[])vvcC.Clone();
        try
        {
            var pos = ParseHeader(copy).ArraysOffset;
            var arrays = copy[pos++];
            for (var a = 0; a < arrays; a++)
            {
                copy[pos] = (byte)(complete ? copy[pos] | 0x80 : copy[pos] & 0x7F);
                var type = copy[pos++] & 0x1F;
                var count = 1;
                if (type is not (NalDci or NalOpi))
                {
                    count = (copy[pos] << 8) | copy[pos + 1];
                    pos += 2;
                }

                for (var i = 0; i < count; i++)
                    pos += 2 + ((copy[pos] << 8) | copy[pos + 1]);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
        }

        return copy;
    }

    /// <summary>The fields before the NAL unit arrays and the offset of num_of_arrays.</summary>
    private static (VvcConfig Config, int ArraysOffset) ParseHeader(ReadOnlySpan<byte> vvcC)
    {
        var pos = 0;
        var first = vvcC[pos++];
        var lengthSize = ((first >> 1) & 3) + 1;
        int profile = -1, level = 0, chroma = 1, bitDepth = 8, width = 0, height = 0;
        var highTier = false;
        if ((first & 1) != 0) // ptl_present_flag
        {
            var w = (vvcC[pos] << 8) | vvcC[pos + 1];
            pos += 2;
            var numSublayers = (w >> 4) & 7;
            chroma = w & 3;
            bitDepth = (vvcC[pos++] >> 5) + 8;

            // VvcPTLRecord
            var numBytesConstraintInfo = vvcC[pos++] & 0x3F;
            profile = vvcC[pos] >> 1;
            highTier = (vvcC[pos++] & 1) != 0;
            level = vvcC[pos++];
            pos += numBytesConstraintInfo; // frame-only, multilayer flags and the general constraints
            if (numSublayers > 1)
            {
                var present = vvcC[pos++];
                for (var i = numSublayers - 2; i >= 0; i--)
                {
                    if ((present >> i & 1) != 0)
                        pos++; // sublayer_level_idc
                }
            }

            var numSubProfiles = vvcC[pos++];
            pos += 4 * numSubProfiles;
            width = (vvcC[pos] << 8) | vvcC[pos + 1];
            height = (vvcC[pos + 2] << 8) | vvcC[pos + 3];
            pos += 6; // and avg_frame_rate
        }

        return (new VvcConfig(lengthSize, profile, highTier, level, chroma, bitDepth, width, height, []), pos);
    }

    /// <summary>
    /// Builds a VVCDecoderConfigurationRecord (without the FullBox header) for 4-byte length-prefixed samples from
    /// parameter sets; the profile_tier_level comes from the first SPS that has one.
    /// </summary>
    /// <param name="complete">array_completeness: true when the samples carry no parameter sets of their own ('vvc1').</param>
    public static byte[] BuildVvcC(IReadOnlyList<byte[]> vps, IReadOnlyList<byte[]> sps, IReadOnlyList<byte[]> pps, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(vps);
        ArgumentNullException.ThrowIfNull(sps);
        ArgumentNullException.ThrowIfNull(pps);
        var o = new List<byte>();
        byte[]? ptl = null;
        VvcSps? info = null;
        foreach (var s in sps)
        {
            if (TryProfileTierLevel(s, out var bytes, out var parsed))
            {
                ptl = bytes;
                info = parsed;
                break;
            }
        }

        o.Add((byte)(0xF8 | (3 << 1) | (ptl is null ? 0 : 1))); // reserved '11111', LengthSizeMinusOne = 3, ptl_present_flag
        if (ptl is not null && info is not null)
        {
            var numSublayers = info.MaxSubLayersMinus1 + 1;
            // ols_idx 0, num_sublayers, constant_frame_rate 1 (unknown), chroma_format_idc
            var w = (numSublayers << 4) | (1 << 2) | info.ChromaFormatIdc;
            o.Add((byte)(w >> 8));
            o.Add((byte)w);
            o.Add((byte)(((info.BitDepth - 8) << 5) | 0x1F));
            o.AddRange(ptl);
            o.Add((byte)(info.MaxWidth >> 8));
            o.Add((byte)info.MaxWidth);
            o.Add((byte)(info.MaxHeight >> 8));
            o.Add((byte)info.MaxHeight);
            o.Add(0); // avg_frame_rate: unspecified
            o.Add(0);
        }

        var arrays = new[] { (NalVps, vps), (NalSps, sps), (NalPps, pps) }.Where(a => a.Item2.Count > 0).ToList();
        o.Add((byte)arrays.Count);
        foreach (var (type, nals) in arrays)
        {
            o.Add((byte)((complete ? 0x80 : 0) | type));
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

    /// <summary>
    /// The VvcPTLRecord bytes of an SPS (from num_bytes_constraint_info to the sub-profiles) and the parsed SPS; false
    /// when the SPS has no profile_tier_level or cannot be parsed.
    /// </summary>
    private static bool TryProfileTierLevel(byte[] nal, out byte[] record, out VvcSps sps)
    {
        record = [];
        sps = null!;
        try
        {
            sps = ParseSps(nal);
            if (sps.ProfileIdc < 0)
                return false;

            var rbsp = NalUnits.ToRbsp(nal);
            var r = new BitReader(rbsp);
            r.Skip(16 + 4 + 4 + 3 + 2 + 2 + 1); // header, ids, max sublayers, chroma, CTU size, ptl_dpb_hrd_params_present_flag
            var profile = r.Read(7);
            var tier = r.Read(1);
            var level = r.Read(8);
            var constraintStart = r.Position; // ptl_frame_only_constraint_flag
            r.Skip(2);
            SkipGeneralConstraintsInfo(ref r);
            var constraintBits = r.Position - constraintStart;
            var numBytes = (int)((constraintBits + 7) / 8);

            var o = new List<byte> { (byte)(numBytes & 0x3F), (byte)((profile << 1) | tier), (byte)level };
            var copy = new BitReader(rbsp);
            copy.Skip(constraintStart);
            var w = new BitWriter();
            for (var i = 0L; i < numBytes * 8L; i++)
                w.Write(i < constraintBits ? copy.Read(1) : 0, 1);
            o.AddRange(w.ToArray());

            var sub = sps.MaxSubLayersMinus1;
            var presentBits = 0;
            for (var i = sub - 1; i >= 0; i--)
            {
                if (r.Flag())
                    presentBits |= 1 << i;
            }

            r.ByteAlign();
            if (sub > 0)
                o.Add((byte)presentBits);
            for (var i = sub - 1; i >= 0; i--)
            {
                if ((presentBits >> i & 1) != 0)
                    o.Add((byte)r.Read(8));
            }

            var numSubProfiles = (int)r.Read(8);
            o.Add((byte)numSubProfiles);
            for (var i = 0; i < numSubProfiles; i++)
            {
                var v = r.Read(32);
                o.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            }

            record = [.. o];
            return true;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Skips general_constraints_info() (gci_present_flag … gci_alignment_zero_bit).</summary>
    private static void SkipGeneralConstraintsInfo(ref BitReader r)
    {
        if (r.Flag()) // gci_present_flag
        {
            r.Skip(71); // the constraint flags and fields of version 1
            var additional = r.Read(8);
            r.Skip(additional);
        }

        r.ByteAlign();
    }

    /// <summary>Parses a sequence parameter set NAL unit (with its 2-byte header) up to and including the VUI.</summary>
    /// <exception cref="InvalidDataException">The SPS is malformed.</exception>
    public static VvcSps ParseSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.ToRbsp(nal);
        var r = new BitReader(rbsp);
        r.Skip(16);
        var id = (int)r.Read(4);
        var vpsId = r.Read(4);
        var maxSub = (int)r.Read(3);
        var chroma = (int)r.Read(2);
        var ctbLog2 = (int)r.Read(2) + 5;
        var ctbSize = 1 << ctbLog2;
        var ptlDpbHrd = r.Flag();
        int profile = -1, level = 0;
        var highTier = false;
        if (ptlDpbHrd)
        {
            profile = (int)r.Read(7);
            highTier = r.Flag();
            level = (int)r.Read(8);
            r.Skip(2); // ptl_frame_only_constraint_flag, ptl_multilayer_enabled_flag
            SkipGeneralConstraintsInfo(ref r);
            var present = new bool[Math.Max(maxSub, 1)];
            for (var i = maxSub - 1; i >= 0; i--)
                present[i] = r.Flag();
            r.ByteAlign();
            for (var i = maxSub - 1; i >= 0; i--)
            {
                if (present[i])
                    r.Skip(8);
            }

            var numSubProfiles = r.Read(8);
            r.Skip(32 * numSubProfiles);
        }

        r.Skip(1); // sps_gdr_enabled_flag
        if (r.Flag()) // sps_ref_pic_resampling_enabled_flag
            r.Skip(1); // sps_res_change_in_clvs_allowed_flag
        var width = (int)r.Ue();
        var height = (int)r.Ue();
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (r.Flag()) // sps_conformance_window_flag
        {
            cropLeft = (int)r.Ue();
            cropRight = (int)r.Ue();
            cropTop = (int)r.Ue();
            cropBottom = (int)r.Ue();
        }

        var subWidthC = chroma is 1 or 2 ? 2 : 1;
        var subHeightC = chroma == 1 ? 2 : 1;
        var widthCtbs = (width + ctbSize - 1) >> ctbLog2;
        var heightCtbs = (height + ctbSize - 1) >> ctbLog2;

        if (r.Flag()) // sps_subpic_info_present_flag
        {
            var numSubpicsMinus1 = (int)r.Ue();
            bool independent = true, sameSize = false;
            if (numSubpicsMinus1 > 0)
            {
                independent = r.Flag();
                sameSize = r.Flag();
            }

            if (numSubpicsMinus1 > 0)
            {
                var wlen = CeilLog2(widthCtbs);
                var hlen = CeilLog2(heightCtbs);
                for (var i = 0; i <= numSubpicsMinus1; i++)
                {
                    if (!sameSize || i == 0)
                    {
                        if (i > 0 && width > ctbSize)
                            r.Skip(wlen); // sps_subpic_ctu_top_left_x
                        if (i > 0 && height > ctbSize)
                            r.Skip(hlen); // sps_subpic_ctu_top_left_y
                        if (i < numSubpicsMinus1 && width > ctbSize)
                            r.Skip(wlen); // sps_subpic_width_minus1
                        if (i < numSubpicsMinus1 && height > ctbSize)
                            r.Skip(hlen); // sps_subpic_height_minus1
                    }

                    if (!independent)
                        r.Skip(2); // sps_subpic_treated_as_pic_flag, sps_loop_filter_across_subpic_enabled_flag
                }
            }

            var idLen = (int)r.Ue() + 1;
            if (r.Flag() && r.Flag()) // sps_subpic_id_mapping_explicitly_signalled_flag, sps_subpic_id_mapping_present_flag
                r.Skip((long)idLen * (numSubpicsMinus1 + 1));
        }

        var bitDepth = (int)r.Ue() + 8;
        r.Skip(2); // sps_entropy_coding_sync_enabled_flag, sps_entry_point_offsets_present_flag
        var log2MaxPocLsb = (int)r.Read(4) + 4;
        if (r.Flag()) // sps_poc_msb_cycle_flag
            r.Ue();
        r.Skip(8 * r.Read(2)); // sps_num_extra_ph_bytes and their flags
        r.Skip(8 * r.Read(2)); // sps_num_extra_sh_bytes and their flags
        if (ptlDpbHrd)
        {
            var sublayerDpb = maxSub > 0 && r.Flag();
            for (var i = sublayerDpb ? 0 : maxSub; i <= maxSub; i++)
            {
                r.Ue();
                r.Ue();
                r.Ue();
            }
        }

        var minCbLog2 = (int)r.Ue() + 2;
        r.Skip(1); // sps_partition_constraints_override_enabled_flag
        r.Ue(); // sps_log2_diff_min_qt_min_cb_intra_slice_luma
        if (r.Ue() != 0) // sps_max_mtt_hierarchy_depth_intra_slice_luma
        {
            r.Ue();
            r.Ue();
        }

        if (chroma != 0 && r.Flag()) // sps_qtbtt_dual_tree_intra_flag
        {
            r.Ue();
            if (r.Ue() != 0)
            {
                r.Ue();
                r.Ue();
            }
        }

        r.Ue(); // sps_log2_diff_min_qt_min_cb_inter_slice
        if (r.Ue() != 0) // sps_max_mtt_hierarchy_depth_inter_slice
        {
            r.Ue();
            r.Ue();
        }

        var maxLumaTransform64 = ctbSize > 32 && r.Flag();
        var transformSkip = r.Flag();
        if (transformSkip)
        {
            r.Ue();
            r.Skip(1); // sps_bdpcm_enabled_flag
        }

        if (r.Flag()) // sps_mts_enabled_flag
            r.Skip(2);
        var lfnst = r.Flag();
        if (chroma != 0)
        {
            var jointCbCr = r.Flag();
            var sameQpTable = r.Flag();
            var tables = sameQpTable ? 1 : jointCbCr ? 3 : 2;
            for (var i = 0; i < tables; i++)
            {
                r.Se(); // sps_qp_table_start_minus26
                var points = r.Ue() + 1;
                for (var j = 0; j < points; j++)
                {
                    r.Ue();
                    r.Ue();
                }
            }
        }

        r.Skip(1); // sps_sao_enabled_flag
        if (r.Flag() && chroma != 0) // sps_alf_enabled_flag
            r.Skip(1); // sps_ccalf_enabled_flag
        r.Skip(1); // sps_lmcs_enabled_flag
        var weightedPred = r.Flag();
        var weightedBipred = r.Flag();
        var longTermRefs = r.Flag();
        var interLayer = vpsId > 0 && r.Flag();
        r.Skip(1); // sps_idr_rpl_present_flag
        var rpl1SameAsRpl0 = r.Flag();
        for (var i = 0; i < (rpl1SameAsRpl0 ? 1 : 2); i++)
        {
            var lists = r.Ue();
            for (var j = 0; j < lists; j++)
                SkipRefPicListStruct(ref r, longTermRefs, interLayer, weightedPred || weightedBipred, log2MaxPocLsb);
        }

        r.Skip(1); // sps_ref_wraparound_enabled_flag
        if (r.Flag()) // sps_temporal_mvp_enabled_flag
            r.Skip(1); // sps_sbtmvp_enabled_flag
        var amvr = r.Flag();
        if (r.Flag()) // sps_bdof_enabled_flag
            r.Skip(1);
        r.Skip(1); // sps_smvd_enabled_flag
        if (r.Flag()) // sps_dmvr_enabled_flag
            r.Skip(1);
        if (r.Flag()) // sps_mmvd_enabled_flag
            r.Skip(1);
        var maxMergeCand = 6 - (int)r.Ue();
        r.Skip(1); // sps_sbt_enabled_flag
        if (r.Flag()) // sps_affine_enabled_flag
        {
            r.Ue();
            r.Skip(1); // sps_6param_affine_enabled_flag
            if (amvr)
                r.Skip(1);
            if (r.Flag()) // sps_affine_prof_enabled_flag
                r.Skip(1);
        }

        r.Skip(2); // sps_bcw_enabled_flag, sps_ciip_enabled_flag
        if (maxMergeCand >= 2 && r.Flag() && maxMergeCand >= 3) // sps_gpm_enabled_flag
            r.Ue();
        r.Ue(); // sps_log2_parallel_merge_level_minus2
        r.Skip(3); // sps_isp_enabled_flag, sps_mrl_enabled_flag, sps_mip_enabled_flag
        if (chroma != 0)
            r.Skip(1); // sps_cclm_enabled_flag
        if (chroma == 1)
            r.Skip(2); // sps_chroma_horizontal/vertical_collocated_flag
        var palette = r.Flag();
        var act = chroma == 3 && !maxLumaTransform64 && r.Flag();
        if (transformSkip || palette)
            r.Ue(); // sps_min_qp_prime_ts
        if (r.Flag()) // sps_ibc_enabled_flag
            r.Ue();
        if (r.Flag()) // sps_ladf_enabled_flag
        {
            var intervals = (int)r.Read(2) + 1;
            r.Se();
            for (var i = 0; i < intervals; i++)
            {
                r.Se();
                r.Ue();
            }
        }

        var explicitScaling = r.Flag();
        if (lfnst && explicitScaling)
            r.Skip(1);
        if (act && explicitScaling && r.Flag()) // sps_scaling_matrix_for_alternative_colour_space_disabled_flag
            r.Skip(1); // sps_scaling_matrix_designated_colour_space_flag
        r.Skip(2); // sps_dep_quant_enabled_flag, sps_sign_data_hiding_enabled_flag
        if (r.Flag() && r.Flag()) // sps_virtual_boundaries_enabled_flag, sps_virtual_boundaries_present_flag
        {
            var ver = r.Ue();
            for (var i = 0; i < ver; i++)
                r.Ue();
            var hor = r.Ue();
            for (var i = 0; i < hor; i++)
                r.Ue();
        }

        uint numUnitsInTick = 0, timeScale = 0;
        if (ptlDpbHrd && r.Flag()) // sps_timing_hrd_params_present_flag
        {
            numUnitsInTick = r.Read(32);
            timeScale = r.Read(32);
            var nalHrd = r.Flag();
            var vclHrd = r.Flag();
            var duHrd = false;
            uint cpbCnt = 0;
            if (nalHrd || vclHrd)
            {
                r.Skip(1); // general_same_pic_timing_in_all_ols_flag
                duHrd = r.Flag();
                if (duHrd)
                    r.Skip(8);
                r.Skip(8); // bit_rate_scale, cpb_size_scale
                if (duHrd)
                    r.Skip(4);
                cpbCnt = r.Ue();
            }

            var sublayerCpb = maxSub > 0 && r.Flag();
            for (var i = sublayerCpb ? 0 : maxSub; i <= maxSub; i++)
            {
                var fixedGeneral = r.Flag();
                var fixedWithin = fixedGeneral || r.Flag();
                if (fixedWithin)
                    r.Ue(); // elemental_duration_in_tc_minus1
                else if ((nalHrd || vclHrd) && cpbCnt == 0)
                    r.Skip(1); // low_delay_hrd_flag
                for (var k = (nalHrd ? 1 : 0) + (vclHrd ? 1 : 0); k > 0; k--)
                {
                    for (var j = 0; j <= cpbCnt; j++)
                    {
                        r.Ue();
                        r.Ue();
                        if (duHrd)
                        {
                            r.Ue();
                            r.Ue();
                        }

                        r.Skip(1); // cbr_flag
                    }
                }
            }
        }

        var fieldSeq = r.Flag();
        var color = Model.ColorInfo.Unspecified;
        int sarW = 1, sarH = 1;
        if (r.Flag()) // sps_vui_parameters_present_flag
        {
            r.Ue(); // sps_vui_payload_size_minus1
            r.ByteAlign();
            r.Skip(4); // progressive, interlaced, non-packed, non-projected
            if (r.Flag()) // vui_aspect_ratio_info_present_flag
            {
                r.Skip(1); // vui_aspect_ratio_constant_flag
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

            if (r.Flag()) // vui_overscan_info_present_flag
                r.Skip(1);
            if (r.Flag()) // vui_colour_description_present_flag
            {
                var primaries = (int)r.Read(8);
                var transfer = (int)r.Read(8);
                var matrix = (int)r.Read(8);
                color = new Model.ColorInfo(primaries, transfer, matrix, r.Flag());
            }
        }

        if (sarW <= 0 || sarH <= 0)
            (sarW, sarH) = (1, 1);
        return new VvcSps
        {
            Id = id,
            MaxSubLayersMinus1 = maxSub,
            ProfileIdc = profile,
            HighTier = highTier,
            LevelIdc = level,
            ChromaFormatIdc = chroma,
            BitDepth = bitDepth,
            MaxWidth = width,
            MaxHeight = height,
            Width = width - subWidthC * (cropLeft + cropRight),
            Height = height - subHeightC * (cropTop + cropBottom),
            Log2MaxPocLsb = log2MaxPocLsb,
            SarWidth = sarW,
            SarHeight = sarH,
            NumUnitsInTick = numUnitsInTick,
            TimeScale = timeScale,
            FieldSequence = fieldSeq,
            Color = color,
        };
    }

    /// <summary>Skips a ref_pic_list_struct() of the SPS.</summary>
    private static void SkipRefPicListStruct(ref BitReader r, bool longTermRefs, bool interLayer, bool weighted, int log2MaxPocLsb)
    {
        var entries = r.Ue();
        var ltrpInHeader = longTermRefs && entries > 0 && r.Flag();
        for (var i = 0; i < entries; i++)
        {
            if (interLayer && r.Flag()) // inter_layer_ref_pic_flag
            {
                r.Ue(); // ilrp_idx
                continue;
            }

            var shortTerm = !longTermRefs || r.Flag();
            if (shortTerm)
            {
                var abs = r.Ue();
                if ((weighted && i != 0 ? abs : abs + 1) > 0)
                    r.Skip(1); // strp_entry_sign_flag
            }
            else if (!ltrpInHeader)
            {
                r.Skip(log2MaxPocLsb); // rpls_poc_lsb_lt
            }
        }
    }

    /// <summary>Parses the identifiers of a picture parameter set NAL unit (with its header).</summary>
    public static VvcPps ParsePps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 4)
            throw new InvalidDataException("Truncated VVC PPS.");
        var r = new BitReader(nal[2..4]); // no emulation prevention can occur in the first 10 bits
        return new VvcPps((int)r.Read(6), (int)r.Read(4));
    }

    /// <summary>
    /// The picture order count LSB of a picture, from its picture header NAL unit or from a slice that carries the picture
    /// header (sh_picture_header_in_slice_header_flag), with the SPS's LSB size and ph_non_ref_pic_flag.
    /// </summary>
    /// <exception cref="InvalidDataException">The NAL unit has no picture header, or its PPS / SPS is unknown.</exception>
    public static (int Lsb, int Log2MaxPocLsb, bool NonReference) ParsePocLsb(
        ReadOnlySpan<byte> nal, IReadOnlyDictionary<int, VvcSps> spss, IReadOnlyDictionary<int, VvcPps> ppss)
    {
        ArgumentNullException.ThrowIfNull(spss);
        ArgumentNullException.ThrowIfNull(ppss);
        var type = NalType(nal);
        var rbsp = NalUnits.ToRbsp(nal[..Math.Min(nal.Length, 64)]);
        var r = new BitReader(rbsp);
        r.Skip(16);
        if (IsVcl(type) && !r.Flag()) // sh_picture_header_in_slice_header_flag
            throw new InvalidDataException("The slice has no picture header.");
        if (type != NalPictureHeader && !IsVcl(type))
            throw new InvalidDataException("Not a picture header or slice.");

        // picture_header_structure()
        var gdrOrIrap = r.Flag();
        var nonRef = r.Flag();
        if (gdrOrIrap)
            r.Skip(1); // ph_gdr_pic_flag
        if (r.Flag()) // ph_inter_slice_allowed_flag
            r.Skip(1); // ph_intra_slice_allowed_flag
        var ppsId = (int)r.Ue();
        if (!ppss.TryGetValue(ppsId, out var pps) || !spss.TryGetValue(pps.SpsId, out var sps))
            throw new InvalidDataException($"Unknown VVC PPS {ppsId}.");
        return ((int)r.Read(sps.Log2MaxPocLsb), sps.Log2MaxPocLsb, nonRef);
    }

    private static int CeilLog2(int x) => x <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(x - 1));
}
