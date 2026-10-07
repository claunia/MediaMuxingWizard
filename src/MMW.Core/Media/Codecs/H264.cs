using System.Globalization;

namespace MMW.Core.Media.Codecs;

/// <summary>Fields of an H.264 sequence parameter set.</summary>
public sealed record H264Sps
{
    public int ProfileIdc { get; init; }

    public int ConstraintFlags { get; init; }

    public int LevelIdc { get; init; }

    public int Id { get; init; }

    public int ChromaFormatIdc { get; init; } = 1;

    public bool SeparateColourPlane { get; init; }

    public int BitDepthLuma { get; init; } = 8;

    public int BitDepthChroma { get; init; } = 8;

    public int Log2MaxFrameNum { get; init; }

    public int PocType { get; init; }

    public int Log2MaxPocLsb { get; init; }

    public bool FrameMbsOnly { get; init; } = true;

    public int Width { get; init; }

    public int Height { get; init; }

    public int SarWidth { get; init; } = 1;

    public int SarHeight { get; init; } = 1;

    /// <summary>VUI timing (0 when absent).</summary>
    public uint NumUnitsInTick { get; init; }

    public uint TimeScale { get; init; }

    public bool FixedFrameRate { get; init; }

    /// <summary>Colour description from the VUI (unspecified when absent).</summary>
    public Model.ColorInfo Color { get; init; } = Model.ColorInfo.Unspecified;

    /// <summary>Frames per second from VUI timing, or 0.</summary>
    public double FrameRate => NumUnitsInTick > 0 && TimeScale > 0 ? TimeScale / (2.0 * NumUnitsInTick) : 0;
}

/// <summary>Fields of an H.264 picture parameter set needed to parse slice headers.</summary>
public sealed record H264Pps(int Id, int SpsId, bool BottomFieldPicOrderInFramePresent);

/// <summary>The start of an H.264 slice header.</summary>
public readonly record struct H264SliceHeader(
    int NalType,
    int NalRefIdc,
    int FirstMbInSlice,
    int SliceType,
    int PpsId,
    int FrameNum,
    bool FieldPic,
    bool BottomField,
    int IdrPicId,
    int PocLsb,
    int DeltaPocBottom);

/// <summary>Parsing of H.264 parameter sets and slice headers, and avcC records.</summary>
public static class H264
{
    public const int NalSlice = 1;
    public const int NalIdr = 5;
    public const int NalSei = 6;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAud = 9;
    public const int NalEndOfSequence = 10;
    public const int NalEndOfStream = 11;
    public const int NalFiller = 12;
    public const int NalSpsExt = 13;

    private static readonly (int W, int H)[] s_sar =
    [
        (0, 0), (1, 1), (12, 11), (10, 11), (16, 11), (40, 33), (24, 11), (20, 11), (32, 11), (80, 33), (18, 11), (15, 11), (64, 33), (160, 99), (4, 3), (3, 2), (2, 1),
    ];

    /// <summary>Sample aspect ratio of an aspect_ratio_idc (H.264 Table E-1, shared by HEVC and VVC); null when reserved.</summary>
    internal static (int W, int H)? SampleAspectRatio(int idc) => idc > 0 && idc < s_sar.Length ? s_sar[idc] : null;

    public static bool HasChromaInfo(int profileIdc) => profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

    /// <summary>Parses an SPS NAL unit (header byte included).</summary>
    public static H264Sps ParseSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.ToRbsp(nal[1..]);
        var r = new BitReader(rbsp);
        var profile = (int)r.Read(8);
        var constraints = (int)r.Read(8);
        var level = (int)r.Read(8);
        var id = (int)r.Ue();
        int chroma = 1, depthLuma = 8, depthChroma = 8;
        var separate = false;
        if (HasChromaInfo(profile))
        {
            chroma = (int)r.Ue();
            if (chroma == 3)
                separate = r.Flag();
            depthLuma = (int)r.Ue() + 8;
            depthChroma = (int)r.Ue() + 8;
            r.Skip(1); // qpprime_y_zero_transform_bypass_flag
            if (r.Flag())
            {
                for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                {
                    if (r.Flag())
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
                }
            }
        }

        var log2MaxFrameNum = (int)r.Ue() + 4;
        var pocType = (int)r.Ue();
        var log2MaxPocLsb = 0;
        if (pocType == 0)
        {
            log2MaxPocLsb = (int)r.Ue() + 4;
        }
        else if (pocType == 1)
        {
            r.Skip(1);
            r.Se();
            r.Se();
            var n = r.Ue();
            for (var i = 0; i < n; i++)
                r.Se();
        }

