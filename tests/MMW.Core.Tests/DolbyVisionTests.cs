using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>
/// Dolby Vision detection from the bitstream: RPU header parsing, profile/compatibility/level derivation and the
/// DOVIDecoderConfigurationRecord rebuilt for the container.
/// </summary>
public sealed class DolbyVisionTests
{
    private static readonly ColorInfo Pq = new(9, 16, 9);
    private static readonly ColorInfo Hlg = new(9, 18, 9);
    private static readonly ColorInfo Sdr = new(1, 1, 1);

    /// <summary>Builds an RPU NAL payload (after the NAL header, RBSP) with sequence information.</summary>
    private static byte[] Rpu(int vdrRpuProfile, bool fullRange, int blBits, int elBits, int vdrBits, bool elResampling, bool disableResidual)
    {
        var w = new BitWriter();
        w.Write(0x19, 8); // prefix
        w.Write(2, 6); // rpu_type
        w.Write(18, 11); // rpu_format
        w.Write((uint)vdrRpuProfile, 4);
        w.Write(0, 4); // vdr_rpu_level
        w.Write(1, 1); // vdr_seq_info_present_flag
        w.Write(0, 1); // chroma_resampling_explicit_filter_flag
        w.Write(0, 2); // coefficient_data_type
        w.Ue(23); // coefficient_log2_denom
        w.Write(1, 2); // vdr_rpu_normalized_idc
        w.Write(fullRange ? 1u : 0, 1);
        w.Ue((uint)blBits - 8);
        w.Ue((uint)elBits - 8);
        w.Ue((uint)vdrBits - 8);
        w.Write(0, 1); // spatial_resampling_filter_flag
        w.Write(0, 3); // reserved
        w.Write(elResampling ? 1u : 0, 1);
        w.Write(disableResidual ? 1u : 0, 1);
        w.Write(0xFFFF_FFFF, 32); // rest of the RPU
        return w.ToArray();
    }

    [Fact]
    public void Profile_8_1_sample_without_container_configuration()
    {
        // HEVC Main 10 3840×2160 23.976, BT.2020 PQ, RPU profile 1 without residual: profile 8.1.
        var header = DolbyVision.ParseRpuHeader(Rpu(1, false, 10, 10, 12, false, true));
        Assert.NotNull(header);
        Assert.Equal(1, header.VdrRpuProfile);
        Assert.False(header.BlVideoFullRange);
        Assert.Equal((10, 10, 12), (header.BlBitDepth, header.ElBitDepth, header.VdrBitDepth));
        Assert.False(header.HasEnhancementLayer);

        var detection = DolbyVision.Describe(CodecType.Hevc, header, false, 3840, 2160, 24000 / 1001.0, Pq);
        Assert.NotNull(detection);
        Assert.Equal("8.1", detection.ProfileName);
        Assert.Equal(6, detection.Level);
        Assert.False(detection.ElPresent);

        var info = DolbyVision.ParseConfigurationRecord(detection.ConfigurationRecord);
        Assert.Equal(new DolbyVisionInfo(1, 0, 8, 6, true, false, true, 1), info);
        Assert.Equal(24, detection.ConfigurationRecord.Length);
        Assert.Equal("dvvC", DolbyVision.Mp4BoxType(info.Profile));
    }

    [Fact]
    public void Profiles_follow_the_rpu_header()
    {
        // Profile 5: full-range IPT, no compatible base layer.
        var p5 = DolbyVision.Describe(CodecType.Hevc, DolbyVision.ParseRpuHeader(Rpu(0, true, 10, 10, 12, false, true)), false, 1920, 1080, 24, Pq);
        Assert.Equal((5, 0, "5"), (p5!.Profile, p5.BlSignalCompatibilityId, p5.ProfileName));
        Assert.Equal("dvcC", DolbyVision.Mp4BoxType(p5.Profile));

        // Profile 7 (12-bit FEL/MEL): EL marked present only when carried in the same track.
        var p7Header = DolbyVision.ParseRpuHeader(Rpu(1, false, 10, 10, 12, true, false));
        var p7 = DolbyVision.Describe(CodecType.Hevc, p7Header, true, 3840, 2160, 24, Pq);
        Assert.Equal((7, 6, true), (p7!.Profile, p7.BlSignalCompatibilityId, p7.ElPresent));
        Assert.False(DolbyVision.Describe(CodecType.Hevc, p7Header, false, 3840, 2160, 24, Pq)!.ElPresent);

        // Profile 4: 10-bit VDR with an EL on an SDR base layer.
        var p4 = DolbyVision.Describe(CodecType.Hevc, DolbyVision.ParseRpuHeader(Rpu(1, false, 10, 10, 10, true, false)), true, 1920, 1080, 24, Sdr);
        Assert.Equal((4, 2), (p4!.Profile, p4.BlSignalCompatibilityId));

        // Profile 8 compatibility follows the base layer's colour description.
        var p8 = DolbyVision.ParseRpuHeader(Rpu(1, false, 10, 10, 12, false, true));
        Assert.Equal("8.4", DolbyVision.Describe(CodecType.Hevc, p8, false, 3840, 2160, 50, Hlg)!.ProfileName);
        Assert.Equal("8.2", DolbyVision.Describe(CodecType.Hevc, p8, false, 1920, 1080, 25, Sdr)!.ProfileName);
        Assert.Equal("8.4", DolbyVision.Describe(CodecType.Hevc, p8, false, 1920, 1080, 25, new ColorInfo(9, 14, 9))!.ProfileName); // HLG signalled as BT.2020 SDR

        // AV1 is always profile 10.
        Assert.Equal("10.1", DolbyVision.Describe(CodecType.Av1, p8, false, 3840, 2160, 24, Pq)!.ProfileName);
        Assert.Equal("10.0", DolbyVision.Describe(CodecType.Av1, DolbyVision.ParseRpuHeader(Rpu(0, true, 10, 10, 12, false, true)), false, 3840, 2160, 24, Pq)!.ProfileName);
    }

