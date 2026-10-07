using System.Buffers.Binary;
using System.Globalization;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>An AV2 OBU header (AV2 §5.2.2): 1 byte, or 2 with the embedded and extended layer identifiers.</summary>
public readonly record struct Av2ObuHeader(int Type, int TLayer, int MLayer, int XLayer, int Length);

/// <summary>The fields of an AV2 sequence header OBU (AV2 §5.4) that describe the stream and its frame headers.</summary>
public sealed record Av2SequenceHeader
{
    public int SeqHeaderId { get; init; }

    public int Profile { get; init; }

    public int Level { get; init; }

    public int Tier { get; init; }

    /// <summary>chroma_format_idc: 0 4:2:0, 1 4:0:0, 2 4:4:4, 3 4:2:2.</summary>
    public int ChromaFormat { get; init; }

    public int BitDepth { get; init; }

    public bool SinglePictureHeader { get; init; }

    public bool StillPicture { get; init; }

    /// <summary>Output order equals decoding order (AV1-style); otherwise samples need composition offsets.</summary>
    public bool MonotonicOutputOrder { get; init; }

    public int MaxTLayer { get; init; }

    public int MaxMLayer { get; init; }

    public int MaxWidth { get; init; }

    public int MaxHeight { get; init; }

    public int CropLeft { get; init; }

    public int CropRight { get; init; }

    public int CropTop { get; init; }

    public int CropBottom { get; init; }

    public int OrderHintBits { get; init; }

    public int NumRefFrames { get; init; }

    public int LongTermFrameIdBits { get; init; }

    public bool Monochrome => ChromaFormat == 1;

    public bool SubsamplingX => ChromaFormat is 0 or 1 or 3;

    public bool SubsamplingY => ChromaFormat is 0 or 1;

    /// <summary>Displayed width: the maximum frame width less the sequence cropping window.</summary>
    public int Width => Math.Max(1, MaxWidth - CropLeft - CropRight);

    public int Height => Math.Max(1, MaxHeight - CropTop - CropBottom);
}

/// <summary>A content interpretation OBU (AV2 §5.15): colour, sample aspect ratio and timing.</summary>
public sealed record Av2ContentInterpretation(ColorInfo Color, int SarWidth, int SarHeight, int ChromaSamplePosition, double FrameRate);

/// <summary>What a coded frame's header says about its output: whether it is output and its order hint.</summary>
/// <param name="OrderHintLsbs">The coded order hint; null when it comes from a reference frame (bridge, derived SEF).</param>
public sealed record Av2FrameInfo(int ObuType, bool IsOutput, int? OrderHintLsbs, int OrderHintBits);

/// <summary>
/// AV2 bitstream helpers (AV2 Bitstream &amp; Decoding Process Specification v1.0.0) and its storage: the
/// length-delimited OBU format (Annex B, the only AV2 bitstream format), the ISOBMFF binding draft ('av02', 'av2C':
/// samples hold the OBUs of one temporal unit without temporal delimiter or configuration OBUs) and the Matroska
/// mapping of the reference encoder (V_AV2, a 4-byte configuration record, blocks holding whole temporal units).
/// </summary>
public static class Av2
{
    public const int ObuSequenceHeader = 1;
    public const int ObuTemporalDelimiter = 2;
    public const int ObuMultiFrameHeader = 3;
    public const int ObuClosedLoopKey = 4;
    public const int ObuOpenLoopKey = 5;
    public const int ObuLeadingTileGroup = 6;
    public const int ObuRegularTileGroup = 7;
    public const int ObuMetadataShort = 8;
    public const int ObuMetadataGroup = 9;
    public const int ObuSwitch = 10;
    public const int ObuLeadingSef = 11;
    public const int ObuRegularSef = 12;
    public const int ObuLeadingTip = 13;
    public const int ObuRegularTip = 14;
    public const int ObuBufferRemovalTiming = 15;
    public const int ObuLayerConfigurationRecord = 16;
    public const int ObuAtlasSegment = 17;
    public const int ObuOperatingPointSet = 18;
    public const int ObuBridgeFrame = 19;
    public const int ObuMsdo = 20;
    public const int ObuRasFrame = 21;
    public const int ObuQuantizationMatrix = 22;
    public const int ObuFilmGrain = 23;
    public const int ObuContentInterpretation = 24;
    public const int ObuPadding = 25;