        r.Ue(); // max_num_ref_frames
        r.Skip(1); // gaps_in_frame_num_value_allowed_flag
        var widthMbs = (int)r.Ue() + 1;
        var heightMapUnits = (int)r.Ue() + 1;
        var frameMbsOnly = r.Flag();
        if (!frameMbsOnly)
            r.Skip(1); // mb_adaptive_frame_field_flag
        r.Skip(1); // direct_8x8_inference_flag
        var width = widthMbs * 16;
        var height = heightMapUnits * 16 * (frameMbsOnly ? 1 : 2);
        if (r.Flag())
        {
            var left = (int)r.Ue();
            var right = (int)r.Ue();
            var top = (int)r.Ue();
            var bottom = (int)r.Ue();
            var arrayType = separate ? 0 : chroma;
            var cropX = arrayType == 0 ? 1 : arrayType == 3 ? 1 : 2;
            var cropY = (arrayType == 0 ? 1 : arrayType == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
            width -= (left + right) * cropX;
            height -= (top + bottom) * cropY;
        }

        int sarW = 1, sarH = 1;
        uint units = 0, scale = 0;
        var fixedRate = false;
        var color = Model.ColorInfo.Unspecified;
        if (r.BitsLeft > 0 && r.Flag())
        {
            ParseVui(ref r, out sarW, out sarH, out units, out scale, out fixedRate, out color);
        }

        return new H264Sps
        {
            ProfileIdc = profile,
            ConstraintFlags = constraints,
            LevelIdc = level,
            Id = id,
            ChromaFormatIdc = chroma,
            SeparateColourPlane = separate,
            BitDepthLuma = depthLuma,
            BitDepthChroma = depthChroma,
            Log2MaxFrameNum = log2MaxFrameNum,
            PocType = pocType,
            Log2MaxPocLsb = log2MaxPocLsb,
            FrameMbsOnly = frameMbsOnly,
            Width = width,
            Height = height,
            SarWidth = sarW,
            SarHeight = sarH,
            NumUnitsInTick = units,
            TimeScale = scale,
            FixedFrameRate = fixedRate,
            Color = color,
        };
    }

    private static void ParseVui(ref BitReader r, out int sarW, out int sarH, out uint units, out uint scale, out bool fixedRate, out Model.ColorInfo color)
    {
        sarW = 1;
        sarH = 1;
        units = 0;
        scale = 0;
        fixedRate = false;
        color = Model.ColorInfo.Unspecified;
        try
        {
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
                r.Skip(1); // overscan_appropriate_flag
            if (r.Flag())
            {
                r.Skip(3); // video_format
                var full = r.Flag();
                if (r.Flag())
                {
                    var primaries = (int)r.Read(8);
                    var transfer = (int)r.Read(8);
                    var matrix = (int)r.Read(8);
                    color = new Model.ColorInfo(primaries, transfer, matrix, full);
                }
            }

            if (r.Flag())
            {
                r.Ue();
                r.Ue();
            }

            if (r.Flag())
            {
                units = r.Read(32);
                scale = r.Read(32);
                fixedRate = r.Flag();
            }
        }
        catch (InvalidDataException)
        {
            // Truncated VUI: keep what was read.
        }
    }

    private static void SkipScalingList(ref BitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
                next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    /// <summary>Parses the start of a PPS NAL unit.</summary>
    public static H264Pps ParsePps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.ToRbsp(nal[1..]);
        var r = new BitReader(rbsp);
        var id = (int)r.Ue();
        var spsId = (int)r.Ue();
        r.Skip(1); // entropy_coding_mode_flag
        var bottomFieldPicOrder = r.Flag();
        return new H264Pps(id, spsId, bottomFieldPicOrder);
    }

