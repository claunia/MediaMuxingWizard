using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// Recognises HDR10+ dynamic metadata (SMPTE ST 2094-40 application 4, as ITU-T T.35 messages registered by
/// Samsung) in HEVC/H.264 SEI, AV1 metadata OBUs and Matroska BlockAdditions.
/// </summary>
public static class Hdr10Plus
{
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
        var found = false;
        Sei.ForEachMessageInSample(data, nalLengthSize, hevc, (type, payload) =>
            found = type == Sei.UserDataRegisteredItuTT35 && IsHdr10PlusT35(payload));
        return found;
    }

    private static bool InAv1(ReadOnlySpan<byte> data)
    {
        var found = false;
        Av1.ForEachObu(data, (type, payload) =>
        {
            var q = 0;
            found = type == Av1.ObuMetadata && DolbyVision.Leb128(payload, ref q, out var metadataType) &&
                    metadataType == Av1MetadataItuTT35 && IsHdr10PlusT35(payload[q..]);
            return found;
        });
        return found;
    }
}
