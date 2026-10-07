using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>Colour description and static HDR10 metadata read from the bitstream.</summary>
public sealed class VideoStreamInfoTests
{
    /// <summary>An AV1 sequence header (4K, order hints, screen content tools chosen per frame) with BT.2020 PQ, limited range.</summary>
    private static byte[] SequenceHeader(bool reduced = false, bool timing = false, bool describeColour = true)
    {
        var w = new BitWriter();
        w.Write(0, 3); // seq_profile
        w.Flag(false); // still_picture
        w.Flag(reduced);
        if (reduced)
        {
            w.Write(8, 5); // seq_level_idx
        }
        else
        {
            w.Flag(timing);
            if (timing)
            {
                w.Write(1001, 32);
                w.Write(24000, 32);
                w.Flag(true); // equal_picture_interval
                w.Write(1, 1); // uvlc 0
                w.Flag(false); // decoder_model_info_present_flag
            }

            w.Flag(false); // initial_display_delay_present_flag
            w.Write(0, 5); // operating_points_cnt_minus_1
            w.Write(0, 12); // operating_point_idc
            w.Write(12, 5); // seq_level_idx (> 7)
            w.Flag(false); // seq_tier
        }

        w.Write(11, 4);
        w.Write(11, 4);
        w.Write(3839, 12);
        w.Write(2159, 12);
        if (!reduced)
            w.Flag(false); // frame_id_numbers_present_flag
        w.Write(0, 3); // 128x128, filter intra, intra edge
        if (!reduced)
        {
            w.Write(0, 4);
            w.Flag(true); // enable_order_hint
            w.Write(0, 2); // jnt_comp, ref_frame_mvs
            w.Flag(true); // seq_choose_screen_content_tools
            w.Flag(true); // seq_choose_integer_mv
            w.Write(6, 3); // order_hint_bits_minus_1
        }

        w.Write(3, 3); // superres, cdef, restoration
        w.Flag(true); // high_bitdepth
        w.Flag(false); // mono_chrome
        w.Flag(describeColour);
        if (describeColour)
        {
            w.Write(9, 8);
            w.Write(16, 8);
            w.Write(9, 8);
        }

        w.Flag(false); // color_range: limited
        w.Write(0, 16); // subsampling, film grain… (not read)
        return w.ToArray();
    }

    [Fact]
    public void Reads_the_av1_colour_config()
    {
        Assert.Equal(new ColorInfo(9, 16, 9, false), Av1.SequenceHeaderColor(SequenceHeader()));
        Assert.Equal(new ColorInfo(9, 16, 9, false), Av1.SequenceHeaderColor(SequenceHeader(timing: true)));
        Assert.Equal(new ColorInfo(9, 16, 9, false), Av1.SequenceHeaderColor(SequenceHeader(reduced: true)));
        Assert.False(Av1.SequenceHeaderColor(SequenceHeader(describeColour: false)).IsSpecified);
        Assert.False(Av1.SequenceHeaderColor([0x00]).IsSpecified);
    }

    [Fact]
    public void Mastering_display_sei_is_stored_green_blue_red()
    {
        // G(0.265,0.69) B(0.15,0.06) R(0.68,0.32) WP(0.3127,0.329), 1000 / 0.005 cd/m²
        byte[] sei = [0x33, 0xC2, 0x86, 0xC4, 0x1D, 0x4C, 0x0B, 0xB8, 0x84, 0xD0, 0x3E, 0x80, 0x3D, 0x13, 0x40, 0x42, 0x00, 0x98, 0x96, 0x80, 0x00, 0x00, 0x00, 0x32];
        var hdr = VideoStreamInfoScanner.ParseMasteringDisplaySei(sei)!;
        Assert.Equal([(0.68, 0.32), (0.265, 0.69), (0.15, 0.06)], hdr.DisplayPrimaries!.Select(p => (Math.Round(p.X, 4), Math.Round(p.Y, 4))));
        Assert.Equal((0.3127, 0.329), (Math.Round(hdr.WhitePoint!.Value.X, 4), Math.Round(hdr.WhitePoint.Value.Y, 4)));
        Assert.Equal(1000, hdr.MaxLuminance!.Value, 4);
        Assert.Equal(0.005, hdr.MinLuminance!.Value, 6);
    }

    [Fact]
    public void Av1_mdcv_is_red_green_blue_fixed_point()
    {
        byte[] mdcv = [0xAE, 0x14, 0x51, 0xEC, 0x43, 0xD7, 0xB0, 0xA4, 0x26, 0x66, 0x0F, 0x5C, 0x50, 0x0D, 0x54, 0x39, 0x00, 0x03, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x52];
        var hdr = VideoStreamInfoScanner.ParseAv1Mdcv(mdcv)!;
        Assert.Equal([(0.68, 0.32), (0.265, 0.69), (0.15, 0.06)], hdr.DisplayPrimaries!.Select(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))));
        Assert.Equal(1000, hdr.MaxLuminance!.Value, 4);
        Assert.Equal(0.005, hdr.MinLuminance!.Value, 3);
    }

    [Fact]
    public void Ambient_viewing_environment_matches_the_sei_layout()
    {
        // 314 lux, D65 (as iPhone HLG recordings carry it).
        byte[] amve = [0x00, 0x2F, 0xE9, 0xA0, 0x3D, 0x13, 0x40, 0x42];
        var hdr = VideoStreamInfoScanner.ParseAmbientViewingEnvironment(amve)!;
        Assert.Equal(314, hdr.AmbientIlluminance!.Value, 4);
        Assert.Equal((0.3127, 0.329), (Math.Round(hdr.AmbientLight!.Value.X, 4), Math.Round(hdr.AmbientLight.Value.Y, 4)));
        Assert.Null(VideoStreamInfoScanner.ParseAmbientViewingEnvironment([0, 0, 0, 0, 0x3D, 0x13, 0x40, 0x42])); // 0 lux is invalid
        Assert.True(HdrInfo.Merge(new HdrInfo { MaxCll = 100 }, hdr)!.HasAmbient);
    }

    [Fact]
    public void Container_values_win_and_the_stream_fills_the_gaps()
    {
        var mastering = new HdrInfo { DisplayPrimaries = [(0.68, 0.32), (0.265, 0.69), (0.15, 0.06)], WhitePoint = (0.3127, 0.329), MaxLuminance = 1000, MinLuminance = 0.005 };
        var light = new HdrInfo { MaxCll = 1000, MaxFall = 400 };
        var merged = HdrInfo.Merge(mastering, light)!;
        Assert.Equal((1000, 400), (merged.MaxCll, merged.MaxFall));
        Assert.Equal(1000, merged.MaxLuminance);
        Assert.Same(mastering, HdrInfo.Merge(mastering, mastering with { MaxLuminance = 4000 })); // nothing missing
        Assert.Same(light, HdrInfo.Merge(light, null));

        var config = new CodecConfig { Kind = TrackKind.Video, Codec = CodecType.Hevc, StreamColor = new ColorInfo(9, 16, 9, false), StreamHdr = mastering };
        Assert.Equal(new ColorInfo(9, 16, 9, false), config.EffectiveColor);
        Assert.Equal(new ColorInfo(1, 1, 1), (config with { Color = new ColorInfo(1, 1, 1) }).EffectiveColor);
        Assert.Same(mastering, config.EffectiveHdr);
    }
}
