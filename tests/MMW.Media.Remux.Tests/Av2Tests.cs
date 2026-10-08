using System.Security.Cryptography;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// AV2 from the reference encoder's IVF, OBU and WebM files to MP4 (ISOBMFF binding draft) and Matroska (V_AV2 as the
/// reference encoder writes it), checked by decoding the results with the reference decoder.
/// </summary>
/// <remarks>
/// Needs avmenc and avmdec (AVM v1.0.0) on PATH or in the directory named by MMW_AVM_DIR; skipped otherwise.
/// </remarks>
public sealed class Av2Tests
{
    private static string? Tool(string name)
    {
        if (Environment.GetEnvironmentVariable("MMW_AVM_DIR") is { Length: > 0 } dir && File.Exists(Path.Combine(dir, name)))
            return Path.Combine(dir, name);
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }

    private static (string Encoder, string Decoder) RequireAvm()
    {
        MediaProbe.RequireFfmpeg();
        MediaRemux.EnsureRegistered();
        if (Tool("avmenc") is not { } encoder || Tool("avmdec") is not { } decoder)
        {
            Assert.Skip("avmenc/avmdec (AVM v1.0.0) not found; set MMW_AVM_DIR.");
            throw new InvalidOperationException();
        }

        return (encoder, decoder);
    }

    /// <summary>Nine frames of 128×96 (key frames every 4), 8-bit or 10-bit BT.2100 PQ.</summary>
    private static string Encode(string format, bool hdr = false, bool monotonic = false)
    {
        var (encoder, _) = RequireAvm();
        var source = Fixtures.Get(hdr ? "av2-src10.y4m" : "av2-src.y4m", "ffmpeg",
            $"-v error -y -f lavfi -i testsrc2=size=128x96:rate=25:duration=0.36 -pix_fmt {(hdr ? "yuv420p10le -strict -1" : "yuv420p")} {{out}}");
        var options = "--cpu-used=9 --limit=9 --kf-max-dist=4";
        if (hdr)
            options += " -b 10 --input-bit-depth=10 --color-primaries=bt2020 --transfer-characteristics=smpte2084 --matrix-coefficients=bt2020ncl";
        if (monotonic)
            options += " --enable-keyframe-filtering=0 --monotonic-output-order=1";
        var name = $"av2{(hdr ? "-pq" : string.Empty)}{(monotonic ? "-monotonic" : string.Empty)}.{format}";
        return Fixtures.Get(name, encoder, $"{options} --{format} -o {{out}} {Fixtures.Quote(source)}");
    }

    /// <summary>MD5 of the frames the reference decoder outputs.</summary>
    private static string Decode(string path)
    {
        var (_, decoder) = RequireAvm();
        var yuv = MediaProbe.TempPath(".yuv");
        try
        {
            Fixtures.Run(decoder, $"--rawvideo -o {Fixtures.Quote(yuv)} {Fixtures.Quote(path)}");
            using var stream = File.OpenRead(yuv);
            return Convert.ToHexString(MD5.HashData(stream));
        }
        finally
        {
            MediaProbe.Delete(yuv);
        }
    }

    private static async Task<string> SaveAsync(string source, ContainerKind target, string extension = "")
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(extension.Length > 0 ? extension : target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    private static List<MediaSample> Samples(string path)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        var track = Assert.Single(demuxer.Tracks, t => t.Config.Codec == CodecType.Av2);
        var samples = new List<MediaSample>();
        while (track.ReadNext() is { } s)
        {
            s.Data = s.GetData().ToArray();
            s.Reader = null;
            samples.Add(s);
        }

        return samples;
    }

    /// <summary>
    /// The reference encoder's default (non-monotonic output order) through MP4 and back to Matroska: the samples keep
    /// their composition offsets and the stream decodes to the same frames.
    /// </summary>
    [Theory]
    [InlineData("ivf")]
    [InlineData("obu")]
    [InlineData("webm")]
    public async Task Remuxes_through_mp4_and_matroska(string format)
    {
        var source = Encode(format);
        var mp4 = await SaveAsync(source, ContainerKind.Mp4);
        string? mkv = null;
        try
        {
            mkv = await SaveAsync(mp4, ContainerKind.Matroska);
            Assert.Equal(Decode(source), Decode(mkv));

            // ISOBMFF samples: one temporal unit each, without delimiters or sequence headers; sync at key frames.
            var samples = Samples(mp4);
            Assert.Equal(9, samples.Count);
            Assert.All(samples, s => Assert.DoesNotContain(Av2.Obus(s.Data.Span), o => (o[0] >> 2 & 0x1F) is Av2.ObuTemporalDelimiter or Av2.ObuSequenceHeader));
            Assert.Equal([0, 4, 8], samples.Select((s, i) => (s, i)).Where(x => x.s.IsSync).Select(x => x.i));
            var display = samples.Select(s => s.Pts).Order().Distinct().Count();
            Assert.Equal(9, display); // every unit has its own presentation time
            Assert.Contains(samples, s => s.CtsOffset != samples[0].CtsOffset); // displayed out of decoding order
        }
        finally
        {
            MediaProbe.Delete(mp4, mkv ?? string.Empty);
        }
    }

