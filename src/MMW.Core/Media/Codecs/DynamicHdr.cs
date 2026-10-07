using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>Dynamic HDR metadata formats besides HDR10+ and HDR Vivid (which have their own flags).</summary>
[Flags]
public enum DynamicHdrFormats
{
    None = 0,

    /// <summary>SMPTE ST 2094-10 (Dolby display management, application #1) as ATSC A/341 carries it.</summary>
    Smpte2094Part10 = 1,

    /// <summary>ETSI TS 103 433-1 SL-HDR1 (SDR signal with HDR reconstruction metadata).</summary>
    SlHdr1 = 2,

    /// <summary>ETSI TS 103 433-2 SL-HDR2 (PQ signal with display adaptation metadata).</summary>
    SlHdr2 = 4,

    /// <summary>ETSI TS 103 433-3 SL-HDR3 (HLG signal with display adaptation metadata).</summary>
    SlHdr3 = 8,
}

/// <summary>Recognises ST 2094-10 and SL-HDR dynamic metadata in ITU-T T.35 messages.</summary>
public static class DynamicHdr
{
    private const long Av1MetadataItuTT35 = 4;

    /// <summary>"ST 2094-10, SL-HDR2" for the formats in <paramref name="formats"/>.</summary>
    public static string Describe(DynamicHdrFormats formats)
    {
        var names = new List<string>();
        if (formats.HasFlag(DynamicHdrFormats.Smpte2094Part10))
            names.Add("ST 2094-10");
        if (formats.HasFlag(DynamicHdrFormats.SlHdr1))
            names.Add("SL-HDR1");
        if (formats.HasFlag(DynamicHdrFormats.SlHdr2))
            names.Add("SL-HDR2");
        if (formats.HasFlag(DynamicHdrFormats.SlHdr3))
            names.Add("SL-HDR3");
        return string.Join(", ", names);
    }

    /// <summary>
    /// The format of a T.35 message (starting at itu_t_t35_country_code): ATSC A/341 ST 2094-10 (country 0xB5,
    /// provider 0x0031, user identifier "GA94", user_data_type_code 0x09), or the SL-HDR Information message of ETSI
    /// TS 103 433 (provider 0x003A, message code 0x00 or 0x01 for AVC, then sl_hdr_mode_value_minus1).
    /// </summary>
    public static DynamicHdrFormats Classify(ReadOnlySpan<byte> t35)
    {
        if (t35.Length < 5 || t35[0] != 0xB5)
            return DynamicHdrFormats.None;
        var provider = BinaryPrimitives.ReadUInt16BigEndian(t35[1..]);
        if (provider == 0x0031 && t35.Length >= 8 && t35[3..7].SequenceEqual("GA94"u8) && t35[7] == 0x09)
            return DynamicHdrFormats.Smpte2094Part10;
        if (provider == 0x003A && t35[3] is 0x00 or 0x01)
        {
            return (t35[4] >> 4) switch
            {
                0 => DynamicHdrFormats.SlHdr1,
                1 => DynamicHdrFormats.SlHdr2,
                2 => DynamicHdrFormats.SlHdr3,
                _ => DynamicHdrFormats.None,
            };
        }

        return DynamicHdrFormats.None;
    }

    /// <summary>The formats in a frame of <paramref name="codec"/> (length-prefixed NAL units for H.264/HEVC/VVC/EVC).</summary>
    public static DynamicHdrFormats InSample(CodecType codec, ReadOnlySpan<byte> data, int nalLengthSize)
    {
        var found = DynamicHdrFormats.None;
        switch (codec)
        {
            case CodecType.H264 or CodecType.Hevc or CodecType.Vvc or CodecType.Evc:
                Sei.ForEachMessageInSample(data, nalLengthSize, codec, (type, payload) =>
                {
                    if (type == Sei.UserDataRegisteredItuTT35)
                        found |= Classify(payload);
                    return false;
                });
                break;
            case CodecType.Av1:
                Av1.ForEachObu(data, (type, payload) =>
                {
                    var q = 0;
                    if (type == Av1.ObuMetadata && DolbyVision.Leb128(payload, ref q, out var metadataType) && metadataType == Av1MetadataItuTT35)
                        found |= Classify(payload[q..]);
                    return false;
                });
                break;
        }

        return found;
    }
}
