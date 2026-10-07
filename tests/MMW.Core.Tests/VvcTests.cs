using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>VVC (H.266) NAL unit header, PPS and configuration record ('vvcC') helpers.</summary>
public sealed class VvcTests
{
    [Theory]
    [InlineData(1, 32, false, "Main 10@L2.0")]
    [InlineData(1, 83, false, "Main 10@L5.1")]
    [InlineData(33, 102, true, "Main 10 4:4:4@L6.2 High")]
    [InlineData(65, 0, false, "Main 10 Still Picture")]
    [InlineData(-1, 51, false, "")]
    public void Names_profile_and_level(int profile, int level, bool highTier, string expected) =>
        Assert.Equal(expected, Vvc.ProfileLevel(profile, level, highTier));

    [Fact]
    public void Reads_the_nal_unit_header_and_pps_identifiers()
    {
        byte[] pps = [0, (Vvc.NalPps << 3) | 1, 0b000101_00, 0b11_000000, 0x80]; // pps_pic_parameter_set_id 5, sps 3
        Assert.Equal(Vvc.NalPps, Vvc.NalType(pps));
        Assert.Equal(0, Vvc.TemporalId(pps));
        Assert.Equal(new VvcPps(5, 3), Vvc.ParsePps(pps));
        Assert.True(Vvc.IsIrap(Vvc.NalCra));
        Assert.False(Vvc.IsIrap(Vvc.NalGdr));
        Assert.True(Vvc.IsVcl(Vvc.NalRasl));
        Assert.False(Vvc.IsVcl(Vvc.NalPictureHeader));
    }

    /// <summary>A record with two sub-layers (one sub-layer level), a sub-profile and a DCI array (no num_nalus field).</summary>
    [Fact]
    public void Parses_a_record_with_sublayers_sub_profiles_and_a_dci()
    {
        byte[] sps = [0, (Vvc.NalSps << 3) | 1, 0xAA];
        byte[] dci = [0, (Vvc.NalDci << 3) | 1, 0xBB];
        byte[] record =
        [
            0xF8 | (3 << 1) | 1, // LengthSizeMinusOne 3, ptl_present_flag
            0x00, (2 << 4) | (1 << 2) | 1, // ols_idx 0, num_sublayers 2, constant_frame_rate 1, chroma 4:2:0
            (2 << 5) | 0x1F, // bit_depth_minus8 2
            1, (1 << 1) | 1, 83, 0x80, // one constraint byte, Main 10, high tier, level 5.1, frame-only flag
            0b1, 80, // sub-layer 0 level present: 80
            1, 0x12, 0x34, 0x56, 0x78, // one sub-profile
            0x07, 0x80, 0x04, 0x38, 0, 0, // 1920×1080, avg_frame_rate
            2,
            0x80 | Vvc.NalDci, 0, 3, .. dci,
            0x80 | Vvc.NalSps, 0, 1, 0, 3, .. sps,
        ];
        var c = Vvc.ParseVvcC(record);
        Assert.Equal((4, 1, true, 83, 1, 10, 1920, 1080), (c.LengthSize, c.ProfileIdc, c.HighTier, c.LevelIdc, c.ChromaFormatIdc, c.BitDepth, c.MaxWidth, c.MaxHeight));
        Assert.Equal([Vvc.NalDci, Vvc.NalSps], c.Nals.Select(n => n.Type));
        Assert.Equal(sps, c.Nals[1].Nal);
        Assert.Equal("Main 10@L5.1 High", Vvc.ProfileLevel(record));

        var incomplete = Vvc.MarkArraysComplete(record, false);
        Assert.All(Vvc.ParseVvcC(incomplete).Nals, n => Assert.False(n.Complete));
        Assert.Equal(record.Length, incomplete.Length);
        Assert.Throws<InvalidDataException>(() => Vvc.ParseVvcC(record.AsSpan(0, 20)));
    }
}
