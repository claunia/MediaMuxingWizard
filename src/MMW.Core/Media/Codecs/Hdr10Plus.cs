using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// Recognises HDR10+ dynamic metadata (SMPTE ST 2094-40 application 4, as ITU-T T.35 messages registered by
/// Samsung) in HEVC/H.264 SEI, AV1 metadata OBUs and Matroska BlockAdditions.
/// </summary>
public static class Hdr10Plus
{
    private const int SeiPayloadUserDataRegisteredItuTT35 = 4;
    private const int Av1ObuMetadata = 5;
    private const long Av1MetadataItuTT35 = 4;

    /// <summary>
    /// True when <paramref name="t35"/> (starting at itu_t_t35_country_code) is an HDR10+ message: country 0xB5 (USA),
    /// terminal provider 0x003C (Samsung), provider-oriented code 0x0001, application identifier 4.
    /// </summary>
    public static bool IsHdr10PlusT35(ReadOnlySpan<byte> t35) =>
        t35.Length >= 6 && t35[0] == 0xB5 && BinaryPrimitives.ReadUInt16BigEndian(t35[1..]) == 0x003C &&
        BinaryPrimitives.ReadUInt16BigEndian(t35[3..]) == 0x0001 && t35[5] == 4;

    /// <summary>True when a frame of <paramref name="codec"/> (length-prefixed NAL units for H.264/HEVC) or its block additions carry HDR10+.</summary>
    public static bool InSample(CodecType codec, ReadOnlySpan<byte> data, int nalLengthSize, IReadOnlyList<BlockAddition>? additions = null)
    {
        if (additions is not null)
        {
            foreach (var a in additions)
            {
                if (a.Id == BlockAddition.ItuT35 && IsHdr10PlusT35(a.Data.Span))
                    return true;
            }
        }

        return codec switch
        {
            CodecType.Hevc => InNalUnits(data, nalLengthSize, hevc: true),
            CodecType.H264 => InNalUnits(data, nalLengthSize, hevc: false),
            CodecType.Av1 => InAv1(data),
            _ => false,
        };
    }

    private static bool InNalUnits(ReadOnlySpan<byte> data, int nalLengthSize, bool hevc)
    {
        foreach (var range in NalUnits.SplitLengthPrefixed(data, nalLengthSize))
        {
            var nal = data[range];
            var isSei = hevc ? NalUnits.HevcType(nal) is 39 or 40 : NalUnits.H264Type(nal) == 6;
            if (!isSei)
                continue;
            var headerLength = hevc ? 2 : 1;
            if (nal.Length > headerLength && InSeiMessages(NalUnits.ToRbsp(nal[headerLength..])))
                return true;
        }

        return false;
    }

    /// <summary>Walks sei_message()s: payloadType and payloadSize are coded as runs of 0xFF plus a final byte.</summary>
    private static bool InSeiMessages(ReadOnlySpan<byte> rbsp)
    {
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
                return false;
            type += rbsp[pos++];
            while (pos < rbsp.Length && rbsp[pos] == 0xFF)
            {
                size += 255;
                pos++;
            }

            if (pos >= rbsp.Length)
                return false;
            size += rbsp[pos++];
            if (size > rbsp.Length - pos)
                return false;
            if (type == SeiPayloadUserDataRegisteredItuTT35 && IsHdr10PlusT35(rbsp.Slice(pos, size)))
                return true;
            pos += size;
        }

        return false;
    }

    private static bool InAv1(ReadOnlySpan<byte> data)
    {
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
                    return false;
            }
            else
            {
                size = data.Length - p;
            }

            if (size < 0 || p + size > data.Length)
                return false;
            if (type == Av1ObuMetadata)
            {
                var payload = data.Slice(p, (int)size);
                var q = 0;
                if (DolbyVision.Leb128(payload, ref q, out var metadataType) && metadataType == Av1MetadataItuTT35 && IsHdr10PlusT35(payload[q..]))
                    return true;
            }

            pos = p + (int)size;
        }

        return false;
    }
}
