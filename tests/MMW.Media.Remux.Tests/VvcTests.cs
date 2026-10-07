using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// VVC (H.266): raw Annex B and MPEG-TS import, profile/level, colour and HDR10 / HLG / HDR10+ read from the bitstream,
/// and 'vvc1' / 'vvi1' sample entries. Streams are encoded with vvenc (FFmpeg has no VVC encoder).
/// </summary>
public sealed class VvcTests
{
    /// <summary>An HDR10+ ITU-T T.35 message (ST 2094-40) taken from a real HEVC HDR10+ stream.</summary>
    private static readonly byte[] s_hdr10PlusT35 = Convert.FromHexString(
        "B5003C000104014000000000000000000000236408000028079C500190C801959001FA58029AD003F2F805771807900010");

    private static void RequireVvenc()
    {
        MediaProbe.RequireFfmpeg();
        try
        {
            Fixtures.Run("vvencFFapp", "--version");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Assert.Skip("vvencFFapp (vvenc) is not installed.");
        }
    }

    private static string Yuv() => Fixtures.Get("vvc-in.yuv", "ffmpeg",
        "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -f rawvideo {out}");

    /// <summary>One second of 10-bit 4:2:0 VVC from vvenc's random access configuration (leading pictures, reordering).</summary>
    private static string Raw(string name, string hdrOptions)
    {
        RequireVvenc();
        return Fixtures.Get(name, "vvencFFapp",
            $"-i {Fixtures.Quote(Yuv())} -s 320x180 -fr 25 --InputBitDepth 10 --InputChromaFormat 420 --preset faster -f 25 {hdrOptions} -b {{out}}");
    }

    private static string Hdr10() => Raw("vvc-hdr10.266",
        "--Hdr pq_2020 --MasteringDisplayColourVolume 13250,34500,7500,3000,34000,16000,15635,16450,10000000,50 --MaxContentLightLevel 1000,400");

    /// <summary>HLG signalled as BT.2020 (14) in the VUI with the alternative transfer characteristics SEI preferring 18.</summary>
    private static string Hlg() => Raw("vvc-hlg.266", "--Hdr hlg_2020");

