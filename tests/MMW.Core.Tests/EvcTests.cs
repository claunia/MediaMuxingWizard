using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>MPEG-5 EVC NAL unit header, configuration record ('evcC'), SPS and picture order count helpers.</summary>
public sealed class EvcTests
{
    /// <summary>The 'evcC' of the corpus sample (Baseline, 3840×2160, 10-bit 4:2:0, one SPS and one PPS).</summary>
    private static readonly byte[] CorpusEvcC = Convert.FromHexString(
        "0100D70000000000000000520F000870FF0298000100163200806B80000000000000002001E020021C5B00015099000100043400FB00");

    [Theory]
    [InlineData(0, 123, "Baseline@L4.1")]
    [InlineData(1, 153, "Main@L5.1")]
    [InlineData(3, 0, "Main Still Picture")]
    public void Names_profile_and_level(int profile, int level, string expected) => Assert.Equal(expected, Evc.ProfileLevel(profile, level));

    [Fact]
    public void Reads_the_nal_unit_header()
    {
        // forbidden_zero_bit 0, nal_unit_type_plus1 25 (SPS), nuh_temporal_id 5, reserved 0, extension 0
        byte[] sps = [(25 << 1) | 1, 0b01_00000_0];
        Assert.Equal(Evc.NalSps, Evc.NalType(sps));
        Assert.Equal(5, Evc.TemporalId(sps));
        Assert.True(Evc.IsVcl(Evc.NalIdr));
        Assert.False(Evc.IsVcl(Evc.NalSei));
    }

    [Fact]
    public void Parses_the_corpus_configuration_record_and_its_sps()
    {
        var c = Evc.ParseEvcC(CorpusEvcC);
        Assert.Equal((0, 215, 1, 10, 10, 3840, 2160, 4), (c.ProfileIdc, c.LevelIdc, c.ChromaFormatIdc, c.BitDepthLuma, c.BitDepthChroma, c.Width, c.Height, c.LengthSize));
        Assert.Equal([Evc.NalSps, Evc.NalPps], c.Nals.Select(n => n.Type));
        Assert.Equal(4, Evc.LengthSize(CorpusEvcC));

        var sps = Evc.ParseSps(c.Nals[0].Nal);
        Assert.Equal((0, 215, 3840, 2160, 10), (sps.ProfileIdc, sps.LevelIdc, sps.Width, sps.Height, sps.BitDepthLuma));
        Assert.False(sps.Pocs);
        var pps = Evc.ParsePps(c.Nals[1].Nal);
        Assert.Equal((0, 0, true), (pps.Id, pps.SpsId, pps.SingleTileInPicture));

        // Rebuilt from its parameter sets, the record is the same but for the reserved bits before lengthSizeMinusOne
        // (written as 0, as FFmpeg does; this file's muxer set them).
        var rebuilt = Evc.BuildEvcC([c.Nals[0].Nal], [c.Nals[1].Nal]);
        byte[] expected = [.. CorpusEvcC];
        expected[16] &= 3;
        Assert.Equal(expected, rebuilt);
        Assert.Throws<InvalidDataException>(() => Evc.ParseEvcC(CorpusEvcC.AsSpan(0, 30)));
    }

    /// <summary>
    /// Without slice POC LSBs the picture order count follows the sub-GOP position given by the temporal id: the order
    /// the reference decoder (xevd) reports for an xeve Baseline stream with 16-picture sub-GOPs.
    /// </summary>
    [Fact]
    public void Derives_the_picture_order_count_from_the_sub_gop_structure()
    {
        var sps = new EvcSps { Log2SubGopLength = 4 };
        var slice = new Evc.EvcSliceInfo(sps, true, -1);
        int[] tids = [0, 0, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 1, 2, 3, 3, 4, 4, 4, 4];
        int[] expected = [0, 16, 8, 4, 12, 2, 6, 10, 14, 1, 3, 5, 7, 9, 11, 13, 15, 24, 20, 18, 22, 17, 19, 21, 23];
        var counter = new Evc.PocCounter();
        var pocs = tids.Select((tid, i) => counter.Next(i == 0 ? Evc.NalIdr : Evc.NalNonIdr, tid, slice)).ToArray();
        Assert.Equal(expected, pocs);
    }

    [Fact]
    public void Derives_the_picture_order_count_from_slice_lsbs()
    {
        var sps = new EvcSps { Pocs = true, Log2MaxPocLsb = 4 };
        var counter = new Evc.PocCounter();
        Assert.Equal(0, counter.Next(Evc.NalIdr, 0, new Evc.EvcSliceInfo(sps, true, -1)));
        Assert.Equal(8, counter.Next(Evc.NalNonIdr, 0, new Evc.EvcSliceInfo(sps, true, 8)));
        Assert.Equal(4, counter.Next(Evc.NalNonIdr, 1, new Evc.EvcSliceInfo(sps, true, 4)));
        Assert.Equal(16, counter.Next(Evc.NalNonIdr, 0, new Evc.EvcSliceInfo(sps, true, 0))); // LSB wrap
        Assert.Equal(12, counter.Next(Evc.NalNonIdr, 1, new Evc.EvcSliceInfo(sps, true, 12)));
    }
}
