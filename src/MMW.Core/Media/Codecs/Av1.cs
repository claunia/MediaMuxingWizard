using MMW.Core.Model;

namespace MMW.Core.Media.Codecs;

/// <summary>The fields of an AV1 sequence header OBU (§5.5) that describe the stream.</summary>
public sealed record Av1SequenceHeader
{
    public int Profile { get; init; }

    public int Level { get; init; }

    public int Tier { get; init; }

    public bool StillPicture { get; init; }

    public bool ReducedStillPictureHeader { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int BitDepth { get; init; }

    public bool Monochrome { get; init; }

    public bool SubsamplingX { get; init; }

    public bool SubsamplingY { get; init; }

    public int ChromaSamplePosition { get; init; }

    /// <summary>initial_display_delay_minus_1 of the first operating point; -1 when absent.</summary>
    public int InitialDisplayDelayMinus1 { get; init; } = -1;

    public ColorInfo Color { get; init; } = ColorInfo.Unspecified;

    /// <summary>Frame rate from timing_info (0 when absent).</summary>
    public double FrameRate { get; init; }
}

/// <summary>AV1 bitstream helpers (AV1 Bitstream &amp; Decoding Process Specification).</summary>
public static class Av1
{
    public const int ObuSequenceHeader = 1;
    public const int ObuMetadata = 5;

    /// <summary>Callback for <see cref="ForEachObu"/>; return true to stop.</summary>
    public delegate bool ObuVisitor(int type, ReadOnlySpan<byte> payload);

    /// <summary>Walks the OBUs of a temporal unit (low-overhead format: OBUs with size fields, the last may omit it).</summary>
    public static void ForEachObu(ReadOnlySpan<byte> data, ObuVisitor visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var pos = 0;
        while (pos < data.Length)
        {
            var header = data[pos];
            var type = (header >> 3) & 0x0F;
            var p = pos + 1 + ((header & 0x04) != 0 ? 1 : 0);
            long size;
            if ((header & 0x02) != 0)
            {
                if (!DolbyVision.Leb128(data, ref p, out size))
                    return;
            }
            else
            {
                size = data.Length - p;
            }

            if (size < 0 || p > data.Length || size > data.Length - p)
                return;
            if (visit(type, data.Slice(p, (int)size)))
                return;
            pos = p + (int)size;
        }
    }

    /// <summary>
    /// The colour description of a sequence header OBU payload (§5.5.1 sequence_header_obu, §5.5.2 color_config);
    /// unspecified when the header has none or cannot be parsed.
    /// </summary>
    public static ColorInfo SequenceHeaderColor(ReadOnlySpan<byte> payload) => ParseSequenceHeader(payload)?.Color ?? ColorInfo.Unspecified;