    /// <summary>The HDR10 stream with an HDR10+ prefix SEI before every picture.</summary>
    private static string Hdr10Plus()
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, "vvc-hdr10plus.266");
        if (File.Exists(path))
            return path;

        var data = File.ReadAllBytes(Hdr10());
        var payload = new List<byte> { Sei.UserDataRegisteredItuTT35, (byte)s_hdr10PlusT35.Length };
        payload.AddRange(s_hdr10PlusT35);
        payload.Add(0x80); // rbsp_trailing_bits
        byte[] sei = [0, 0, 0, 1, 0, Vvc.NalSeiPrefix << 3 | 1, .. EmulationPrevention(payload)];

        using var output = new MemoryStream();
        foreach (var range in NalUnits.SplitAnnexB(data))
        {
            var nal = data.AsSpan()[range];
            if (Vvc.IsVcl(Vvc.NalType(nal)) && Vvc.HasPictureHeaderInSlice(nal))
                output.Write(sei);
            output.Write([0, 0, 0, 1]);
            output.Write(nal);
        }

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, output.ToArray());
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static byte[] EmulationPrevention(IEnumerable<byte> rbsp)
    {
        var o = new List<byte>();
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                o.Add(3);
                zeros = 0;
            }

            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. o];
    }

    private static string Probe(string path, string entries) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries {entries} -of csv=p=0 {Fixtures.Quote(path)}").Trim().TrimEnd(',');

    private static async Task<string> ImportAsync(string source, ContainerKind target, string extension, Action<ImportableTrack>? check = null)
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var video = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Video);
        check?.Invoke(video);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(extension);
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task Raw_hdr10_stream_imports_with_its_profile_colour_and_static_metadata(string extension)
    {
        var raw = Hdr10();
        var target = extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska;
        var output = await ImportAsync(raw, target, extension, video =>
        {
            Assert.Equal(CodecType.Vvc, video.Config.Codec);
            Assert.Equal("320×180, 25 fps, Main 10@L2.0, HDR10", video.Details);
            Assert.Equal(new ColorInfo(9, 16, 9, false), video.Config.EffectiveColor);
            var hdr = Assert.IsType<HdrInfo>(video.Config.EffectiveHdr);
            Assert.Equal(1000, hdr.MaxCll);
            Assert.Equal(400, hdr.MaxFall);
            Assert.Equal(1000, hdr.MaxLuminance!.Value, 3);
        });
        string? mp4 = null;
        try
        {
            if (target == ContainerKind.Mp4)
            {
                Assert.Equal(DecodedFrameHashes(raw), DecodedFrameHashes(output));
            }
            else
            {
                // FFmpeg drops the leading pictures of a Matroska VVC track shown before its first key frame (its own
                // Matroska files too): the packets and their presentation times must be those of the MP4 import.
                mp4 = await ImportAsync(raw, ContainerKind.Mp4, ".mp4");
                Assert.Equal(MediaProbe.PacketHashes(mp4), MediaProbe.PacketHashes(output));
                Assert.Equal(Probe(mp4, "packet=pts_time"), Probe(output, "packet=pts_time"));
            }

            Assert.Equal("bt2020nc,smpte2084,bt2020", Probe(output, "stream=color_space,color_transfer,color_primaries"));
            var sideData = Probe(output, "stream_side_data=side_data_type,max_content,max_luminance");
            Assert.Contains("Mastering display metadata", sideData, StringComparison.Ordinal);
            Assert.Contains("1000", sideData, StringComparison.Ordinal);
            if (target == ContainerKind.Mp4)
                Assert.Equal("vvc1", Probe(output, "stream=codec_tag_string")); // parameter sets moved to vvcC

            var reread = await (target == ContainerKind.Mp4 ? Mp4.ReadAsync(output, Ct) : Mkv.ReadAsync(output, Ct));
            Assert.Equal("Main 10@L2.0", Assert.Single(reread.Tracks.OfType<VideoTrack>()).ProfileLevel);
        }
        finally
        {
            MediaProbe.Delete(output, mp4 ?? string.Empty);
        }
    }

    [Fact]
    public async Task Hlg_signalled_by_the_alternative_transfer_sei_is_written_as_hlg()
    {
        var output = await ImportAsync(Hlg(), ContainerKind.Mp4, ".mp4", video => Assert.Contains("HLG", video.Details, StringComparison.Ordinal));
        try
        {
            Assert.Equal("bt2020nc,arib-std-b67,bt2020", Probe(output, "stream=color_space,color_transfer,color_primaries"));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Hdr10_plus_sei_is_detected()
    {
        var output = await ImportAsync(Hdr10Plus(), ContainerKind.Matroska, ".mkv", video => Assert.Contains("HDR10+", video.Details, StringComparison.Ordinal));
        try
        {
            var doc = await Mkv.ReadAsync(output, Ct);
            var video = Assert.Single(doc.Tracks.OfType<VideoTrack>());
            var scan = await VideoBitstreamScan.ScanAsync(video, Ct);
            Assert.True(scan.Hdr10Plus);
            Assert.Equal(16, scan.StreamInfo!.Color.Transfer);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// MPEG-TS (stream type 0x33, with FFmpeg's extra access unit delimiter): the parameter sets stay in the samples, so
    /// the MP4 sample entry is 'vvi1'.
    /// </summary>
    [Fact]
    public async Task Transport_stream_imports_with_timestamps_and_in_band_parameter_sets()
    {
        var raw = Hdr10();
        var mp4 = await ImportAsync(raw, ContainerKind.Mp4, ".mp4");
        var ts = MediaProbe.TempPath(".ts");
        string? output = null;
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(mp4)} -c copy -f mpegts {Fixtures.Quote(ts)}");
            output = await ImportAsync(ts, ContainerKind.Mp4, ".mp4", video => Assert.Equal(CodecType.Vvc, video.Config.Codec));
            Assert.Equal("vvi1", Probe(output, "stream=codec_tag_string"));
            Assert.Equal(DecodedFrameHashes(raw), DecodedFrameHashes(output));
            Assert.Equal(MediaProbe.PacketHashes(ts)[0].Count, MediaProbe.PacketHashes(output)[0].Count);

            // vvi1: the record's arrays are marked incomplete.
            var reread = await Mp4.ReadAsync(output, Ct);
            Assert.Equal("Main 10@L2.0", Assert.Single(reread.Tracks.OfType<VideoTrack>()).ProfileLevel);
        }
        finally
        {
            MediaProbe.Delete(mp4, ts, output ?? string.Empty);
        }
    }

    [Fact]
    public void Configuration_record_is_rebuilt_from_the_parameter_sets()
    {
        var data = File.ReadAllBytes(Hdr10());
        List<byte[]> sps = [], pps = [];
        foreach (var range in NalUnits.SplitAnnexB(data))
        {
            var nal = data[range];
            var list = Vvc.NalType(nal) switch
            {
                Vvc.NalSps => sps,
                Vvc.NalPps => pps,
                _ => null,
            };
            if (list is not null && !list.Any(n => n.AsSpan().SequenceEqual(nal)))
                list.Add(nal);
        }

        var info = Vvc.ParseSps(sps[0]);
        Assert.Equal((320, 180, 320, 184), (info.Width, info.Height, info.MaxWidth, info.MaxHeight));
        Assert.Equal((10, 1, 25.0), (info.BitDepth, info.ChromaFormatIdc, info.FrameRate));
        Assert.Equal(new ColorInfo(9, 16, 9, false), info.Color);

        var record = Vvc.BuildVvcC([], sps, pps);
        var parsed = Vvc.ParseVvcC(record);
        Assert.Equal((4, 1, 32, 1, 10, 320, 184), (parsed.LengthSize, parsed.ProfileIdc, parsed.LevelIdc, parsed.ChromaFormatIdc, parsed.BitDepth, parsed.MaxWidth, parsed.MaxHeight));
        Assert.Equal([Vvc.NalSps, Vvc.NalPps], parsed.Nals.Select(n => n.Type));
        Assert.All(parsed.Nals, n => Assert.True(n.Complete));
        Assert.All(Vvc.ParseVvcC(Vvc.MarkArraysComplete(record, false)).Nals, n => Assert.False(n.Complete));
        Assert.Equal("Main 10@L2.0", Vvc.ProfileLevel(record));
    }

    [Fact]
    public async Task Corpus_mp4_with_in_band_parameter_sets_remuxes_to_vvi1()
    {
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, "Video codecs", "H266 VVC.mp4") : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mp4.ReadAsync(source, Ct);
            var video = Assert.Single(doc.Tracks.OfType<VideoTrack>());
            Assert.Equal("Main 10@L3.1", video.ProfileLevel);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            Assert.Equal("vvi1", Probe(output, "stream=codec_tag_string"));
            Assert.Equal(DecodedFrameHashes(source), DecodedFrameHashes(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// FFmpeg's transport stream of a VVC track with its own delimiters (FFmpeg adds another before each): the PES
    /// timestamps go to the right access units, and the program starts at the earliest presentation time (a reordered
    /// video's first frame decoded is not its first shown), so no frame is hidden.
    /// </summary>
    [Fact]
    public async Task Corpus_transport_stream_keeps_every_frame_and_its_timing()
    {
        MediaProbe.RequireFfmpeg();
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, "Video codecs", "H266 VVC.mp4") : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        var ts = MediaProbe.TempPath(".ts");
        var reference = MediaProbe.TempPath(".mkv");
        string? output = null;
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(source)} -c copy -f mpegts {Fixtures.Quote(ts)}");
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(ts)} -c copy {Fixtures.Quote(reference)}");
            output = await ImportAsync(ts, ContainerKind.Mp4, ".mp4", video => Assert.Contains("Main 10@L3.1", video.Details, StringComparison.Ordinal));
            var expected = DecodedFrameHashes(reference);
            Assert.Equal(1899, expected.Count); // the frames the MP4 edit list hid are shown by a transport stream
            Assert.Equal(expected, DecodedFrameHashes(output));
        }
        finally
        {
            MediaProbe.Delete(ts, reference, output ?? string.Empty);
        }
    }

}