    public const int MetadataHdrCll = 1;
    public const int MetadataHdrMdcv = 2;
    public const int MetadataItutT35 = 3;

    public const int GlobalXLayer = 31;

    /// <summary>Callback for <see cref="ForEachObu"/>; <paramref name="obu"/> includes the header. Return true to stop.</summary>
    public delegate bool ObuVisitor(Av2ObuHeader header, ReadOnlySpan<byte> obu, ReadOnlySpan<byte> payload);

    /// <summary>Callback for <see cref="ForEachMetadata"/>; return true to stop.</summary>
    public delegate bool MetadataVisitor(int metadataType, ReadOnlySpan<byte> payload);

    public static bool ReadLeb128(ReadOnlySpan<byte> data, ref int pos, out long value) => DolbyVision.Leb128(data, ref pos, out value);

    public static void WriteLeb128(List<byte> output, long value)
    {
        ArgumentNullException.ThrowIfNull(output);
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            output.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);
    }

    public static Av2ObuHeader? ParseObuHeader(ReadOnlySpan<byte> obu)
    {
        if (obu.Length < 1)
            return null;
        var h = obu[0];
        var type = (h >> 2) & 0x1F;
        var tlayer = h & 3;
        if ((h & 0x80) == 0)
            return new Av2ObuHeader(type, tlayer, 0, type is ObuMsdo or ObuTemporalDelimiter ? GlobalXLayer : 0, 1);
        return obu.Length < 2 ? null : new Av2ObuHeader(type, tlayer, obu[1] >> 5, obu[1] & 0x1F, 2);
    }

    /// <summary>Walks length-delimited OBUs (each preceded by its leb128 size), as samples and Annex B streams hold them.</summary>
    public static void ForEachObu(ReadOnlySpan<byte> data, ObuVisitor visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var pos = 0;
        while (pos < data.Length)
        {
            if (!ReadLeb128(data, ref pos, out var size) || size < 1 || size > data.Length - pos)
                return;
            var obu = data.Slice(pos, (int)size);
            pos += (int)size;
            if (ParseObuHeader(obu) is not { } header || header.Length > obu.Length)
                return;
            if (visit(header, obu, obu[header.Length..]))
                return;
        }
    }

    /// <summary>The OBUs (header included, length prefix excluded) of length-delimited data.</summary>
    public static List<byte[]> Obus(ReadOnlySpan<byte> data)
    {
        var list = new List<byte[]>();
        ForEachObu(data, (_, obu, _) =>
        {
            list.Add(obu.ToArray());
            return false;
        });
        return list;
    }

    /// <summary>OBUs of a coded frame (tile groups, key frames, switch, RAS, show-existing, TIP and bridge frames).</summary>
    public static bool IsFrame(int type) =>
        type is ObuClosedLoopKey or ObuOpenLoopKey or ObuLeadingTileGroup or ObuRegularTileGroup or ObuSwitch or ObuLeadingSef or ObuRegularSef or
            ObuLeadingTip or ObuRegularTip or ObuBridgeFrame or ObuRasFrame;

    private static bool IsTileGroup(int type) =>
        type is ObuClosedLoopKey or ObuOpenLoopKey or ObuLeadingTileGroup or ObuRegularTileGroup or ObuSwitch or ObuRasFrame;

    /// <summary>
    /// OBUs that the ISOBMFF binding keeps out of samples: temporal delimiters (the sample is the temporal unit),
    /// padding, and the configuration OBUs carried by 'av2C' (sequence header, layer configuration record, content
    /// interpretation).
    /// </summary>
    public static bool IsConfiguration(int type) => type is ObuSequenceHeader or ObuLayerConfigurationRecord or ObuContentInterpretation;

    private static int CeilLog2(int x)
    {
        var n = 0;
        while ((1 << n) < x)
            n++;
        return n;
    }

    /// <summary>Parses a sequence header OBU payload up to the inter configuration; null when it cannot be read.</summary>
    public static Av2SequenceHeader? ParseSequenceHeader(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new BitReader(payload);
            var id = (int)r.Ue();
            var profile = (int)r.Read(5);
            var single = r.Flag();
            var level = (int)r.Read(5);
            var tier = level > 3 && !single ? (int)r.Read(1) : 0;
            var chroma = (int)r.Ue();
            var depthIdc = (int)r.Ue();
            var bitDepth = depthIdc switch
            {
                0 => 10,
                1 => 8,
                _ => 12,
            };
            bool still, monotonic;
            int maxT, maxM;
            if (single)
            {
                (still, maxT, maxM, monotonic) = (true, 0, 0, true);
            }
            else
            {
                r.Skip(3); // seq_lcr_id
                still = r.Flag();
                maxT = (int)r.Read(2);
                maxM = (int)r.Read(3);
                if (maxM > 0)
                    r.Skip(CeilLog2(maxM + 1)); // seq_max_mlayer_cnt_minus_1
                monotonic = r.Flag();
            }

            var widthBits = (int)r.Read(4) + 1;
            var heightBits = (int)r.Read(4) + 1;
            var maxWidth = (int)r.Read(widthBits) + 1;
            var maxHeight = (int)r.Read(heightBits) + 1;
            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (r.Flag())
            {
                cropLeft = (int)r.Ue();
                cropRight = (int)r.Ue();
                cropTop = (int)r.Ue();
                cropBottom = (int)r.Ue();
            }

            if (!single)
            {
                if (r.Flag())
                    r.Skip(4); // seq_initial_display_delay_minus_1
                if (r.Flag()) // decoder_model_info_present_flag
                {
                    r.Skip(32); // num_units_in_decoding_tick
                    if (r.Flag())
                    {
                        r.Ue(); // decoder_buffer_delay
                        r.Ue(); // encoder_buffer_delay
                        r.Skip(1); // low_delay_mode_flag
                    }
                }
            }

            if (maxM > 0 && r.Flag()) // mlayer_dependency_present_flag
            {
                for (var curr = 1; curr <= maxM; curr++)
                    r.Skip(curr + 1);
            }

            if (maxT > 0 && r.Flag()) // tlayer_dependency_present_flag
            {
                var multi = maxM > 0 && r.Flag();
                for (var m = 0; m <= maxM; m++)
                {
                    for (var currT = 1; currT <= maxT; currT++)
                    {
                        if (multi || m == 0)
                            r.Skip(currT + 1);
                    }
                }
            }

            // sequence_partition_config
            var monochrome = chroma == 1;
            if (!r.Flag())
                r.Skip(1); // use_128x128_superblock
            var sdp = !monochrome && r.Flag();
            if (sdp && !single)
                r.Skip(1);
            if (r.Flag())
                r.Skip(1); // enable_uneven_4way_partitions
            if (r.Flag())
                r.Skip(1); // max_pb_aspect_ratio_log2_minus_1

            // sequence_segment_config
            var maxSegments = r.Flag() ? 16 : 8;
            if (r.Flag())
            {
                r.Skip(1); // seq_allow_seg_info_change
                for (var i = 0; i < maxSegments; i++)
                {
                    for (var j = 0; j < 3; j++)
                    {
                        if (r.Flag() && j == 0)
                            r.Skip(10); // su(1 + 9): the quantizer feature; the others carry no value
                    }
                }
            }

            // sequence_intra_config
            r.Skip(4);
            if (!monochrome)
                r.Skip(2);
            r.Skip(2);

            // sequence_inter_config, up to the fields frame headers need
            int orderHintBits = 0, numRefFrames = 2, longTermBits = 0;
            if (!single)
            {
                var enabled = new bool[5];
                var any = false;
                for (var mode = 1; mode < 5; mode++)
                {
                    enabled[mode] = r.Flag();
                    any |= enabled[mode];
                }

                if (any)
                    r.Skip(1); // seq_frame_motion_modes_present_flag
                if (enabled[3])
                    r.Skip(1); // enable_six_param_warp_delta
                r.Skip(1); // enable_masked_compound
                if (r.Flag())
                    r.Skip(1); // reduced_ref_frame_mvs_mode
                orderHintBits = (int)r.Read(4) + 1;
                r.Skip(1); // enable_refmvbank
                if (!r.Flag())
                    r.Skip(1); // constrain_drl_reorder
                r.Skip(1); // explicit_ref_frame_map
                numRefFrames = r.Flag() ? (int)r.Read(4) + 1 : 8;
                longTermBits = (int)r.Read(3);
            }

            return new Av2SequenceHeader
            {
                SeqHeaderId = id,
                Profile = profile,
                Level = level,
                Tier = tier,
                ChromaFormat = chroma,
                BitDepth = bitDepth,
                SinglePictureHeader = single,
                StillPicture = still,
                MonotonicOutputOrder = monotonic,
                MaxTLayer = maxT,
                MaxMLayer = maxM,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                CropLeft = cropLeft,
                CropRight = cropRight,
                CropTop = cropTop,
                CropBottom = cropBottom,
                OrderHintBits = orderHintBits,
                NumRefFrames = numRefFrames,
                LongTermFrameIdBits = longTermBits,
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static readonly int[] s_sarWidth = [0, 1, 12, 10, 16, 40, 24, 20, 32, 80, 18, 15, 64, 160, 4, 3, 2];
    private static readonly int[] s_sarHeight = [0, 1, 11, 11, 11, 33, 11, 11, 11, 33, 11, 11, 33, 99, 3, 2, 1];

    /// <summary>The colour description presets of ci_color_description_idc (Table 6.13): primaries, transfer, matrix.</summary>
    private static (int Primaries, int Transfer, int Matrix)? ColorPreset(int idc) => idc switch
    {
        1 => (1, 1, 1), // BT.709 SDR
        2 => (9, 16, 9), // BT.2100 PQ
        3 => (9, 18, 9), // BT.2100 HLG
        4 => (1, 13, 0), // sRGB
        5 => (1, 13, 5), // sYCC
        _ => null,
    };

    /// <summary>Parses a content interpretation OBU payload; null when it cannot be read.</summary>
    public static Av2ContentInterpretation? ParseContentInterpretation(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new BitReader(payload);
            var scanType = (int)r.Read(2);
            var colorPresent = r.Flag();
            var chromaPresent = r.Flag();
            var aspectPresent = r.Flag();
            var timingPresent = r.Flag();
            r.Skip(2);
            var color = ColorInfo.Unspecified;
            if (colorPresent)
            {
                var idc = RiceGolomb(ref r, 2);
                var (primaries, transfer, matrix) = idc == 0 ? ((int)r.Read(8), (int)r.Read(8), (int)r.Read(8)) : ColorPreset(idc) ?? (2, 2, 2);
                color = new ColorInfo(primaries, transfer, matrix, r.Flag());
            }

            var chromaPosition = -1;
            if (chromaPresent)
            {
                chromaPosition = (int)r.Ue();
                if (scanType != 1)
                    r.Ue();
            }

            int sarWidth = 1, sarHeight = 1;
            if (aspectPresent)
            {
                var idc = (int)r.Read(8);
                if (idc == 255)
                {
                    sarWidth = (int)r.Ue();
                    sarHeight = (int)r.Ue();
                }
                else if (idc < s_sarWidth.Length && idc > 0)
                {
                    (sarWidth, sarHeight) = (s_sarWidth[idc], s_sarHeight[idc]);
                }
            }

            double frameRate = 0;
            if (timingPresent)
            {
                var unitsPerTick = r.Read(32);
                var timeScale = r.Read(32);
                var ticksPerPicture = r.Flag() ? r.Ue() + 1 : 1;
                if (unitsPerTick > 0 && timeScale > 0)
                    frameRate = timeScale / ((double)unitsPerTick * ticksPerPicture);
            }

            return new Av2ContentInterpretation(color, sarWidth, sarHeight, chromaPosition, frameRate);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>rg(n): Rice-Golomb code with parameter n (AV2 §4.11.10).</summary>
    private static int RiceGolomb(ref BitReader r, int n)
    {
        for (var q = 0; q < 32; q++)
        {
            if (!r.Flag())
                return (q << n) + (int)r.Read(n);
        }

        throw new InvalidDataException(Strings.Error_InvalidRgCode);
    }

    /// <summary>
    /// Walks the metadata units of a metadata OBU payload (short: one unit; group: several), giving each unit's type and
    /// payload. Cancelled units are skipped.
    /// </summary>
    public static void ForEachMetadata(int obuType, ReadOnlySpan<byte> payload, MetadataVisitor visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        try
        {
            if (obuType == ObuMetadataShort)
            {
                // metadata_is_suffix(1) muh_layer_idc(3) muh_cancel_flag(1) muh_persistence_idc(3), then leb128 type.
                if (payload.Length < 2 || (payload[0] & 0x08) != 0)
                    return;
                var pos = 1;
                if (!ReadLeb128(payload, ref pos, out var type))
                    return;
                // metadataPayloadSize = obuPayloadSize - 2 - Leb128Bytes: the unit ends before the trailing bits byte.
                if (payload.Length - 1 > pos)
                    visit((int)type, payload[pos..^1]);
                return;
            }

            if (obuType != ObuMetadataGroup)
                return;
            if (payload.Length < 2)
                return;
            var r = 1; // metadata_is_suffix(1) metadata_necessity_idc(2) metadata_application_id(5)
            if (!ReadLeb128(payload, ref r, out var countMinus1))
                return;
            for (long i = 0; i <= countMinus1 && r < payload.Length; i++)
            {
                if (!ReadLeb128(payload, ref r, out var type) || r >= payload.Length)
                    return;
                var headerSize = payload[r] >> 1;
                var cancel = (payload[r] & 1) != 0;
                r++;
                var headerEnd = r + headerSize;
                if (headerEnd > payload.Length)
                    return;
                if (cancel)
                {
                    r = headerEnd;
                    continue;
                }

                if (!ReadLeb128(payload, ref r, out var size))
                    return;
                r = headerEnd; // the rest of the unit header: layer, persistence, priority, layer maps, extensions
                if (size > payload.Length - r)
                    return;
                if (visit((int)type, payload.Slice(r, (int)size)))
                    return;
                r += (int)size;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // Malformed metadata: ignored.
        }
    }

    /// <summary>The ITU-T T.35 messages (country code onward) of a sample's metadata OBUs; the visitor returns true to stop.</summary>
    public static bool ForEachT35(ReadOnlySpan<byte> sample, Func<byte[], bool> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var stop = false;
        ForEachObu(sample, (header, _, payload) =>
        {
            if (header.Type is ObuMetadataShort or ObuMetadataGroup)
            {
                ForEachMetadata(header.Type, payload, (type, unit) => stop = type == MetadataItutT35 && visit(unit.ToArray()));
            }

            return stop;
        });
        return stop;
    }

    /// <summary>
    /// Reads a coded frame's header up to its order hint (AV2 §5.18.2), with the sequence header it activates; null
    /// when it cannot be read (multi-frame headers are not followed).
    /// </summary>
    public static Av2FrameInfo? ParseFrameHeader(int obuType, ReadOnlySpan<byte> payload, Func<int, Av2SequenceHeader?> sequenceHeader)
    {
        ArgumentNullException.ThrowIfNull(sequenceHeader);
        try
        {
            var r = new BitReader(payload);
            if (IsTileGroup(obuType) && !r.Flag())
                return null; // not the first tile group: the frame header was in an earlier OBU
            var bridge = obuType == ObuBridgeFrame;
            if (!bridge && r.Ue() != 0)
                return null; // cur_mfh_id: a multi-frame header supplies the parameters
            if (sequenceHeader((int)r.Ue()) is not { } seq)
                return null;
            if (bridge)
                return new Av2FrameInfo(obuType, false, null, seq.OrderHintBits);
            if (seq.SinglePictureHeader)
                return new Av2FrameInfo(obuType, true, 0, 0);
            if (obuType is ObuLeadingSef or ObuRegularSef)
            {
                r.Skip(CeilLog2(seq.NumRefFrames)); // frame_to_show_map_idx
                var derive = r.Flag();
                return new Av2FrameInfo(obuType, true, derive ? null : (int)r.Read(seq.OrderHintBits), seq.OrderHintBits);
            }

            var key = obuType is ObuClosedLoopKey or ObuOpenLoopKey;
            var restricted = false;
            if (obuType is ObuSwitch or ObuRasFrame)
                restricted = r.Flag();
            else if (!key && obuType is not (ObuLeadingTip or ObuRegularTip))
                r.Skip(1); // frame_is_inter
            if (key)
                r.Skip(seq.LongTermFrameIdBits);
            if (obuType is ObuRasFrame or ObuOpenLoopKey && seq.LongTermFrameIdBits != 0)
            {
                var keyRefs = (int)r.Read(3);
                r.Skip(keyRefs * seq.LongTermFrameIdBits);
            }

            var immediate = obuType != ObuOpenLoopKey && r.Flag();
            var implicitOutput = !immediate && !seq.MonotonicOutputOrder && r.Flag();
            if (obuType != ObuSwitch)
                r.Skip(1); // frame_size_override_flag
            var orderHint = (int)r.Read(seq.OrderHintBits);
            _ = restricted;
            return new Av2FrameInfo(obuType, immediate || implicitOutput, orderHint, seq.OrderHintBits);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>seq_level_idx 0–21 (Table A.7): levels 2 to 4 have two sub-levels, 5 to 8 four.</summary>
    private static readonly string[] s_levels =
        ["2.0", "2.1", "3.0", "3.1", "4.0", "4.1", "5.0", "5.1", "5.2", "5.3", "6.0", "6.1", "6.2", "6.3", "7.0", "7.1", "7.2", "7.3", "8.0", "8.1", "8.2", "8.3"];

    /// <summary>"Main_420_10_IP0@L4.0" with the tier when it is the high one.</summary>
    public static string ProfileLevel(Av2SequenceHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var profile = header.Profile switch
        {
            0 => "Main_420_10_IP0",
            1 => "Main_420_10_IP1",
            2 => "Main_420_10_IP2",
            3 => "Main_422_10_IP1",
            4 => "Main_444_10_IP1",
            31 => "Configurable",
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Label_Profile, header.Profile),
        };
        var level = header.Level switch
        {
            < 22 => $"L{s_levels[header.Level]}",
            31 => "max",
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Label_Level, header.Level),
        };
        return $"{profile}@{level}{(header.Tier == 1 ? " High tier" : string.Empty)}";
    }

    // ------------------------------------------------------------------ configuration records

    /// <summary>
    /// The AV2CodecConfigurationBox payload ('av2C', ISOBMFF binding draft): reserved byte, OBU count minus one, then
    /// each configuration OBU with its leb128 length.
    /// </summary>
    public static byte[] BuildConfigurationBox(IReadOnlyList<byte[]> configurationObus)
    {
        ArgumentNullException.ThrowIfNull(configurationObus);
        if (configurationObus.Count is 0 or > 256)
            throw new InvalidDataException(Strings.Error_Av2ObuCount);
        var o = new List<byte> { 0, (byte)(configurationObus.Count - 1) };
        foreach (var obu in configurationObus)
        {
            WriteLeb128(o, obu.Length);
            o.AddRange(obu);
        }

        return [.. o];
    }

    /// <summary>The configuration OBUs of an 'av2C' payload; empty when it is not one (e.g. a Matroska record).</summary>
    public static List<byte[]> ConfigurationObus(ReadOnlySpan<byte> av2C)
    {
        var list = new List<byte[]>();
        if (av2C.Length < 3 || av2C[0] != 0)
            return list;
        var count = av2C[1] + 1;
        var data = av2C[2..];
        ForEachObu(data, (_, obu, _) =>
        {
            list.Add(obu.ToArray());
            return list.Count == count;
        });
        return list.Count == count ? list : [];
    }

    /// <summary>The sequence header and content interpretation of an 'av2C' payload.</summary>
    public static (Av2SequenceHeader? Sequence, Av2ContentInterpretation? Interpretation) Describe(ReadOnlySpan<byte> av2C)
    {
        Av2SequenceHeader? sequence = null;
        Av2ContentInterpretation? interpretation = null;
        foreach (var obu in ConfigurationObus(av2C))
        {
            if (ParseObuHeader(obu) is not { } h)
                continue;
            if (h.Type == ObuSequenceHeader && sequence is null)
                sequence = ParseSequenceHeader(obu.AsSpan(h.Length));
            else if (h.Type == ObuContentInterpretation && interpretation is null)
                interpretation = ParseContentInterpretation(obu.AsSpan(h.Length));
        }

        return (sequence, interpretation);
    }

    /// <summary>
    /// The 4-byte configuration record the AV2 reference encoder writes as Matroska CodecPrivate: marker and version
    /// (1, 1), profile (5), level (5), tier (1), bit depth index (2), monochrome, subsampling x/y, chroma sample
    /// position (3), initial presentation delay (1 + 4).
    /// </summary>
    public static byte[] BuildMatroskaRecord(Av2SequenceHeader sequence, Av2ContentInterpretation? interpretation)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var w = new BitWriter();
        w.Write(1, 1);
        w.Write(1, 7);
        w.Write((uint)sequence.Profile, 5);
        w.Write((uint)sequence.Level, 5);
        w.Write((uint)sequence.Tier, 1);
        w.Write(sequence.BitDepth switch { 10 => 0u, 8 => 1u, _ => 2u }, 2);
        w.Write(sequence.Monochrome ? 1u : 0u, 1);
        w.Write(sequence.SubsamplingX ? 1u : 0u, 1);
        w.Write(sequence.SubsamplingY ? 1u : 0u, 1);
        w.Write((uint)Math.Clamp(interpretation?.ChromaSamplePosition ?? 0, 0, 7), 3);
        w.Write(0, 1);
        w.Write(0, 4);
        return w.ToArray();
    }

    // ------------------------------------------------------------------ samples and temporal units

    /// <summary>Splits length-delimited data into temporal units at its temporal delimiters (ranges include them).</summary>
    public static List<Range> SplitTemporalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<Range>();
        var pos = 0;
        var start = -1;
        while (pos < data.Length)
        {
            var at = pos;
            if (!ReadLeb128(data, ref pos, out var size) || size < 1 || size > data.Length - pos)
                break;
            if (((data[pos] >> 2) & 0x1F) == ObuTemporalDelimiter)
            {
                if (start >= 0)
                    units.Add(start..at);
                start = at;
            }
            else if (start < 0)
            {
                start = at; // data without a leading delimiter: one temporal unit
            }

            pos += (int)size;
        }

        if (start >= 0)
            units.Add(start..pos);
        return units;
    }

    /// <summary>
    /// A temporal unit as an ISOBMFF sample: without its temporal delimiter, padding and configuration OBUs, which are
    /// returned separately (in order).
    /// </summary>
    public static byte[] ToSample(ReadOnlySpan<byte> temporalUnit, List<byte[]> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var o = new List<byte>(temporalUnit.Length);
        var pos = 0;
        while (pos < temporalUnit.Length)
        {
            var start = pos;
            if (!ReadLeb128(temporalUnit, ref pos, out var size) || size < 1 || size > temporalUnit.Length - pos)
                break;
            var obu = temporalUnit.Slice(pos, (int)size);
            pos += (int)size;
            var type = (obu[0] >> 2) & 0x1F;
            if (type is ObuTemporalDelimiter or ObuPadding)
                continue;
            if (IsConfiguration(type))
            {
                configuration.Add(obu.ToArray());
                continue;
            }

            o.AddRange(temporalUnit[start..pos]);
        }

        return [.. o];
    }

    /// <summary>Position of an OBU in a temporal unit (AV2 §7.3.7), for placing configuration OBUs back into it.</summary>
    private static int Rank(int type, int xlayer) => type switch
    {
        ObuTemporalDelimiter => 0,
        ObuMsdo => 1,
        ObuLayerConfigurationRecord => xlayer == GlobalXLayer ? 2 : 6,
        ObuOperatingPointSet => xlayer == GlobalXLayer ? 3 : 7,
        ObuAtlasSegment => xlayer == GlobalXLayer ? 4 : 8,
        ObuMetadataShort or ObuMetadataGroup when xlayer == GlobalXLayer => 5,
        ObuSequenceHeader => 9,
        ObuContentInterpretation => 10,
        _ => 11,
    };

    /// <summary>
    /// The temporal unit of a sample (ISOBMFF binding §"AV2 sample reconstruction"): a temporal delimiter, then the
    /// sample's OBUs with the configuration OBUs inserted at their earliest valid positions when
    /// <paramref name="withConfiguration"/> (random access points).
    /// </summary>
    public static byte[] ToTemporalUnit(ReadOnlySpan<byte> sample, IReadOnlyList<byte[]> configuration, bool withConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var o = new List<byte>(sample.Length + 64) { 1, ObuTemporalDelimiter << 2 };
        var pending = withConfiguration ? new Queue<byte[]>(configuration) : new Queue<byte[]>();
        var pos = 0;
        while (pos < sample.Length)
        {
            var start = pos;
            if (!ReadLeb128(sample, ref pos, out var size) || size < 1 || size > sample.Length - pos)
                break;
            var obu = sample.Slice(pos, (int)size);
            pos += (int)size;
            var rank = ParseObuHeader(obu) is { } h ? Rank(h.Type, h.XLayer) : 11;
            while (pending.Count > 0 && ParseObuHeader(pending.Peek()) is { } c && Rank(c.Type, c.XLayer) < rank)
                Append(o, pending.Dequeue());
            o.AddRange(sample[start..pos]);
        }

        while (pending.Count > 0)
            Append(o, pending.Dequeue());
        return [.. o];

        static void Append(List<byte> output, byte[] obu)
        {
            WriteLeb128(output, obu.Length);
            output.AddRange(obu);
        }
    }

    /// <summary>
    /// A sync sample (ISOBMFF binding §"Sync sample"): in every extended layer, the first coded frame is a closed loop
    /// key frame.
    /// </summary>
    public static bool IsSync(ReadOnlySpan<byte> sample)
    {
        var seen = 0u;
        var sync = false;
        var any = false;
        ForEachObu(sample, (h, _, _) =>
        {
            if (!IsFrame(h.Type) || (seen & (1u << h.XLayer)) != 0)
                return false;
            seen |= 1u << h.XLayer;
            any = true;
            sync = h.Type == ObuClosedLoopKey;
            return !sync; // one layer starting without a key frame makes the sample a non-sync one
        });
        return any && sync;
    }
}

/// <summary>
/// The display order of AV2 temporal units, from their output frames' order hints: the order hint is unwrapped
/// against the largest seen since the last closed loop key frame (which restarts it), and placed after the frames of
/// the previous coded video sequences. Streams with monotonic output order are displayed in decoding order.
/// </summary>
public sealed class Av2DisplayOrder
{
    private readonly Dictionary<int, Av2SequenceHeader> _sequences = [];
    private long _cvsBase;
    private long _cvsFrames;
    private long _cvsFirst = -1;
    private long _maxDisplay = -1;
    private long _decoded;

    /// <summary>The sequence headers carried out of band (the 'av2C' OBUs).</summary>
    public void AddConfiguration(IEnumerable<byte[]> obus)
    {
        ArgumentNullException.ThrowIfNull(obus);
        foreach (var obu in obus)
            AddSequenceHeader(obu);
    }

    private void AddSequenceHeader(ReadOnlySpan<byte> obu)
    {
        if (Av2.ParseObuHeader(obu) is { Type: Av2.ObuSequenceHeader } h && Av2.ParseSequenceHeader(obu[h.Length..]) is { } seq)
            _sequences[seq.SeqHeaderId] = seq;
    }

    /// <summary>True when every sequence header seen asks for monotonic output order (display order = decoding order).</summary>
    public bool Monotonic => _sequences.Count == 0 || _sequences.Values.All(s => s.MonotonicOutputOrder);

    /// <summary>
    /// The display index of the next temporal unit (length-delimited OBUs, with or without delimiter and configuration
    /// OBUs), counted from the first; its decoding index when the order cannot be derived.
    /// </summary>
    public long Next(ReadOnlySpan<byte> temporalUnit)
    {
        var index = _decoded++;
        int? lsbs = null;
        var bits = 0;
        var key = false;
        var firstFrame = true;
        var unitXLayer = -1;
        var unknownOutput = false;
        Av2.ForEachObu(temporalUnit, (h, obu, payload) =>
        {
            if (h.Type == Av2.ObuSequenceHeader)
            {
                AddSequenceHeader(obu);
                return false;
            }

            if (!Av2.IsFrame(h.Type) || h.MLayer != 0)
                return false;
            if (unitXLayer < 0)
                unitXLayer = h.XLayer;
            else if (h.XLayer != unitXLayer)
                return false; // the first extended layer gives the temporal unit's order
            if (firstFrame)
            {
                key = h.Type == Av2.ObuClosedLoopKey;
                firstFrame = false;
            }

            if (Av2.ParseFrameHeader(h.Type, payload, id => _sequences.GetValueOrDefault(id)) is { IsOutput: true } frame)
            {
                if (frame.OrderHintLsbs is { } value)
                    (lsbs, bits, unknownOutput) = (value, frame.OrderHintBits, false);
                else
                    unknownOutput = true;
            }

            return false;
        });

        if (Monotonic)
            return index;
        if (key)
        {
            _cvsBase += _cvsFrames;
            _cvsFrames = 0;
            _cvsFirst = -1;
            _maxDisplay = -1;
        }

        long display;
        if (lsbs is { } l && !unknownOutput && bits > 0)
        {
            display = l;
            if (!key && _maxDisplay >= 0)
            {
                var offset = _maxDisplay - ((1L << bits) >> 1) - l;
                if (offset >= 0)
                    display += ((offset >> bits) + 1) << bits;
            }
        }
        else
        {
            display = _maxDisplay + 1; // output of a frame decoded earlier (derived from a reference)
        }

        _maxDisplay = Math.Max(_maxDisplay, display);
        if (_cvsFirst < 0)
            _cvsFirst = display;
        _cvsFrames++;
        return _cvsBase + display - _cvsFirst;
    }
}