    /// <summary>Parses a sequence header OBU payload (§5.5) through color_config; null when it cannot be read.</summary>
    public static Av1SequenceHeader? ParseSequenceHeader(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new BitReader(payload);
            var profile = (int)r.Read(3);
            var still = r.Flag();
            var reduced = r.Flag();
            int level, tier = 0;
            double frameRate = 0;
            var initialDelay = -1;
            if (reduced)
            {
                level = (int)r.Read(5);
            }
            else
            {
                var decoderModelInfo = false;
                var bufferDelayLength = 0;
                if (r.Flag()) // timing_info_present_flag
                {
                    var unitsPerTick = r.Read(32);
                    var timeScale = r.Read(32);
                    var ticksPerPicture = r.Flag() ? r.Ue() + 1 : 1; // equal_picture_interval: num_ticks_per_picture_minus_1
                    if (unitsPerTick > 0 && timeScale > 0)
                        frameRate = timeScale / ((double)unitsPerTick * ticksPerPicture);
                    decoderModelInfo = r.Flag();
                    if (decoderModelInfo)
                    {
                        bufferDelayLength = (int)r.Read(5) + 1;
                        r.Skip(32 + 5 + 5); // num_units_in_decoding_tick, buffer_removal_time_length_minus_1, frame_presentation_time_length_minus_1
                    }
                }

                var initialDisplayDelay = r.Flag();
                var operatingPoints = (int)r.Read(5) + 1;
                level = 0;
                for (var i = 0; i < operatingPoints; i++)
                {
                    r.Skip(12); // operating_point_idc
                    var opLevel = (int)r.Read(5);
                    var opTier = opLevel > 7 ? (int)r.Read(1) : 0;
                    if (i == 0)
                        (level, tier) = (opLevel, opTier);
                    if (decoderModelInfo && r.Flag()) // decoder_model_present_for_this_op
                        r.Skip(bufferDelayLength * 2 + 1); // decoder/encoder_buffer_delay, low_delay_mode_flag
                    if (initialDisplayDelay && r.Flag())
                    {
                        var delay = (int)r.Read(4); // initial_display_delay_minus_1
                        if (i == 0)
                            initialDelay = delay;
                    }
                }
            }

            var widthBits = (int)r.Read(4) + 1;
            var heightBits = (int)r.Read(4) + 1;
            var width = (int)r.Read(widthBits) + 1;
            var height = (int)r.Read(heightBits) + 1;
            if (!reduced && r.Flag()) // frame_id_numbers_present_flag
                r.Skip(4 + 3);
            r.Skip(3); // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
            if (!reduced)
            {
                r.Skip(4); // enable_interintra_compound, enable_masked_compound, enable_warped_motion, enable_dual_filter
                var orderHint = r.Flag();
                if (orderHint)
                    r.Skip(2); // enable_jnt_comp, enable_ref_frame_mvs
                var forceScreenContentTools = r.Flag() ? 2u : r.Read(1); // seq_choose_screen_content_tools
                if (forceScreenContentTools > 0 && !r.Flag()) // seq_choose_integer_mv
                    r.Skip(1); // seq_force_integer_mv
                if (orderHint)
                    r.Skip(3); // order_hint_bits_minus_1
            }

            r.Skip(3); // enable_superres, enable_cdef, enable_restoration

            // color_config()
            var highBitDepth = r.Flag();
            var twelveBit = profile == 2 && highBitDepth && r.Flag();
            var bitDepth = twelveBit ? 12 : highBitDepth ? 10 : 8;
            var mono = profile != 1 && r.Flag();
            var described = r.Flag(); // color_description_present_flag
            var (primaries, transfer, matrix) = described ? ((int)r.Read(8), (int)r.Read(8), (int)r.Read(8)) : (2, 2, 2);
            bool fullRange, ssx, ssy;
            var chromaPosition = 0;
            if (mono)
            {
                fullRange = r.Flag();
                (ssx, ssy) = (true, true);
            }
            else if (primaries == 1 && transfer == 13 && matrix == 0)
            {
                fullRange = true; // sRGB / identity: always full range, 4:4:4
                (ssx, ssy) = (false, false);
            }
            else
            {
                fullRange = r.Flag(); // color_range
                (ssx, ssy) = profile switch
                {
                    0 => (true, true),
                    1 => (false, false),
                    _ when bitDepth == 12 => r.Flag() is var x && x ? (true, r.Flag()) : (false, false),
                    _ => (true, false),
                };
                if (ssx && ssy)
                    chromaPosition = (int)r.Read(2);
            }

            return new Av1SequenceHeader
            {
                Profile = profile,
                Level = level,
                Tier = tier,
                StillPicture = still,
                ReducedStillPictureHeader = reduced,
                Width = width,
                Height = height,
                BitDepth = bitDepth,
                Monochrome = mono,
                SubsamplingX = ssx,
                SubsamplingY = ssy,
                ChromaSamplePosition = chromaPosition,
                InitialDisplayDelayMinus1 = initialDelay,
                Color = described ? new ColorInfo(primaries, transfer, matrix, fullRange) : ColorInfo.Unspecified,
                FrameRate = frameRate,
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>"Main@L4.0" (Main, High, Professional) with the tier when it is the high one.</summary>
    public static string ProfileLevel(int profile, int level, int tier)
    {
        var name = profile switch
        {
            0 => "Main",
            1 => "High",
            2 => "Professional",
            _ => $"Profile {profile}",
        };
        var levelName = level switch
        {
            < 24 => $"L{2 + (level >> 2)}.{level & 3}",
            31 => "max",
            _ => $"level {level}",
        };
        return $"{name}@{levelName}{(tier == 1 ? " High tier" : string.Empty)}";
    }

    /// <summary>The profile and level an AV1CodecConfigurationRecord ('av1C') announces; empty when it is not one.</summary>
    public static string ProfileLevel(ReadOnlySpan<byte> av1C) =>
        av1C.Length >= 4 && av1C[0] == 0x81 ? ProfileLevel(av1C[1] >> 5, av1C[1] & 0x1F, av1C[2] >> 7) : string.Empty;

    /// <summary>
    /// The AV1CodecConfigurationRecord ('av1C', also Matroska CodecPrivate) for a sequence header OBU (stored as its
    /// configOBUs, with its size field).
    /// </summary>
    public static byte[] BuildAv1C(Av1SequenceHeader header, ReadOnlySpan<byte> sequenceHeaderObu)
    {
        ArgumentNullException.ThrowIfNull(header);
        byte[] record =
        [
            0x81,
            (byte)((header.Profile << 5) | (header.Level & 0x1F)),
            (byte)((header.Tier << 7) | (header.BitDepth > 8 ? 0x40 : 0) | (header.BitDepth == 12 ? 0x20 : 0) | (header.Monochrome ? 0x10 : 0) |
                   (header.SubsamplingX ? 0x08 : 0) | (header.SubsamplingY ? 0x04 : 0) | (header.ChromaSamplePosition & 3)),
            0, // initial_presentation_delay_present = 0 (counted in samples, not the sequence header's frames; as FFmpeg writes it)
        ];
        return [.. record, .. sequenceHeaderObu];
    }

    /// <summary>The OBUs of a temporal unit with their sizes; low-overhead data where the last OBU may omit its size.</summary>
    private static List<(int Type, int Start, int HeaderLength, int PayloadStart, int End)> Split(ReadOnlySpan<byte> data)
    {
        var list = new List<(int, int, int, int, int)>();
        var pos = 0;
        while (pos < data.Length)
        {
            var header = data[pos];
            var headerLength = 1 + ((header & 0x04) != 0 ? 1 : 0);
            var p = pos + headerLength;
            long size;
            if ((header & 0x02) != 0)
            {
                if (!DolbyVision.Leb128(data, ref p, out size))
                    break;
            }
            else
            {
                size = data.Length - p;
            }

            if (size < 0 || p > data.Length || size > data.Length - p)
                break;
            list.Add(((header >> 3) & 0x0F, pos, headerLength, p, p + (int)size));
            pos = p + (int)size;
        }

        return list;
    }

    public const int ObuTemporalDelimiter = 2;
    public const int ObuFrameHeader = 3;
    public const int ObuFrame = 6;
    public const int ObuPadding = 15;

    /// <summary>
    /// A temporal unit as an ISOBMFF / Matroska sample (AV1-ISOBMFF §2.4): without temporal delimiters and padding, every
    /// OBU with its size field.
    /// </summary>
    public static byte[] ToSample(ReadOnlySpan<byte> temporalUnit)
    {
        var o = new List<byte>(temporalUnit.Length + 8);
        foreach (var (type, start, headerLength, payloadStart, end) in Split(temporalUnit))
        {
            if (type is ObuTemporalDelimiter or ObuPadding)
                continue;
            if ((temporalUnit[start] & 0x02) != 0)
            {
                o.AddRange(temporalUnit[start..end]);
                continue;
            }

            o.Add((byte)(temporalUnit[start] | 0x02));
            if (headerLength == 2)
                o.Add(temporalUnit[start + 1]);
            Av2.WriteLeb128(o, end - payloadStart);
            o.AddRange(temporalUnit[payloadStart..end]);
        }

        return [.. o];
    }

    /// <summary>
    /// The 'av1C' record (and the sequence header it describes) from the sequence header OBU of a sample or temporal
    /// unit, for tracks stored without one; null when the sample has no readable sequence header.
    /// </summary>
    public static (byte[] Av1C, Av1SequenceHeader Header)? ConfigurationFromSample(ReadOnlySpan<byte> sample)
    {
        var obu = FindSequenceHeader(ToSample(sample)); // with size fields, as configOBUs need them
        if (obu.IsEmpty)
            return null;
        var pos = 1 + ((obu[0] & 0x04) != 0 ? 1 : 0);
        if (!DolbyVision.Leb128(obu, ref pos, out _) || ParseSequenceHeader(obu[pos..]) is not { } header)
            return null;
        return (BuildAv1C(header, obu), header);
    }

    /// <summary>The first sequence header OBU (header, size field and payload) of a temporal unit; empty when it has none.</summary>
    public static ReadOnlySpan<byte> FindSequenceHeader(ReadOnlySpan<byte> sample)
    {
        foreach (var (type, start, _, _, end) in Split(sample))
        {
            if (type == ObuSequenceHeader)
                return sample[start..end];
        }

        return [];
    }

    /// <summary>
    /// A sync sample (AV1-ISOBMFF §2.4): its first frame is a key frame that is shown (or the stream uses reduced still
    /// picture headers).
    /// </summary>
    public static bool IsSync(ReadOnlySpan<byte> sample, bool reducedStillPictureHeader)
    {
        foreach (var (type, _, _, payloadStart, end) in Split(sample))
        {
            if (type is not (ObuFrameHeader or ObuFrame))
                continue;
            if (reducedStillPictureHeader)
                return true;
            if (end <= payloadStart)
                return false;
            var b = sample[payloadStart];
            // show_existing_frame(1), frame_type(2), show_frame(1)
            return (b & 0x80) == 0 && ((b >> 5) & 3) == 0 && (b & 0x10) != 0;
        }

        return false;
    }

    /// <summary>
    /// Annex B temporal units (temporal_unit_size, then frame units of obu_length-prefixed OBUs without size fields) as
    /// low-overhead temporal units.
    /// </summary>
    public static byte[] FromAnnexB(ReadOnlySpan<byte> temporalUnit)
    {
        var o = new List<byte>(temporalUnit.Length + 16);
        var pos = 0;
        while (pos < temporalUnit.Length)
        {
            if (!DolbyVision.Leb128(temporalUnit, ref pos, out var frameUnitSize) || frameUnitSize > temporalUnit.Length - pos)
                break;
            var end = pos + (int)frameUnitSize;
            while (pos < end)
            {
                if (!DolbyVision.Leb128(temporalUnit, ref pos, out var obuLength) || obuLength < 1 || obuLength > end - pos)
                    return [.. o];
                var obu = temporalUnit.Slice(pos, (int)obuLength);
                pos += (int)obuLength;
                if ((obu[0] & 0x02) != 0)
                {
                    o.AddRange(obu); // already has its size field
                    continue;
                }

                var headerLength = 1 + ((obu[0] & 0x04) != 0 ? 1 : 0);
                o.Add((byte)(obu[0] | 0x02));
                if (headerLength == 2)
                    o.Add(obu[1]);
                Av2.WriteLeb128(o, obu.Length - headerLength);
                o.AddRange(obu[headerLength..]);
            }
        }

        return [.. o];
    }
}
