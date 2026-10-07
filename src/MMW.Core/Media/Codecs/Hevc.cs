using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>Fields of an HEVC sequence parameter set.</summary>
public sealed record HevcSps
{
    public int Id { get; init; }

    public int MaxSubLayersMinus1 { get; init; }

    public bool TemporalIdNesting { get; init; }

    /// <summary>The 12 bytes of general_profile_tier_level (profile space … level_idc).</summary>
    public byte[] GeneralProfileTierLevel { get; init; } = new byte[12];

    public int ChromaFormatIdc { get; init; } = 1;

    public bool SeparateColourPlane { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int BitDepthLuma { get; init; } = 8;

    public int BitDepthChroma { get; init; } = 8;

    public int Log2MaxPocLsb { get; init; }

    public int SarWidth { get; init; } = 1;

    public int SarHeight { get; init; } = 1;

    public uint NumUnitsInTick { get; init; }

    public uint TimeScale { get; init; }

    public Model.ColorInfo Color { get; init; } = Model.ColorInfo.Unspecified;

    public double FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? (double)TimeScale / NumUnitsInTick : 0;
}

/// <summary>Fields of an HEVC picture parameter set needed to parse slice headers.</summary>
public sealed record HevcPps(int Id, int SpsId, bool DependentSliceSegmentsEnabled, bool OutputFlagPresent, int NumExtraSliceHeaderBits);

/// <summary>Parsing of HEVC parameter sets and slice headers, and hvcC records.</summary>
public static class Hevc
{
    public const int NalVps = 32;
    public const int NalSps = 33;
    public const int NalPps = 34;
    public const int NalAud = 35;
    public const int NalEos = 36;
    public const int NalEob = 37;
    public const int NalFiller = 38;
    public const int NalSeiPrefix = 39;
    public const int NalSeiSuffix = 40;

    private static readonly (int W, int H)[] s_sar =
    [
        (0, 0), (1, 1), (12, 11), (10, 11), (16, 11), (40, 33), (24, 11), (20, 11), (32, 11), (80, 33), (18, 11), (15, 11), (64, 33), (160, 99), (4, 3), (3, 2), (2, 1),
    ];

    public static bool IsVcl(int type) => type is >= 0 and < 32;

    public static bool IsIrap(int type) => type is >= 16 and <= 23;

    public static bool IsIdr(int type) => type is 19 or 20;

    public static bool IsRasl(int type) => type is 8 or 9;

    /// <summary>Temporal ID of a NAL unit.</summary>
    public static int TemporalId(ReadOnlySpan<byte> nal) => nal.Length >= 2 ? (nal[1] & 7) - 1 : 0;