    [Fact]
    public void Rejects_what_is_not_an_rpu_with_sequence_information()
    {
        var rpu = Rpu(1, false, 10, 10, 12, false, true);
        var badPrefix = rpu.ToArray();
        badPrefix[0] = 0x18;
        Assert.Null(DolbyVision.ParseRpuHeader(badPrefix));
        Assert.Null(DolbyVision.ParseRpuHeader(rpu.AsSpan(0, 4)));

        var w = new BitWriter();
        w.Write(0x19, 8);
        w.Write(2, 6);
        w.Write(18, 11);
        w.Write(1, 4);
        w.Write(0, 4);
        w.Write(0, 1); // no sequence info
        w.Write(0, 30);
        Assert.Null(DolbyVision.ParseRpuHeader(w.ToArray()));

        Assert.Null(DolbyVision.Describe(CodecType.Hevc, null, false, 1920, 1080, 24, Pq));
        Assert.Null(DolbyVision.Describe(CodecType.H264, DolbyVision.ParseRpuHeader(rpu), false, 1920, 1080, 24, Pq));
    }

    [Theory]
    [InlineData(1280, 720, 24, 1)]
    [InlineData(1280, 720, 30, 2)]
    [InlineData(1920, 1080, 24, 3)]
    [InlineData(1920, 1080, 30, 4)]
    [InlineData(1920, 1080, 60, 5)]
    [InlineData(3840, 2160, 23.976, 6)]
    [InlineData(3840, 2160, 30, 7)]
    [InlineData(3840, 2160, 48, 8)]
    [InlineData(3840, 2160, 60, 9)]
    [InlineData(3840, 2160, 120, 10)]
    [InlineData(4096, 2160, 100, 11)]
    [InlineData(7680, 4320, 60, 12)]
    [InlineData(7680, 4320, 120, 13)]
    public void Level_comes_from_the_luma_sample_rate(int width, int height, double fps, int level) =>
        Assert.Equal(level, DolbyVision.Level(width, height, DolbyVision.NominalFrameRate(fps)));

    [Fact]
    public void Frame_rates_snap_to_nominal_values()
    {
        Assert.Equal(24000 / 1001.0, DolbyVision.NominalFrameRate(23.9761), 6);
        Assert.Equal(25, DolbyVision.NominalFrameRate(25.02), 6);
        Assert.Equal(17.3, DolbyVision.NominalFrameRate(17.3), 6);
    }

