using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>
/// Recognises HDR Vivid dynamic metadata (CUVA, T/UWA 005.1): ITU-T T.35 messages registered by China (country 0x26,
/// terminal provider 0x0004, provider-oriented code 0x0005) in H.264/HEVC/VVC/EVC SEI and AV1 metadata OBUs, and the
/// HDR picture extension of AVS2/AVS3.
/// </summary>
public static class HdrVivid
{
    private const long Av1MetadataItuTT35 = 4;

    /// <summary>True when <paramref name="t35"/> (starting at itu_t_t35_country_code) is an HDR Vivid message.</summary>
    public static bool IsHdrVividT35(ReadOnlySpan<byte> t35) =>
        t35.Length >= 5 && t35[0] == 0x26 && BinaryPrimitives.ReadUInt16BigEndian(t35[1..]) == 0x0004 &&
        BinaryPrimitives.ReadUInt16BigEndian(t35[3..]) == 0x0005;

    /// <summary>True when a frame of <paramref name="codec"/> (length-prefixed NAL units for H.264/HEVC/VVC/EVC) carries HDR Vivid.</summary>
    public static bool InSample(CodecType codec, ReadOnlySpan<byte> data, int nalLengthSize)
    {
        switch (codec)
        {
            case CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Evc:
            {
                var found = false;
                Sei.ForEachMessageInSample(data, nalLengthSize, codec, (type, payload) =>
                    found = type == Sei.UserDataRegisteredItuTT35 && IsHdrVividT35(payload));
                return found;
            }

            case CodecType.Av1:
            {
                var found = false;
                Av1.ForEachObu(data, (type, payload) =>
                {
                    var q = 0;
                    found = type == Av1.ObuMetadata && DolbyVision.Leb128(payload, ref q, out var metadataType) &&
                            metadataType == Av1MetadataItuTT35 && IsHdrVividT35(payload[q..]);
                    return found;
                });
                return found;
            }

            case CodecType.Av2:
                return Av2.ForEachT35(data, t35 => IsHdrVividT35(t35));
            case CodecType.Avs2 or CodecType.Avs3:
                return Avs.HasHdrDynamicMetadata(data);
            default:
                return false;
        }
    }
}
