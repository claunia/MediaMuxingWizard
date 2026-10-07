using MMW.Core.Model;

namespace MMW.Core.Media.Codecs;

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
    public static ColorInfo SequenceHeaderColor(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new BitReader(payload);
            var profile = (int)r.Read(3);
            r.Skip(1); // still_picture
            var reduced = r.Flag();
            if (reduced)
            {
                r.Skip(5); // seq_level_idx[0]
            }
            else
            {
                var decoderModelInfo = false;
                var bufferDelayLength = 0;
                if (r.Flag()) // timing_info_present_flag
                {
                    r.Skip(64); // num_units_in_display_tick, time_scale
                    if (r.Flag()) // equal_picture_interval
                        Uvlc(ref r);
                    decoderModelInfo = r.Flag();
                    if (decoderModelInfo)
                    {
                        bufferDelayLength = (int)r.Read(5) + 1;
                        r.Skip(32 + 5 + 5); // num_units_in_decoding_tick, buffer_removal_time_length_minus_1, frame_presentation_time_length_minus_1
                    }
                }

                var initialDisplayDelay = r.Flag();
                var operatingPoints = (int)r.Read(5) + 1;
                for (var i = 0; i < operatingPoints; i++)
                {
                    r.Skip(12); // operating_point_idc
                    if (r.Read(5) > 7) // seq_level_idx
                        r.Skip(1); // seq_tier
                    if (decoderModelInfo && r.Flag()) // decoder_model_present_for_this_op
                        r.Skip(bufferDelayLength * 2 + 1); // decoder/encoder_buffer_delay, low_delay_mode_flag
                    if (initialDisplayDelay && r.Flag())
                        r.Skip(4); // initial_display_delay_minus_1
                }
            }

            var widthBits = (int)r.Read(4) + 1;
            var heightBits = (int)r.Read(4) + 1;
            r.Skip(widthBits + heightBits); // max_frame_width/height_minus_1
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
            if (profile == 2 && highBitDepth)
                r.Skip(1); // twelve_bit
            var mono = profile != 1 && r.Flag();
            if (!r.Flag()) // color_description_present_flag
                return ColorInfo.Unspecified;
            var primaries = (int)r.Read(8);
            var transfer = (int)r.Read(8);
            var matrix = (int)r.Read(8);
            bool fullRange;
            if (mono || !(primaries == 1 && transfer == 13 && matrix == 0))
                fullRange = r.Flag(); // color_range
            else
                fullRange = true; // sRGB / identity: always full range
            return new ColorInfo(primaries, transfer, matrix, fullRange);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return ColorInfo.Unspecified;
        }
    }

    private static void Uvlc(ref BitReader r)
    {
        var leadingZeros = 0;
        while (!r.Flag())
        {
            if (++leadingZeros >= 32)
                return;
        }

        r.Skip(leadingZeros);
    }
}