    [Fact]
    public void Configuration_record_round_trips()
    {
        var record = DolbyVision.BuildConfigurationRecord(10, 13, true, false, true, 4);
        Assert.Equal(new DolbyVisionInfo(1, 0, 10, 13, true, false, true, 4), DolbyVision.ParseConfigurationRecord(record));
        Assert.All(record.AsSpan(5).ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<InvalidDataException>(() => DolbyVision.ParseConfigurationRecord(record.AsSpan(0, 4)));
    }

    [Theory]
    [InlineData("hvc1", 5, 0, "dvh1")]
    [InlineData("hev1", 5, 0, "dvhe")]
    [InlineData("dvhe", 8, 1, "hev1")] // a compatible base layer keeps the base codec's type
    [InlineData("hvc1", 8, 4, "hvc1")]
    [InlineData("hev1", 7, 6, "hev1")]
    [InlineData("hev1", 4, 0, "hev1")] // only profiles 1, 3 and 5 use dvhe/dvh1
    [InlineData("avc1", 9, 2, "avc1")]
    [InlineData("avc3", 1, 0, "dvav")]
    [InlineData("av01", 10, 0, "av01")] // dav1 only on request (most players cannot read it)
    [InlineData("dav1", 10, 0, "av01")]
    [InlineData("av01", 10, 1, "av01")]
    [InlineData("dav1", 10, 4, "av01")]
    public void Mp4_sample_entry_types_follow_the_specification(string entry, int profile, int compat, string expected)
    {
        var info = DolbyVision.ParseConfigurationRecord(DolbyVision.BuildConfigurationRecord(profile, 6, true, false, true, compat));
        Assert.Equal(expected, DolbyVision.Mp4SampleEntryType(entry, info));
    }

    [Fact]
    public void Av1_profile_10_without_compatible_base_layer_is_dav1_on_request()
    {
        var info = DolbyVision.ParseConfigurationRecord(DolbyVision.BuildConfigurationRecord(10, 6, true, false, true, 0));
        Assert.Equal("dav1", DolbyVision.Mp4SampleEntryType("av01", info, av1UsesDav1: true));
        Assert.Equal("dav1", DolbyVision.Mp4SampleEntryType("dav1", info, av1UsesDav1: true));
        var compatible = DolbyVision.ParseConfigurationRecord(DolbyVision.BuildConfigurationRecord(10, 6, true, false, true, 4));
        Assert.Equal("av01", DolbyVision.Mp4SampleEntryType("av01", compatible, av1UsesDav1: true));
        Assert.Equal("hvc1", DolbyVision.Mp4SampleEntryType("dvh1", null));
    }

    [Fact]
    public void Layers_brands_and_level_edits()
    {
        DolbyVisionInfo Info(int profile, bool el, bool bl, int compat = 6) =>
            DolbyVision.ParseConfigurationRecord(DolbyVision.BuildConfigurationRecord(profile, 6, true, el, bl, compat));

        Assert.True(DolbyVision.NeedsEnhancementLayerConfig(Info(7, el: true, bl: true)));
        Assert.False(DolbyVision.NeedsEnhancementLayerConfig(Info(8, el: false, bl: true, 1)));
        Assert.False(DolbyVision.NeedsEnhancementLayerConfig(Info(7, el: true, bl: false)));
        Assert.True(DolbyVision.IsEnhancementLayerTrack(Info(7, el: true, bl: false)));
        Assert.False(DolbyVision.IsEnhancementLayerTrack(Info(7, el: true, bl: true)));

        Assert.Equal(["dby1", "db1p"], DolbyVision.Mp4Brands(Info(8, false, true, 1), Pq));
        Assert.Equal(["dby1", "db2g"], DolbyVision.Mp4Brands(Info(9, false, true, 2), Sdr));
        Assert.Equal(["dby1", "db4h"], DolbyVision.Mp4Brands(Info(8, false, true, 4), Hlg));
        Assert.Equal(["dby1", "db4g"], DolbyVision.Mp4Brands(Info(8, false, true, 4), new ColorInfo(9, 14, 9)));
        Assert.Equal(["dby1"], DolbyVision.Mp4Brands(Info(5, false, true, 0), Pq));

        var record = DolbyVision.BuildConfigurationRecord(8, 3, true, false, true, 4);
        Assert.Equal(new DolbyVisionInfo(1, 0, 8, 13, true, false, true, 4), DolbyVision.ParseConfigurationRecord(DolbyVision.WithLevel(record, 13)));
        Assert.Equal(3, DolbyVision.ParseConfigurationRecord(record).Level); // the original is not modified
    }

    [Fact]
    public void Scans_length_prefixed_hevc_samples()
    {
        var rpu = Rpu(1, false, 10, 10, 12, false, true);
        var nal = new byte[2 + rpu.Length];
        nal[0] = 62 << 1;
        nal[1] = 1;
        rpu.CopyTo(nal, 2);
        byte[] slice = [1 << 1, 1, 0xAF, 0x00, 0x11];

        var sample = new List<byte>();
        foreach (var unit in new[] { slice, nal })
        {
            sample.AddRange([0, 0, 0, (byte)unit.Length]);
            sample.AddRange(unit);
        }

        var (header, rpuSeen, elSeen) = DolbyVision.ScanHevc([sample.ToArray()], 4);
        Assert.True(rpuSeen);
        Assert.False(elSeen);
        Assert.Equal(8, DolbyVision.Profile(header!));

        var (_, none, _) = DolbyVision.ScanHevc([(byte[])[0, 0, 0, 5, .. slice]], 4);
        Assert.False(none);
    }

    [Fact]
    public void Tracks_with_a_record_are_not_rescanned()
    {
        var video = new VideoTrack { Id = 1, Format = "HEVC", Source = new TrackSource("/x.mp4", ContainerKind.Mp4, 1) };
        Assert.True(DolbyVisionDetector.NeedsCheck(video));
        video.DolbyVisionRecord = DolbyVision.BuildConfigurationRecord(8, 6, true, false, true, 1);
        Assert.False(DolbyVisionDetector.NeedsCheck(video));
        Assert.Equal(8, video.DolbyVision!.Profile);
        Assert.False(DolbyVisionDetector.NeedsCheck(new VideoTrack { Id = 1, Format = "AVC", Source = new TrackSource("/x.mp4", ContainerKind.Mp4, 1) }));
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                _bits.Add(((value >> i) & 1) != 0);
        }

        public void Ue(uint value)
        {
            var v = value + 1;
            var len = 32 - uint.LeadingZeroCount(v);
            Write(0, (int)len - 1);
            Write(v, (int)len);
        }

        public byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var i = 0; i < _bits.Count; i++)
                if (_bits[i])
                    bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            return bytes;
        }
    }
}
