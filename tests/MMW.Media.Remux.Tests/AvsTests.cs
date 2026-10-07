using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Chinese AVS video: AVS3 (uavs3enc / uavs3dec), AVS1 (xavs / FFmpeg) raw import, MP4 and Matroska output, presentation
/// order, HDR10 / HLG / HDR Vivid signalling (added to the encoder's output: a sequence display extension, a mastering
/// display extension and HDR picture extensions), MPEG-TS, and the corpus files (AVS2 among them). Also HDR Vivid in HEVC.
/// </summary>
public sealed partial class AvsTests
{
    private static void RequireTools(params string[] tools)
    {
        MediaProbe.RequireFfmpeg();
        // The AVS tools exit with an error for --help: look them up on PATH.
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tool in tools)
        {
            if (!path.Any(dir => File.Exists(Path.Combine(dir, tool)) || File.Exists(Path.Combine(dir, tool + ".exe"))))
                Assert.Skip($"{tool} is not installed.");
        }
    }

    private static string Yuv() => Fixtures.Get("avs-in.yuv", "ffmpeg",
        "-v error -y -f lavfi -i testsrc2=duration=1:size=320x176:rate=25 -pix_fmt yuv420p -f rawvideo {out}");

    private static string Avs3()
    {
        RequireTools("uavs3enc", "uavs3dec");
        return Fixtures.Get("avs3.avs3", "uavs3enc", $"-i {Fixtures.Quote(Yuv())} -w 320 -h 176 -d 8 --fps_num 25 --fps_den 1 -f 25 -o {{out}}");
    }

    private static string Avs1()
    {
        RequireTools("xavs");
        return Fixtures.Get("avs1.cavs", "xavs", $"--fps 25 --bframes 2 --keyint 12 -o {{out}} {Fixtures.Quote(Yuv())} 320x176");
    }

    /// <summary>
    /// The stream with a sequence display extension (BT.2020, <paramref name="transfer"/> in AVS code points: 12 PQ,
    /// 14 HLG) and a mastering display extension after each sequence header, and (<paramref name="vivid"/>) an HDR
    /// picture extension before each picture's first slice.
    /// </summary>
    private static string WithHdr(string source, int transfer, bool vivid)
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, $"{Path.GetFileNameWithoutExtension(source)}-{transfer}{(vivid ? "-vivid" : string.Empty)}{Path.GetExtension(source)}");
        if (File.Exists(path))
            return path;

        static string B(int value, int count) => Convert.ToString(value, 2).PadLeft(count, '0');
        var display = B(2, 4) + B(5, 3) + "0" + "1" + B(9, 8) + B(transfer, 8) + B(8, 8) + B(320, 14) + "1" + B(176, 14) + "0" + "1";
        int[] values = [13250, 34500, 7500, 3000, 34000, 16000, 15635, 16450, 1000, 50, 1000, 400];
        var mastering = B(10, 4) + string.Concat(values.Select(v => B(v, 16) + "1")) + B(0, 16);
        // id 0101, hdr_dynamic_metadata_type 5, the T.35 message (China, HDR Vivid) and the start of the CUVA metadata.
        byte[] dynamic = [0, 0, 1, Avs.Extension, 0x55, 0x26, 0x00, 0x04, 0x00, 0x05, 0x01, 0x00, 0x0B, 0xF6, 0x5C, 0x2F, 0x8C, 0xFA];

        var data = File.ReadAllBytes(source);
        using var output = new MemoryStream();
        var afterPicture = false;
        foreach (var range in Avs.SplitUnits(data))
        {
            var unit = data.AsSpan()[range];
            var code = Avs.StartCode(unit);
            if (vivid && afterPicture && code < Avs.SequenceHeader)
            {
                output.Write(dynamic);
                afterPicture = false;
            }

            output.Write(unit);
            if (Avs.IsPicture(code))
                afterPicture = true;
            if (code == Avs.SequenceHeader)
            {
                output.Write(Extension(display));
                output.Write(Extension(mastering));
            }
        }

        File.WriteAllBytes(path + ".tmp", output.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
        return path;
    }

    private static byte[] Extension(string bits)
    {
        bits += "1"; // marker bit
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        var o = new List<byte> { 0, 0, 1, Avs.Extension };
        for (var i = 0; i < bits.Length; i += 8)
            o.Add(Convert.ToByte(bits.Substring(i, 8), 2));
        return [.. o];
    }

    private static async Task<(string Path, ImportableTrack Video)> ImportAsync(string source, ContainerKind target, string extension)
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var video = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Video);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks.Where(t => t.Support.CanMux));
        var output = MediaProbe.TempPath(extension);
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return (output, video);
    }

    /// <summary>The video samples of <paramref name="path"/> in decoding order and their presentation times.</summary>
    private static (List<byte[]> Samples, List<long> Pts) Samples(string path)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        var video = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Video);
        var samples = new List<byte[]>();
        var pts = new List<long>();
        while (video.ReadNext() is { } sample)
        {
            samples.Add(sample.GetData().ToArray());
            pts.Add(sample.Pts);
        }

        return (samples, pts);
    }

    [GeneratedRegex(@"^\s+\d+\s+(\d+) \(", RegexOptions.Multiline)]
    private static partial Regex Uavs3PocPattern();

    /// <summary>uavs3dec: the order counts of the pictures it outputs and the MD5 of the decoded video.</summary>
    private static (HashSet<int> Pocs, string Md5) DecodeAvs3(byte[] stream)
    {
        var input = MediaProbe.TempPath(".avs3");
        var yuv = MediaProbe.TempPath(".yuv");
        try
        {
            File.WriteAllBytes(input, stream);
            var log = Fixtures.Run("uavs3dec", $"-i {Fixtures.Quote(input)} -o {Fixtures.Quote(yuv)} -l 2");
            var pocs = Uavs3PocPattern().Matches(log).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            return (pocs, File.Exists(yuv) ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(yuv))) : string.Empty);
        }
        finally
        {
            MediaProbe.Delete(input, yuv);
        }
    }

    private static List<int> Ranks(IReadOnlyList<long> values) => values.Select(v => values.Count(o => o < v)).ToList();

    [Theory]
    [InlineData(12, false, ".mp4", "HDR10", "smpte2084")]
    [InlineData(14, false, ".mp4", "HLG", "arib-std-b67")]
    [InlineData(12, true, ".mkv", "HDR Vivid", "smpte2084")]
    public async Task Raw_avs3_imports_with_its_colour_hdr_and_order(int transfer, bool vivid, string extension, string hdr, string ffmpegTransfer)
    {
        var raw = WithHdr(Avs3(), transfer, vivid);
        var target = extension == ".mp4" ? ContainerKind.Mp4 : ContainerKind.Matroska;
        var (output, video) = await ImportAsync(raw, target, extension);
        try
        {
            Assert.Equal(CodecType.Avs3, video.Config.Codec);
            Assert.Equal($"320×176, 25 fps, Main 10@L10.2.120, {hdr}", video.Details);
            var colour = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=color_space,color_transfer,color_primaries -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.Equal($"bt2020nc,{ffmpegTransfer},bt2020", colour.Trim().TrimEnd(','));
            if (target == ContainerKind.Mp4)
            {
                Assert.Equal("avs3", Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=codec_tag_string -of csv=p=0 {Fixtures.Quote(output)}").Trim().TrimEnd(','));
                var reread = Assert.Single((await Mp4.ReadAsync(output, Ct)).Tracks.OfType<VideoTrack>());
                Assert.Equal("Main 10@L10.2.120", reread.ProfileLevel); // from the av3c record
                Assert.Contains("Mastering display metadata", Fixtures.Run("ffprobe", $"-v error -show_entries stream_side_data=side_data_type -of csv=p=0 {Fixtures.Quote(output)}"), StringComparison.Ordinal);
            }

            // The stored pictures decode like the source and are presented in the decoder's order: decoding the first
            // k pictures outputs the k-th picture's order count for the first time.
            var (samples, pts) = Samples(output);
            Assert.Equal(25, samples.Count);
            Assert.Equal(DecodeAvs3(File.ReadAllBytes(raw)).Md5, DecodeAvs3([.. samples.SelectMany(s => s)]).Md5);
            var seen = new HashSet<int>();
            var decoderOrder = new List<long>();
            for (var k = 1; k <= 10; k++)
            {
                var pocs = DecodeAvs3([.. samples.Take(k).SelectMany(s => s)]).Pocs;
                decoderOrder.Add(Assert.Single(pocs.Except(seen)));
                seen.UnionWith(pocs);
            }

            Assert.Equal(Ranks(decoderOrder), Ranks(pts.Take(10).ToList()));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Avs3_transport_stream_keeps_the_presentation_order()
    {
        var (mp4, _) = await ImportAsync(WithHdr(Avs3(), 12, vivid: true), ContainerKind.Mp4, ".mp4");
        var ts = MediaProbe.TempPath(".ts");
        string? output = null;
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(mp4)} -c copy -f mpegts {Fixtures.Quote(ts)}"); // stream type 0xD4
            (output, var video) = await ImportAsync(ts, ContainerKind.Matroska, ".mkv");
            Assert.Equal(CodecType.Avs3, video.Config.Codec);
            Assert.Contains("HDR Vivid", video.Details, StringComparison.Ordinal);
            Assert.Equal(Ranks(Samples(mp4).Pts), Ranks(Samples(output).Pts));
        }
        finally
        {
            MediaProbe.Delete(mp4, ts, output ?? string.Empty);
        }
    }

    [Fact]
    public async Task Raw_avs1_goes_to_matroska_in_order_and_mp4_says_why_not()
    {
        var raw = Avs1();
        var mp4 = await TrackImporter.InspectAsync(raw, ContainerKind.Mp4, Ct);
        var support = Assert.Single(mp4).Support;
        Assert.False(support.CanMux);
        Assert.Contains("no MP4 sample entry", support.Reason, StringComparison.Ordinal);

        var (output, video) = await ImportAsync(raw, ContainerKind.Matroska, ".mkv");
        try
        {
            Assert.Equal("320×176, 25 fps, Jizhun@L4.0", video.Details);
            var id = MediaProbe.MkvIdentify(output);
            Assert.Empty(id.GetProperty("errors").EnumerateArray());
            Assert.Contains("CAVS", id.GetProperty("tracks")[0].GetProperty("codec").GetString(), StringComparison.Ordinal);
            Assert.Equal(DecodedFrameHashes(raw), DecodedFrameHashes(output));

            // xavs restarts picture_distance at every I picture: the presentation times run on across GOPs.
            var times = Fixtures.Run("ffmpeg", $"-v quiet -i {Fixtures.Quote(output)} -fps_mode passthrough -f framemd5 -")
                .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => long.Parse(l.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(Enumerable.Range(0, 25).Select(i => (long)i), times);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Hdr_vivid_is_detected_in_hevc()
    {
        MediaProbe.RequireFfmpeg();
        var source = Fixtures.Get("vivid-src.hevc", "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=duration=1:size=320x180:rate=25 -pix_fmt yuv420p10le -c:v libx265 -x265-params " +
            "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc -f hevc {out}");
        // A prefix SEI with an HDR Vivid T.35 message (China, terminal provider 0x0004, oriented code 0x0005) per picture.
        byte[] t35 = [0x26, 0x00, 0x04, 0x00, 0x05, 0x01, .. Enumerable.Range(0x11, 32).Select(b => (byte)b)];
        byte[] sei = [0, 0, 0, 1, Hevc.NalSeiPrefix << 1, 1, Sei.UserDataRegisteredItuTT35, (byte)t35.Length, .. t35, 0x80];
        var data = File.ReadAllBytes(source);
        using var output = new MemoryStream();
        foreach (var range in NalUnits.SplitAnnexB(data))
        {
            var nal = data.AsSpan()[range];
            if (Hevc.IsVcl(NalUnits.HevcType(nal)) && Hevc.IsFirstSliceSegment(nal))
                output.Write(sei);
            output.Write([0, 0, 0, 1]);
            output.Write(nal);
        }

        var raw = MediaProbe.TempPath(".hevc");
        try
        {
            File.WriteAllBytes(raw, output.ToArray());
            Assert.Contains("CUVA", Fixtures.Run("ffprobe", $"-v error -read_intervals %+#1 -show_frames -show_entries frame=side_data {Fixtures.Quote(raw)}"), StringComparison.Ordinal);
            var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Matroska, Ct);
            Assert.Contains("HDR Vivid", Assert.Single(tracks).Details, StringComparison.Ordinal);
            Assert.True(tracks[0].Config.HdrVivid);
        }
        finally
        {
            MediaProbe.Delete(raw);
        }
    }

    private static string CorpusFile(string name)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine(dir, "Video codecs", name) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        return path;
    }

    [Theory]
    [InlineData("AVS.mkv", "AVS")]
    [InlineData("AVS2.mkv", "AVS2")]
    [InlineData("AVS2.m2ts", "AVS2")]
    [InlineData("AVS3.mkv", "AVS3")]
    public async Task Corpus_file_shows_its_profile_and_has_presentation_times(string name, string format)
    {
        MediaProbe.RequireFfmpeg();
        var source = CorpusFile(name);
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        var video = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Video);
        Assert.Equal(format, video.Format);
        Assert.Contains("@L", video.Details, StringComparison.Ordinal);

        // Every picture has its own presentation time, in an order that differs from decoding order (B pictures).
        var (_, pts) = Samples(source);
        Assert.Equal(pts.Count, pts.Distinct().Count());
        Assert.NotEqual(pts.Order().ToList(), pts);
    }

    [Theory]
    [InlineData("AVS2.mkv")]
    [InlineData("AVS3.mkv")]
    public async Task Corpus_matroska_file_remuxes_to_mp4_in_the_same_order(string name)
    {
        var source = CorpusFile(name);
        var doc = await Mkv.ReadAsync(source, Ct);
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);
            var (inSamples, inPts) = Samples(source);
            var (outSamples, outPts) = Samples(output);
            Assert.Equal(inSamples.Select(Convert.ToHexString), outSamples.Select(Convert.ToHexString));
            Assert.Equal(Ranks(inPts), Ranks(outPts));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// The DVB / UWA HDR Vivid test streams (EBU, CC BY 4.0): AVS3 with HDR picture extensions, VVC with T.35 SEI, and
    /// HEVC carrying four kinds of dynamic metadata at once (ST 2094-10, SL-HDR2, HDR10+, HDR Vivid).
    /// </summary>
    [Theory]
    [InlineData("DVB_2160p50_HDR_with_HDR_Vivid_DM_AVS3_20250923_2.ts", "AVS3", "3840×2160, 50 fps, High 10@L8.0.60, HDR Vivid", 1000)]
    [InlineData("DVB_2160p50_HDR_with_HDR_Vivid_DM_H266_20250930_1.ts", "VVC", "3840×2160, 50 fps, Main 10@L5.1, HDR Vivid", null)] // HDR Vivid SEI only, no static metadata
    [InlineData("DVB_2160p50_HDR_with_Switched_four_DMI_2094-10_2094-40_SL-HDR2_HDR_Vivid_20250926_1.ts", "HEVC", "3840×2160, 50 fps, Main 10@5.1, HDR10+, HDR Vivid", 1000)]
    public async Task Corpus_dvb_stream_shows_hdr_vivid(string name, string format, string details, int? maxCll)
    {
        var source = Corpus.Directory is { } dir ? Path.Combine(dir, "High Dynamic Range", "HDR Vivid", name) : string.Empty;
        Corpus.Require(File.Exists(source) ? source : string.Empty);
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        var video = Assert.Single(tracks, t => t.Config.Kind == TrackKind.Video);
        Assert.Equal(format, video.Format);
        Assert.Equal(details, video.Details);
        Assert.Equal(new ColorInfo(9, 16, 9, false), video.Config.EffectiveColor);
        Assert.Equal(maxCll, video.Config.EffectiveHdr?.MaxCll);
    }
}
