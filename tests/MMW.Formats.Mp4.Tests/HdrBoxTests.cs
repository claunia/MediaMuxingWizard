using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Media;

namespace MMW.Formats.Mp4.Tests;

/// <summary>Static HDR boxes of visual sample entries, laid out the way FFmpeg reads and writes them.</summary>
public sealed class HdrBoxTests
{
    private static readonly HdrInfo Bt2020 = new()
    {
        DisplayPrimaries = [(0.68, 0.32), (0.265, 0.69), (0.15, 0.06)], // R, G, B
        WhitePoint = (0.3127, 0.329),
        MaxLuminance = 1000,
        MinLuminance = 0.005,
        MaxCll = 1000,
        MaxFall = 400,
    };

    private static VideoTrack Describe(params Box[] boxes)
    {
        var track = new VideoTrack();
        CodecInfo.DescribeVideo(new Box("hvc1", new byte[78], [.. boxes]), track);
        return track;
    }

    private static void AssertBt2020(HdrInfo? hdr, int digits = 4)
    {
        Assert.NotNull(hdr);
        Assert.Equal(Bt2020.DisplayPrimaries!.Select(p => (Math.Round(p.X, digits), Math.Round(p.Y, digits))),
            hdr.DisplayPrimaries!.Select(p => (Math.Round(p.X, digits), Math.Round(p.Y, digits))));
        Assert.Equal(1000, hdr.MaxLuminance!.Value, 3);
        Assert.Equal(0.005, hdr.MinLuminance!.Value, 3);
        Assert.Equal((1000, 400), (hdr.MaxCll, hdr.MaxFall));
    }

    [Fact]
    public void Mdcv_stores_green_blue_red()
    {
        var mdcv = Mp4SampleEntries.BuildMdcv(Bt2020)!;
        // display_primaries[0] is green: 0.265 / 0.00002 = 13250.
        Assert.Equal([0x33, 0xC2, 0x86, 0xC4], mdcv.Payload[..4]);
        // …then blue (7500, 3000) and red (34000, 16000).
        Assert.Equal([0x1D, 0x4C, 0x0B, 0xB8, 0x84, 0xD0, 0x3E, 0x80], mdcv.Payload[4..12]);
        AssertBt2020(Describe(mdcv, Mp4SampleEntries.BuildClli(Bt2020)!).Hdr);
    }

    [Fact]
    public void Vp9_binding_smdm_and_coll_are_full_boxes_in_fixed_point()
    {
        // SmDm: version/flags, R, G, B and white point as 0.16 fixed point, luminance as 24.8 and 18.14 fixed point.
        byte[] smdm =
        [
            0, 0, 0, 0,
            0xAE, 0x14, 0x51, 0xEC, 0x43, 0xD7, 0xB0, 0xA4, 0x26, 0x66, 0x0F, 0x5C, 0x50, 0x0D, 0x54, 0x39,
            0x00, 0x03, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x52,
        ];
        byte[] coll = [0, 0, 0, 0, 0x03, 0xE8, 0x01, 0x90];
        AssertBt2020(Describe(new Box("SmDm", smdm), new Box("CoLL", coll)).Hdr, digits: 3);
    }

    [Fact]
    public void Amve_round_trips()
    {
        var ambient = new HdrInfo { AmbientIlluminance = 314, AmbientLight = (0.3127, 0.329) };
        var amve = Mp4SampleEntries.BuildAmve(ambient)!;
        Assert.Equal([0x00, 0x2F, 0xE9, 0xA0, 0x3D, 0x13, 0x40, 0x42], amve.Payload);
        var hdr = Describe(amve).Hdr!;
        Assert.Equal(314, hdr.AmbientIlluminance!.Value, 4);
        Assert.Equal(0.329, hdr.AmbientLight!.Value.Y, 4);
        Assert.Null(Mp4SampleEntries.BuildAmve(new HdrInfo { MaxCll = 1 }));
    }
}
