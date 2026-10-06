using MMW.Core.Media;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>HDR10+ (SMPTE ST 2094-40 as ITU-T T.35) in HEVC/H.264 SEI, AV1 metadata OBUs and Matroska BlockAdditions.</summary>
public sealed class Hdr10PlusTests
{
    // itu_t_t35_country_code, terminal_provider_code, terminal_provider_oriented_code, application_identifier,
    // application_version, then a few metadata bytes (including 00 00 that need emulation prevention in NAL units).
    private static readonly byte[] T35 = [0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01, 0x40, 0x00, 0x00, 0x01, 0x80];

    // Dolby Vision RPUs in AV1 also use T.35 (provider 0x003B): not HDR10+.
    private static readonly byte[] DolbyT35 = [0xB5, 0x00, 0x3B, 0x00, 0x00, 0x08, 0x00, 0x37, 0xCD, 0x08];

    private static byte[] LengthPrefixed(params byte[][] nals)
    {
        var data = new List<byte>();
        foreach (var nal in nals)
        {
            data.AddRange([(byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length]);
            data.AddRange(nal);
        }

        return [.. data];
    }

    /// <summary>A SEI NAL unit with an unrelated message first, then user_data_registered_itu_t_t35 (with emulation prevention).</summary>
    private static byte[] Sei(byte[] header, byte[] t35)
    {
        var rbsp = new List<byte> { 5, 2, 0xAA, 0xBB, 4, (byte)t35.Length }; // user_data_unregistered stub, then type 4
        rbsp.AddRange(t35);
        rbsp.Add(0x80);
        var nal = new List<byte>(header);
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros == 2 && b <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }

            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. nal];
    }

    private static byte[] Av1Metadata(byte[] t35) => [(5 << 3) | 2, (byte)(t35.Length + 1), 4, .. t35]; // OBU_METADATA, ITU-T T.35

    [Fact]
    public void Recognises_the_samsung_application_4_message()
    {
        Assert.True(Hdr10Plus.IsHdr10PlusT35(T35));
        Assert.False(Hdr10Plus.IsHdr10PlusT35(DolbyT35));
        Assert.False(Hdr10Plus.IsHdr10PlusT35(T35.AsSpan(0, 5)));
        byte[] otherApp = [.. T35];
        otherApp[5] = 5;
        Assert.False(Hdr10Plus.IsHdr10PlusT35(otherApp));
    }

    [Fact]
    public void Finds_hdr10plus_in_hevc_and_h264_sei()
    {
        byte[] slice = [1 << 1, 1, 0xAF, 0x88];
        Assert.True(Hdr10Plus.InSample(CodecType.Hevc, LengthPrefixed(Sei([39 << 1, 1], T35), slice), 4)); // prefix SEI
        Assert.False(Hdr10Plus.InSample(CodecType.Hevc, LengthPrefixed(Sei([39 << 1, 1], DolbyT35), slice), 4));
        Assert.False(Hdr10Plus.InSample(CodecType.Hevc, LengthPrefixed(slice), 4));
        Assert.True(Hdr10Plus.InSample(CodecType.H264, LengthPrefixed(Sei([6], T35), [0x65, 0x88]), 4));
    }

    [Fact]
    public void Finds_hdr10plus_in_av1_metadata_obus()
    {
        byte[] temporalDelimiter = [(2 << 3) | 2, 0];
        byte[] frame = [(6 << 3) | 2, 3, 0x10, 0x20, 0x30];
        Assert.True(Hdr10Plus.InSample(CodecType.Av1, [.. temporalDelimiter, .. Av1Metadata(T35), .. frame], 0));
        Assert.False(Hdr10Plus.InSample(CodecType.Av1, [.. temporalDelimiter, .. Av1Metadata(DolbyT35), .. frame], 0));
        Assert.False(Hdr10Plus.InSample(CodecType.Av1, [.. temporalDelimiter, .. frame], 0));
    }

    [Fact]
    public void Finds_hdr10plus_in_block_additions()
    {
        byte[] vp9Frame = [0x82, 0x49, 0x83];
        Assert.True(Hdr10Plus.InSample(CodecType.Vp9, vp9Frame, 0, [new BlockAddition(BlockAddition.ItuT35, T35)]));
        Assert.False(Hdr10Plus.InSample(CodecType.Vp9, vp9Frame, 0, [new BlockAddition(1, T35)])); // other BlockAddID
        Assert.False(Hdr10Plus.InSample(CodecType.Vp9, vp9Frame, 0));
    }
}
