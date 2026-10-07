using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>Headers of older video and audio bitstreams: MPEG-1/2, MPEG-4 Part 2, VC-1, DV and MPEG audio.</summary>
public sealed class LegacyVideoTests
{
    /// <summary>A sequence header, a sequence extension (Main@Main, interlaced, 4:2:0) and a sequence display extension (BT.709).</summary>
    [Fact]
    public void Mpeg2_sequence_extensions_give_profile_level_and_colour()
    {
        byte[] data =
        [
            0, 0, 1, 0xB3, 0x2D, 0x01, 0xE0, 0x24, 0xFF, 0xFF, 0xE0, 0x00,
            0, 0, 1, 0xB5, 0x14, 0x82, 0x00, 0x01, 0x00, 0x00,
            0, 0, 1, 0xB5, 0x2B, 0x01, 0x01, 0x01, 0x16, 0x82, 0x01, 0xE0,
            0, 0, 1, 0xB8, 0, 0, 0, 0,
        ];
        var info = Mpeg12Video.Describe(data);
        Assert.NotNull(info);
        Assert.Equal("Main@Main, interlaced", info.ProfileLevel);
        Assert.Equal(new ColorInfo(1, 1, 1), info.Color);
    }

    [Fact]
    public void Mpeg1_has_no_profile() =>
        Assert.Equal(string.Empty, Mpeg12Video.Describe([0, 0, 1, 0xB3, 0x2D, 0x01, 0xE0, 0x24, 0xFF, 0xFF, 0xE0, 0x00, 0, 0, 1, 0xB8, 0, 0, 0, 0])?.ProfileLevel);

    [Theory]
    [InlineData(0x48, "Main@Main")]
    [InlineData(0x46, "Main@High-1440")]
    [InlineData(0x14, "High@High")]
    [InlineData(0x85, "4:2:2@Main")]
    [InlineData(0x58, "Simple@Main")]
    public void Mpeg2_profile_and_level(int indication, string expected) => Assert.Equal(expected, Mpeg12Video.ProfileLevel(indication));

    [Theory]
    [InlineData(0x01, "Simple@L1")]
    [InlineData(0x08, "Simple@L0")]
    [InlineData(0x04, "Simple@L4a")]
    [InlineData(0xF5, "Advanced Simple@L5")]
    [InlineData(0xF7, "Advanced Simple@L3b")]
    [InlineData(0x91, "Advanced Real Time Simple@L1")]
    [InlineData(0x00, "")]
    public void Mpeg4_profile_and_level(int indication, string expected) => Assert.Equal(expected, Mpeg4Part2.ProfileLevel(indication));

    /// <summary>The Advanced profile sequence header of the corpus VC-1 file (level 3, progressive).</summary>
    [Fact]
    public void Vc1_advanced_sequence_header()
    {
        var info = Vc1.Describe("WVC1", Convert.FromHexString("270000010FDBFE27F16788800000010E10449FC5"));
        Assert.NotNull(info);
        Assert.Equal("Advanced@L3", info.ProfileLevel);
    }

    [Theory]
    [InlineData(0x0A, "Simple")]
    [InlineData(0x4A, "Main")]
    [InlineData(0x8A, "Complex")]
    public void Wmv9_profile_comes_from_struct_c(byte first, string expected) =>
        Assert.Equal(expected, Vc1.Describe("WMV3", [first, 0x00, 0x00, 0x01])?.ProfileLevel);

    [Fact]
    public void Other_vfw_codecs_have_no_vc1_description() => Assert.Null(Vc1.Describe("MP43", [1, 2, 3, 4]));

    /// <summary>A DIF header (625/50, IEC 61834) with VS (DV25) and VSC (16:9) packs in the first VAUX block.</summary>
    [Fact]
    public void Dv_frame_gives_system_sampling_and_aspect()
    {
        var frame = new byte[80 * 150];
        frame[3] = 0x80; // DSF: 625/50
        var vaux = 80 * 3 + 3;
        frame[vaux] = 0x60; // VS pack, stype 0
        frame[vaux + 5] = 0x61; // VSC pack
        frame[vaux + 5 + 2] = 0x02; // 16:9
        Assert.Equal("DV 625/50 4:2:0, 16:9", DvVideo.Describe(frame)?.ProfileLevel);
        frame[3] = 0;
        frame[vaux + 3] = 0x04; // DVCPRO50
        frame[vaux + 5 + 2] = 0;
        Assert.Equal("DVCPRO50 525/60 4:2:2, 4:3", DvVideo.Describe(frame)?.ProfileLevel);
    }

    [Fact]
    public void Mpeg_audio_constant_and_variable_rates()
    {
        ReadOnlyMemory<byte> f128 = new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, f160 = new byte[] { 0xFF, 0xFB, 0xA0, 0x64 };
        Assert.Equal(128, MpegAudio.BitRate(f128.Span));
        Assert.Equal("CBR 128 kbps, joint stereo", MpegAudio.DescribeStream([f128, f128, f128]));
        Assert.Equal("VBR 144 kbps, joint stereo", MpegAudio.DescribeStream([f128, f160]));

        // A LAME summary frame ("Xing" for VBR) is not audio: it tells the mode and the encoder.
        var xing = new byte[200];
        new byte[] { 0xFF, 0xFB, 0x90, 0x64 }.CopyTo(xing, 0);
        "Xing"u8.CopyTo(xing.AsSpan(36));
        "LAME3.100"u8.CopyTo(xing.AsSpan(156));
        Assert.Equal("VBR 128 kbps, joint stereo, LAME3.100", MpegAudio.DescribeStream([xing, f128]));
        Assert.Equal(384, MpegAudio.BitRate([0xFF, 0xFD, 0xE4, 0x00])); // MPEG-1 Layer II
    }
}
