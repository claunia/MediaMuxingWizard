namespace MMW.Core.Media.Codecs;

/// <summary>H.264/HEVC/VVC SEI messages (payloadType/payloadSize coded as runs of 0xFF plus a final byte).</summary>
public static class Sei
{
    public const int UserDataRegisteredItuTT35 = 4;
    public const int MasteringDisplayColourVolume = 137;
    public const int ContentLightLevelInfo = 144;
    public const int AlternativeTransferCharacteristics = 147;
    public const int AmbientViewingEnvironment = 148;

    /// <summary>Callback for <see cref="ForEachMessage"/>; return true to stop.</summary>
    public delegate bool MessageVisitor(int type, ReadOnlySpan<byte> payload);

    /// <summary>Walks the sei_message()s of a SEI RBSP (emulation prevention already removed).</summary>
    public static void ForEachMessage(ReadOnlySpan<byte> rbsp, MessageVisitor visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var pos = 0;
        while (pos < rbsp.Length && rbsp[pos] != 0x80) // rbsp_trailing_bits
        {
            int type = 0, size = 0;
            while (pos < rbsp.Length && rbsp[pos] == 0xFF)
            {
                type += 255;
                pos++;
            }

            if (pos >= rbsp.Length)
                return;
            type += rbsp[pos++];
            while (pos < rbsp.Length && rbsp[pos] == 0xFF)
            {
                size += 255;
                pos++;
            }

            if (pos >= rbsp.Length)
                return;
            size += rbsp[pos++];
            if (size > rbsp.Length - pos)
                return;
            if (visit(type, rbsp.Slice(pos, size)))
                return;
            pos += size;
        }
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for the SEI messages of the SEI NAL units in a length-prefixed H.264/HEVC/VVC sample.
    /// </summary>
    public static void ForEachMessageInSample(ReadOnlySpan<byte> data, int nalLengthSize, CodecType codec, MessageVisitor visit)
    {
        foreach (var range in NalUnits.SplitLengthPrefixed(data, nalLengthSize))
        {
            var stop = false;
            ForEachMessageInNal(data[range], codec, (t, p) => stop = visit(t, p));
            if (stop)
                return;
        }
    }

    /// <summary>Calls <paramref name="visit"/> for the messages of one NAL unit when it is a SEI (HEVC/VVC prefix/suffix, H.264 SEI).</summary>
    public static void ForEachMessageInNal(ReadOnlySpan<byte> nal, CodecType codec, MessageVisitor visit)
    {
        var isSei = codec switch
        {
            CodecType.Hevc => NalUnits.HevcType(nal) is Hevc.NalSeiPrefix or Hevc.NalSeiSuffix,
            CodecType.Vvc => Vvc.NalType(nal) is Vvc.NalSeiPrefix or Vvc.NalSeiSuffix,
            _ => NalUnits.H264Type(nal) == 6,
        };
        var headerLength = codec == CodecType.H264 ? 1 : 2;
        if (isSei && nal.Length > headerLength)
            ForEachMessage(NalUnits.ToRbsp(nal[headerLength..]), visit);
    }
}