    /// <summary>Parses the beginning of a slice header (up to the picture order count fields).</summary>
    public static H264SliceHeader ParseSliceHeader(ReadOnlySpan<byte> nal, IReadOnlyDictionary<int, H264Sps> spss, IReadOnlyDictionary<int, H264Pps> ppss)
    {
        var nalType = nal[0] & 0x1F;
        var refIdc = (nal[0] >> 5) & 3;
        var rbsp = NalUnits.ToRbsp(nal.Length > 64 ? nal[1..64] : nal[1..]);
        var r = new BitReader(rbsp);
        var firstMb = (int)r.Ue();
        var sliceType = (int)r.Ue();
        var ppsId = (int)r.Ue();
        if (!ppss.TryGetValue(ppsId, out var pps) || !spss.TryGetValue(pps.SpsId, out var sps))
            throw new InvalidDataException($"Slice refers to unknown PPS {ppsId}.");
        if (sps.SeparateColourPlane)
            r.Skip(2);
        var frameNum = (int)r.Read(sps.Log2MaxFrameNum);
        bool field = false, bottom = false;
        if (!sps.FrameMbsOnly)
        {
            field = r.Flag();
            if (field)
                bottom = r.Flag();
        }

        var idrPicId = nalType == NalIdr ? (int)r.Ue() : 0;
        int pocLsb = 0, deltaBottom = 0;
        if (sps.PocType == 0)
        {
            pocLsb = (int)r.Read(sps.Log2MaxPocLsb);
            if (pps.BottomFieldPicOrderInFramePresent && !field)
                deltaBottom = r.Se();
        }

        return new H264SliceHeader(nalType, refIdc, firstMb, sliceType, ppsId, frameNum, field, bottom, idrPicId, pocLsb, deltaBottom);
    }

    /// <summary>Builds an AVCDecoderConfigurationRecord with 4-byte NAL lengths.</summary>
    public static byte[] BuildAvcC(IReadOnlyList<byte[]> spsList, IReadOnlyList<byte[]> ppsList, IReadOnlyList<byte[]>? spsExtList = null)
    {
        if (spsList.Count == 0)
            throw new InvalidDataException("An H.264 stream needs at least one SPS.");
        var sps = ParseSps(spsList[0]);
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.WriteByte(spsList[0][1]);
        ms.WriteByte(spsList[0][2]);
        ms.WriteByte(spsList[0][3]);
        ms.WriteByte(0xFF); // reserved + lengthSizeMinusOne = 3
        ms.WriteByte((byte)(0xE0 | spsList.Count));
        foreach (var s in spsList)
            WriteU16Prefixed(ms, s);
        ms.WriteByte((byte)ppsList.Count);
        foreach (var p in ppsList)
            WriteU16Prefixed(ms, p);
        if (HasChromaInfo(sps.ProfileIdc))
        {
            ms.WriteByte((byte)(0xFC | sps.ChromaFormatIdc));
            ms.WriteByte((byte)(0xF8 | (sps.BitDepthLuma - 8)));
            ms.WriteByte((byte)(0xF8 | (sps.BitDepthChroma - 8)));
            var ext = spsExtList ?? [];
            ms.WriteByte((byte)ext.Count);
            foreach (var e in ext)
                WriteU16Prefixed(ms, e);
        }

        return ms.ToArray();
    }

    internal static void WriteU16Prefixed(Stream s, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 8));
        s.WriteByte((byte)data.Length);
        s.Write(data);
    }

    /// <summary>Decoded avcC record.</summary>
    public sealed record AvcConfig(int LengthSize, IReadOnlyList<byte[]> Sps, IReadOnlyList<byte[]> Pps);

    /// <summary>Parses an AVCDecoderConfigurationRecord.</summary>
    public static AvcConfig ParseAvcC(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 7 || avcC[0] != 1)
            throw new InvalidDataException("Invalid avcC record.");
        var lengthSize = (avcC[4] & 3) + 1;
        var pos = 6;
        var sps = ReadArray(avcC, ref pos, avcC[5] & 0x1F);
        List<byte[]> pps = [];
        if (pos < avcC.Length)
        {
            var count = avcC[pos];
            pos++;
            pps = ReadArray(avcC, ref pos, count);
        }

        return new AvcConfig(lengthSize, sps, pps);
    }

    internal static List<byte[]> ReadArray(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        var list = new List<byte[]>(count);
        for (var i = 0; i < count && pos + 2 <= data.Length; i++)
        {
            var len = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            if (pos + len > data.Length)
                throw new InvalidDataException("Truncated parameter set array.");
            list.Add(data.Slice(pos, len).ToArray());
            pos += len;
        }

        return list;
    }

    /// <summary>Decodes "Profile@Level" from an avcC record (also the Matroska CodecPrivate).</summary>
    public static string ProfileLevel(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 4 || avcC[0] != 1)
            return string.Empty;

        var profile = avcC[1] switch
        {
            66 => "Baseline",
            77 => "Main",
            88 => "Extended",
            100 => "High",
            110 => "High 10",
            122 => "High 4:2:2",
            244 => "High 4:4:4",
            44 => "CAVLC 4:4:4",
            var p => "Profile " + p.ToString(CultureInfo.InvariantCulture),
        };
        var level = avcC[3];
        return string.Create(CultureInfo.InvariantCulture, $"{profile}@{level / 10}.{level % 10}");
    }
}