    /// <summary>Parses an SPS NAL unit (2-byte header included).</summary>
    public static HevcSps ParseSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.ToRbsp(nal[2..]);
        var r = new BitReader(rbsp);
        r.Skip(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = (int)r.Read(3);
        var nesting = r.Flag();
        var ptl = new byte[12];
        for (var i = 0; i < 12; i++)
            ptl[i] = (byte)r.Read(8);
        SkipSubLayers(ref r, maxSubLayersMinus1);

        var id = (int)r.Ue();
        var chroma = (int)r.Ue();
        var separate = chroma == 3 && r.Flag();
        var width = (int)r.Ue();
        var height = (int)r.Ue();
        if (r.Flag())
        {
            var subW = chroma is 1 or 2 && !separate ? 2 : 1;
            var subH = chroma == 1 && !separate ? 2 : 1;
            var left = (int)r.Ue();
            var right = (int)r.Ue();
            var top = (int)r.Ue();
            var bottom = (int)r.Ue();
            width -= subW * (left + right);
            height -= subH * (top + bottom);
        }

        var depthLuma = (int)r.Ue() + 8;
        var depthChroma = (int)r.Ue() + 8;
        var log2MaxPocLsb = (int)r.Ue() + 4;

        int sarW = 1, sarH = 1;
        uint units = 0, scale = 0;
        var color = Model.ColorInfo.Unspecified;
        try
        {
            var orderingAll = r.Flag();
            for (var i = orderingAll ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
            {
                r.Ue();
                r.Ue();
                r.Ue();
            }

            r.Ue(); // log2_min_luma_coding_block_size_minus3
            r.Ue();
            r.Ue();
            r.Ue();
            r.Ue();
            r.Ue();
            if (r.Flag() && r.Flag())
                SkipScalingListData(ref r);
            r.Skip(2); // amp, sao
            if (r.Flag())
            {
                r.Skip(8);
                r.Ue();
                r.Ue();
                r.Skip(1);
            }

            var numStRps = (int)r.Ue();
            var numDeltaPocs = new int[numStRps];
            for (var i = 0; i < numStRps; i++)
                numDeltaPocs[i] = SkipShortTermRefPicSet(ref r, i, numDeltaPocs);
            if (r.Flag())
            {
                var n = (int)r.Ue();
                for (var i = 0; i < n; i++)
                {
                    r.Skip(log2MaxPocLsb);
                    r.Skip(1);
                }
            }

            r.Skip(2); // temporal mvp, strong intra smoothing
            if (r.Flag())
                ParseVui(ref r, out sarW, out sarH, out units, out scale, out color);
        }
        catch (InvalidDataException)
        {
            // Truncated or unusual SPS tail: the fields needed for muxing were read above.
        }

        return new HevcSps
        {
            Id = id,
            MaxSubLayersMinus1 = maxSubLayersMinus1,
            TemporalIdNesting = nesting,
            GeneralProfileTierLevel = ptl,
            ChromaFormatIdc = chroma,
            SeparateColourPlane = separate,
            Width = width,
            Height = height,
            BitDepthLuma = depthLuma,
            BitDepthChroma = depthChroma,
            Log2MaxPocLsb = log2MaxPocLsb,
            SarWidth = sarW,
            SarHeight = sarH,
            NumUnitsInTick = units,
            TimeScale = scale,
            Color = color,
        };
    }

    private static void SkipSubLayers(ref BitReader r, int maxSubLayersMinus1)
    {
        var profilePresent = new bool[8];
        var levelPresent = new bool[8];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Flag();
            levelPresent[i] = r.Flag();
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (var i = maxSubLayersMinus1; i < 8; i++)
                r.Skip(2);
        }

        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i])
                r.Skip(88);
            if (levelPresent[i])
                r.Skip(8);
        }
    }

    private static void SkipScalingListData(ref BitReader r)
    {
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            for (var matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                if (!r.Flag())
                {
                    r.Ue();
                }
                else
                {
                    var coefNum = Math.Min(64, 1 << (4 + (sizeId << 1)));
                    if (sizeId > 1)
                        r.Se();
                    for (var i = 0; i < coefNum; i++)
                        r.Se();
                }
            }
        }
    }

    private static int SkipShortTermRefPicSet(ref BitReader r, int index, int[] numDeltaPocs)
    {
        if (index != 0 && r.Flag())
        {
            r.Skip(1); // delta_rps_sign
            r.Ue(); // abs_delta_rps_minus1
            var refIdx = index - 1;
            var count = 0;
            for (var j = 0; j <= numDeltaPocs[refIdx]; j++)
            {
                var used = r.Flag();
                var useDelta = used || r.Flag();
                if (useDelta)
                    count++;
            }

            return count;
        }

        var negative = (int)r.Ue();
        var positive = (int)r.Ue();
        for (var i = 0; i < negative + positive; i++)
        {
            r.Ue();
            r.Skip(1);
        }

        return negative + positive;
    }

    private static void ParseVui(ref BitReader r, out int sarW, out int sarH, out uint units, out uint scale, out Model.ColorInfo color)
    {
        sarW = 1;
        sarH = 1;
        units = 0;
        scale = 0;
        color = Model.ColorInfo.Unspecified;
        if (r.Flag())
        {
            var idc = (int)r.Read(8);
            if (idc == 255)
            {
                sarW = (int)r.Read(16);
                sarH = (int)r.Read(16);
            }
            else if (idc > 0 && idc < s_sar.Length)
            {
                (sarW, sarH) = s_sar[idc];
            }

            if (sarW == 0 || sarH == 0)
                (sarW, sarH) = (1, 1);
        }

        if (r.Flag())
            r.Skip(1);
        if (r.Flag())
        {
            r.Skip(3);
            var full = r.Flag();
            if (r.Flag())
            {
                var p = (int)r.Read(8);
                var t = (int)r.Read(8);
                var m = (int)r.Read(8);
                color = new Model.ColorInfo(p, t, m, full);
            }
        }

        if (r.Flag())
        {
            r.Ue();
            r.Ue();
        }

        r.Skip(3); // neutral chroma, field_seq, frame_field_info
        if (r.Flag())
        {
            r.Ue();
            r.Ue();
            r.Ue();
            r.Ue();
        }

        if (r.Flag())
        {
            units = r.Read(32);
            scale = r.Read(32);
        }
    }

    /// <summary>Parses the start of a PPS NAL unit.</summary>
    public static HevcPps ParsePps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.ToRbsp(nal[2..]);
        var r = new BitReader(rbsp);
        var id = (int)r.Ue();
        var spsId = (int)r.Ue();
        var dependent = r.Flag();
        var output = r.Flag();
        var extra = (int)r.Read(3);
        return new HevcPps(id, spsId, dependent, output, extra);
    }

    /// <summary>True when the VCL NAL unit starts a new picture (first_slice_segment_in_pic_flag).</summary>
    public static bool IsFirstSliceSegment(ReadOnlySpan<byte> nal) => nal.Length > 2 && (nal[2] & 0x80) != 0;

    /// <summary>
    /// Reads slice_pic_order_cnt_lsb of the first slice segment of a picture (0 for IDR pictures) and the active
    /// SPS's log2_max_pic_order_cnt_lsb.
    /// </summary>
    public static (int Lsb, int Log2MaxPocLsb) ParsePocLsb(ReadOnlySpan<byte> nal, IReadOnlyDictionary<int, HevcSps> spss, IReadOnlyDictionary<int, HevcPps> ppss)
    {
        var type = NalUnits.HevcType(nal);
        var rbsp = NalUnits.ToRbsp(nal.Length > 66 ? nal[2..66] : nal[2..]);
        var r = new BitReader(rbsp);
        var first = r.Flag();
        if (IsIrap(type))
            r.Skip(1); // no_output_of_prior_pics_flag
        var ppsId = (int)r.Ue();
        if (!ppss.TryGetValue(ppsId, out var pps) || !spss.TryGetValue(pps.SpsId, out var sps))
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_SliceUnknownPps, ppsId));
        if (!first)
            throw new InvalidDataException(Strings.Error_FirstSliceSegmentPoc);
        if (IsIdr(type))
            return (0, sps.Log2MaxPocLsb);
        r.Skip(pps.NumExtraSliceHeaderBits);
        r.Ue(); // slice_type
        if (pps.OutputFlagPresent)
            r.Skip(1);
        if (sps.SeparateColourPlane)
            r.Skip(2);
        return ((int)r.Read(sps.Log2MaxPocLsb), sps.Log2MaxPocLsb);
    }

    /// <summary>Builds an HEVCDecoderConfigurationRecord (4-byte NAL lengths) from parameter sets.</summary>
    /// <param name="vps">VPS NAL units.</param>
    /// <param name="sps">SPS NAL units (the first one describes the stream).</param>
    /// <param name="pps">PPS NAL units.</param>
    /// <param name="sei">Optional prefix SEI NAL units to store in the record.</param>
    public static byte[] BuildHvcC(IReadOnlyList<byte[]> vps, IReadOnlyList<byte[]> sps, IReadOnlyList<byte[]> pps, IReadOnlyList<byte[]>? sei = null)
    {
        if (sps.Count == 0)
            throw new InvalidDataException(Strings.Error_HevcNeedsSps);
        var info = ParseSps(sps[0]);
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.Write(info.GeneralProfileTierLevel);
        ms.WriteByte(0xF0); // reserved + min_spatial_segmentation_idc (0)
        ms.WriteByte(0x00);
        ms.WriteByte(0xFC); // reserved + parallelismType (0)
        ms.WriteByte((byte)(0xFC | info.ChromaFormatIdc));
        ms.WriteByte((byte)(0xF8 | (info.BitDepthLuma - 8)));
        ms.WriteByte((byte)(0xF8 | (info.BitDepthChroma - 8)));
        ms.WriteByte(0); // avgFrameRate
        ms.WriteByte(0);
        ms.WriteByte((byte)(((info.MaxSubLayersMinus1 + 1) << 3) | (info.TemporalIdNesting ? 4 : 0) | 3));
        var arrays = new List<(int Type, IReadOnlyList<byte[]> Nals)> { (NalVps, vps), (NalSps, sps), (NalPps, pps) };
        if (sei is { Count: > 0 })
            arrays.Add((NalSeiPrefix, sei));
        arrays.RemoveAll(a => a.Nals.Count == 0);
        ms.WriteByte((byte)arrays.Count);
        foreach (var (type, nals) in arrays)
        {
            ms.WriteByte((byte)(0x80 | type)); // array_completeness = 1
            ms.WriteByte((byte)(nals.Count >> 8));
            ms.WriteByte((byte)nals.Count);
            foreach (var n in nals)
                H264.WriteU16Prefixed(ms, n);
        }

        return ms.ToArray();
    }

    /// <summary>Decoded hvcC record.</summary>
    public sealed record HevcConfig(int LengthSize, IReadOnlyList<(int Type, bool Complete, byte[] Nal)> Nals);

    /// <summary>Parses an HEVCDecoderConfigurationRecord.</summary>
    public static HevcConfig ParseHvcC(ReadOnlySpan<byte> hvcC)
    {
        if (hvcC.Length < 23)
            throw new InvalidDataException(Strings.Error_InvalidHvcC);
        var lengthSize = (hvcC[21] & 3) + 1;
        var count = hvcC[22];
        var pos = 23;
        var nals = new List<(int, bool, byte[])>();
        for (var a = 0; a < count && pos + 3 <= hvcC.Length; a++)
        {
            var complete = (hvcC[pos] & 0x80) != 0;
            var type = hvcC[pos] & 0x3F;
            var n = (hvcC[pos + 1] << 8) | hvcC[pos + 2];
            pos += 3;
            foreach (var nal in H264.ReadArray(hvcC, ref pos, n))
                nals.Add((type, complete, nal));
        }

        return new HevcConfig(lengthSize, nals);
    }

    /// <summary>Sets array_completeness on every array (parameter sets only in the record: 'hvc1').</summary>
    public static byte[] MarkArraysComplete(byte[] hvcC, bool complete)
    {
        var copy = (byte[])hvcC.Clone();
        if (copy.Length < 23)
            return copy;
        var count = copy[22];
        var pos = 23;
        for (var a = 0; a < count && pos + 3 <= copy.Length; a++)
        {
            copy[pos] = (byte)(complete ? copy[pos] | 0x80 : copy[pos] & 0x7F);
            var n = (copy[pos + 1] << 8) | copy[pos + 2];
            pos += 3;
            for (var i = 0; i < n && pos + 2 <= copy.Length; i++)
                pos += 2 + ((copy[pos] << 8) | copy[pos + 1]);
        }

        return copy;
    }

    /// <summary>Decodes "Profile@Level" from an hvcC record (also the Matroska CodecPrivate).</summary>
    public static string ProfileLevel(ReadOnlySpan<byte> hvcC)
    {
        if (hvcC.Length < 13 || hvcC[0] != 1)
            return string.Empty;

        var profileIdc = hvcC[1] & 0x1F;
        var highTier = (hvcC[1] & 0x20) != 0;
        var profile = profileIdc switch
        {
            1 => "Main",
            2 => "Main 10",
            3 => "Main Still",
            4 => "RExt",
            var p => string.Format(CultureInfo.CurrentCulture, Strings.Label_Profile, p),
        };
        var levelIdc = hvcC[12];
        var text = levelIdc == 0
            ? profile
            : string.Create(CultureInfo.InvariantCulture, $"{profile}@{levelIdc / 30}.{levelIdc % 30 / 3}");
        return highTier ? text + " High" : text;
    }
}