    [Fact]
    public async Task Monotonic_streams_present_in_decoding_order()
    {
        var source = Encode("ivf", monotonic: true);
        var mp4 = await SaveAsync(source, ContainerKind.Mp4);
        try
        {
            var samples = Samples(mp4);
            Assert.All(samples, s => Assert.Equal(samples[0].CtsOffset, s.CtsOffset));
            var mkv = await SaveAsync(mp4, ContainerKind.Matroska);
            Assert.Equal(Decode(source), Decode(mkv));
            MediaProbe.Delete(mkv);
        }
        finally
        {
            MediaProbe.Delete(mp4);
        }
    }

    /// <summary>The MP4 sample entry: 'av02' with 'av2C', the colour of the content interpretation OBU and 'pixi'.</summary>
    [Fact]
    public async Task Writes_the_av2_sample_entry()
    {
        var source = Encode("ivf", hdr: true);
        var track = Assert.Single(await TrackImporter.InspectAsync(source, ContainerKind.Mp4, Ct));
        Assert.Equal("Main_420_10_IP0@L2.0", track.Config.VideoProfile);
        Assert.Equal(new ColorInfo(9, 16, 9, false), track.Config.Color);
        var mp4 = await SaveAsync(source, ContainerKind.Mp4);
        try
        {
            var probe = Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_tag_string,width,height,color_transfer,color_primaries -show_entries format_tags=compatible_brands -of csv=p=0 {Fixtures.Quote(mp4)}");
            Assert.Contains("av02,128,96,smpte2084,bt2020", probe, StringComparison.Ordinal);
            Assert.Contains("av02iso6", probe.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            var config = Assert.Single(Mp4Probe(mp4));
            var (sequence, interpretation) = Av2.Describe(config.Extradata);
            Assert.Equal(10, sequence!.BitDepth);
            Assert.Equal(16, interpretation!.Color.Transfer);
        }
        finally
        {
            MediaProbe.Delete(mp4);
        }
    }

    private static IEnumerable<CodecConfig> Mp4Probe(string path)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        return demuxer.Tracks.Select(t => t.Config).ToList();
    }

    /// <summary>Raw OBU streams carry no timing: the frame rate is asked for (25 fps unless given).</summary>
    [Fact]
    public void Obu_streams_ask_for_a_frame_rate()
    {
        var source = Encode("obu");
        using (var demuxer = MediaFormatRegistry.OpenDemuxer(source, new DemuxOptions()))
        {
            Assert.True(MMW.Formats.Elementary.ElementaryFormat.RequiresFrameRate(demuxer));
            Assert.Equal(25, demuxer.Tracks[0].Config.FrameRate, 3);
        }

        using var fast = MediaFormatRegistry.OpenDemuxer(source, new DemuxOptions { FrameRate = 50 });
        Assert.Equal(50, fast.Tracks[0].Config.FrameRate, 3);
    }

    /// <summary>The AV2 corpus written by the reference encoder (raw OBU, WebM): imported, its frames in order and starting on a key frame.</summary>
    [Theory]
    [InlineData("Video codecs/AV2.obu", "")]
    [InlineData("Video codecs/AV2.webm", "")]
    public async Task Corpus_av2_is_read(string file, string hdr)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine(dir, file) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        MediaRemux.EnsureRegistered();
        var track = Assert.Single(await TrackImporter.InspectAsync(path, ContainerKind.Mp4, Ct));
        Assert.Equal(CodecType.Av2, track.Config.Codec);
        if (hdr.Length > 0)
            Assert.Contains(hdr, track.Details.Split(", "));
        var samples = Samples(path);
        Assert.Equal(samples.Count, samples.Select(s => s.Pts).Distinct().Count());
        Assert.True(samples[0].IsSync);
    }

    /// <summary>
    /// The Matroska AV2 corpus (the reference encoder's IVF streams muxed by this application with their source's audio):
    /// the container carries the colour and HDR read from the bitstream, the video keeps every frame (AV2 packets can
    /// hold several), and the audio comes along.
    /// </summary>
    [Theory]
    [InlineData("Video codecs/AV2.mkv", "", 1899)]
    [InlineData("High Dynamic Range/HDR10/{HDR10, AV2 - Matroska} Exodus Sample.mkv", "HDR10", 1146)]
    [InlineData("High Dynamic Range/HDR10+/{HDR10+, AV2 - Matroska} Movie Sample.mkv", "HDR10+", 1411)]
    [InlineData("High Dynamic Range/HLG/{HLG, AV2 - Matroska} Cymatic Jazz.mkv", "HLG", 1231)]
    public async Task Corpus_av2_matroska_is_read(string file, string hdr, int frames)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine(dir, file) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(path, ContainerKind.Mp4, Ct);
        var video = Assert.Single(tracks, t => t.Kind == TrackKind.Video);
        Assert.Equal(CodecType.Av2, video.Config.Codec);
        Assert.Single(tracks, t => t.Kind == TrackKind.Audio);
        if (hdr.Length > 0)
            Assert.Contains(hdr, video.Details.Split(", "));
        var samples = Samples(path);
        Assert.True(samples[0].IsSync);
        Assert.InRange(video.Duration.TotalSeconds * video.Config.FrameRate, frames - 2, frames + 2);
    }
}
