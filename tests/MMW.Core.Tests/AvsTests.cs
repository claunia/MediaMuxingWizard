using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>AVS1/AVS2/AVS3 sequence and picture headers, colour mapping and presentation order.</summary>
public sealed class AvsTests
{
    /// <summary>xavs (AVS1 Jizhun, level 4.0): sequence header of a 320×176, 25 fps stream.</summary>
    private static readonly byte[] Avs1Sequence = Convert.FromHexString("000001B020408280058244C01F48002EFD80");

    /// <summary>xavs2 (AVS2 Main, level 0x14): sequence header and user data of a 320×176, 25 fps stream.</summary>
    private static readonly byte[] Avs2Sequence = Convert.FromHexString(
        "000001B02014814002C122604E2400100001582645101A411D0249134094499025890610D0A20902291243842308000001B2786176733220656E636F646572");

    /// <summary>
    /// uavs3enc (AVS3 profile 0x22, level 0x6A) sequence header followed by a sequence display extension (BT.2020, PQ,
    /// BT.2020 NCL) and a mastering display extension (P3-D65, 1000 / 0.005 cd/m², MaxCLL 1000, MaxFALL 400).
    /// </summary>
    private static readonly byte[] Avs3SequenceWithHdr = Convert.FromHexString(
        "000001B0226A8828102C1262700002000FFFFFFD089022088256122B0995850AC2A0A5B050CA0999813118A09989048C502462C121185024214241210C12090AC50904811611044D0109A211110A888842111988443118823145682844414640A31C156838C201D4A8833BEC6F497688000001B52A848604028102C180000001B5A33C2C36247532177184D09F404F44E808503E8801940FA20321000080");

    [Fact]
    public void Parses_the_avs1_sequence_header()
    {
        var s = Assert.IsType<AvsSequence>(Avs.ParseSequence(AvsGeneration.Avs1, Avs1Sequence));
        Assert.Equal((0x20, 0x40, 320, 176, 25.0, 8), (s.ProfileId, s.LevelId, s.Width, s.Height, s.FrameRate, s.BitDepth));
        Assert.False(s.LowDelay);
        Assert.Equal("Jizhun@L4.0", Avs.ProfileLevel(AvsGeneration.Avs1, s.ProfileId, s.LevelId));
    }

    [Fact]
    public void Parses_the_avs2_sequence_header_to_its_reorder_delay()
    {
        var s = Assert.IsType<AvsSequence>(Avs.ParseSequence(AvsGeneration.Avs2, Avs2Sequence));
        Assert.Equal((0x20, 0x14, 320, 176, 25.0, 8), (s.ProfileId, s.LevelId, s.Width, s.Height, s.FrameRate, s.BitDepth));
        Assert.False(s.LowDelay);
        Assert.True(s.OutputReorderDelay > 0);
        Assert.Equal("Main@L2.0.60", Avs.ProfileLevel(AvsGeneration.Avs2, s.ProfileId, s.LevelId));
    }

    [Fact]
    public void Parses_the_avs3_sequence_header_and_its_hdr_extensions()
    {
        var s = Assert.IsType<AvsSequence>(Avs.ParseSequence(AvsGeneration.Avs3, Avs3SequenceWithHdr));
        Assert.Equal((0x22, 0x6A, 320, 176, 25.0), (s.ProfileId, s.LevelId, s.Width, s.Height, s.FrameRate));
        Assert.Equal("Main 10@L10.2.120", Avs.ProfileLevel(AvsGeneration.Avs3, s.ProfileId, s.LevelId));
        Assert.Equal(new ColorInfo(9, 16, 9, false), s.Color);
        var hdr = Assert.IsType<HdrInfo>(s.Hdr);
        Assert.Equal((1000, 400), (hdr.MaxCll, hdr.MaxFall));
        Assert.Equal(1000, hdr.MaxLuminance!.Value, 3);
        Assert.Equal(0.005, hdr.MinLuminance!.Value, 5);
        Assert.Equal(0.68, hdr.DisplayPrimaries![0].X, 3); // red first
        Assert.Equal(0.265, hdr.DisplayPrimaries[1].X, 3); // then green
    }

    [Theory]
    [InlineData(12, 16)] // PQ
    [InlineData(14, 18)] // HLG
    [InlineData(11, 15)] // BT.2020 12-bit
    [InlineData(13, 2)]
    public void Maps_avs_transfer_code_points(int avs, int h273) => Assert.Equal(h273, Avs.MapColor(9, avs, 8, false).Transfer);

    [Fact]
    public void Maps_the_bt2020_matrix() => Assert.Equal(9, Avs.MapColor(9, 12, 8, false).Matrix);

    [Theory]
    [InlineData(0x10, "Main@L2.0.15")]
    [InlineData(0x22, "Main@L4.0.60")]
    [InlineData(0x42, "Main@L6.2.30")]
    [InlineData(0x4A, "Main@L6.2.120")]
    [InlineData(0x13, "Main, level 0x13")]
    public void Names_avs2_levels(int level, string expected) => Assert.Equal(expected, Avs.ProfileLevel(AvsGeneration.Avs2, 0x20, level));

    /// <summary>xavs restarts picture_distance at each GOP's I picture: the order continues after the previous GOP.</summary>
    [Fact]
    public void Avs1_order_continues_across_gops()
    {
        var sequence = new AvsSequence { Generation = AvsGeneration.Avs1 };
        var counter = new Avs.OrderCounter();
        (bool Intra, int Distance)[] pictures = [(true, 0), (false, 2), (false, 1), (false, 3), (true, 0), (false, 2), (false, 1)];
        var order = pictures.Select(p => counter.Next(sequence, new AvsPicture(p.Intra, p.Distance, 0))).ToArray();
        Assert.Equal([0L, 2, 1, 3, 4, 6, 5], order);
    }

    /// <summary>AVS2/AVS3: decode order index plus output delay minus the reorder delay, the index wrapping at 256.</summary>
    [Fact]
    public void Avs2_order_uses_the_output_delay()
    {
        var sequence = new AvsSequence { Generation = AvsGeneration.Avs2, OutputReorderDelay = 3 };
        var counter = new Avs.OrderCounter();
        (int Coi, int Delay)[] pictures = [(0, 3), (1, 10), (2, 5), (3, 2), (4, 0), (255, 3), (0, 3)];
        var order = pictures.Select(p => counter.Next(sequence, new AvsPicture(false, p.Coi, p.Delay))).ToArray();
        Assert.Equal([0L, 8, 4, 2, 1, 255, 256], order);
    }
}
