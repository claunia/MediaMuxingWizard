using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Raw AV1 (IVF, low-overhead OBU, Annex B) imported into MP4 and Matroska.</summary>
public sealed class Av1RawTests
{
    /// <summary>Two seconds at 25 fps with a key frame every second (SVT-AV1 through FFmpeg).</summary>
    private static string Svt(string format, bool hdr = false)
    {
        MediaProbe.RequireFfmpeg();
        var options = hdr
            ? "-pix_fmt yuv420p10le -c:v libsvtav1 -preset 12 -g 25 -svtav1-params color-primaries=9:transfer-characteristics=16:matrix-coefficients=9"
            : "-pix_fmt yuv420p -c:v libsvtav1 -preset 12 -g 25";
        return Fixtures.Get($"av1-raw{(hdr ? "-pq" : string.Empty)}.{format}", "ffmpeg",
            $"-v error -y -f lavfi -i testsrc2=size=320x180:rate=25:duration=2 {options} -f {format} {{out}}");
    }

    /// <summary>The same in Annex B (aomenc --annexb=1).</summary>
    private static string AnnexB()
    {
        MediaProbe.RequireFfmpeg();
        if (!Fixtures.HasTool("aomenc") && !File.Exists("/usr/bin/aomenc"))
            Assert.Skip("aomenc not installed.");
        var y4m = Fixtures.Get("av1-raw.y4m", "ffmpeg", "-v error -y -f lavfi -i testsrc2=size=320x180:rate=25:duration=2 -pix_fmt yuv420p {out}");
        return Fixtures.Get("av1-raw-annexb.obu", "aomenc", $"--cpu-used=9 --limit=50 --kf-max-dist=25 --obu --annexb=1 -o {{out}} {Fixtures.Quote(y4m)}");
    }

    private static string Frames(string path) => Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:v:0 -f framemd5 -");

    private static async Task<string> SaveAsync(string source, ContainerKind target)
    {
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var track = Assert.Single(tracks);
        Assert.Equal((CodecType.Av1, 320, 180, "Main@L2.0"), (track.Config.Codec, track.Config.Width, track.Config.Height, track.Config.VideoProfile));
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    private static async Task RoundTripAsync(string source)
    {
        foreach (var target in new[] { ContainerKind.Mp4, ContainerKind.Matroska })
        {
            var output = await SaveAsync(source, target);
            try
            {
                Assert.Equal(Frames(source).Split('\n').Where(l => !l.StartsWith('#')).Select(l => l.Split(',').Last()),
                    Frames(output).Split('\n').Where(l => !l.StartsWith('#')).Select(l => l.Split(',').Last()));
                var keys = Fixtures.Run("ffprobe", $"-v error -show_entries packet=flags -of csv=p=0 {Fixtures.Quote(output)}").Split('\n').Count(l => l.StartsWith('K'));
                Assert.Equal(2, keys);
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }

    [Theory]
    [InlineData("ivf")]
    [InlineData("obu")]
    public Task Imports_svt_streams(string format) => RoundTripAsync(Svt(format));

    [Fact]
    public Task Imports_annex_b_streams() => RoundTripAsync(AnnexB());

    /// <summary>The configuration record is the one FFmpeg writes, and the colour comes from the sequence header.</summary>
    [Fact]
    public async Task Builds_the_configuration_record_as_ffmpeg()
    {
        var source = Svt("ivf", hdr: true);
        var reference = MediaProbe.TempPath(".mp4");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(source)} -c copy {Fixtures.Quote(reference)}");
            MediaRemux.EnsureRegistered();
            var track = Assert.Single(await TrackImporter.InspectAsync(source, ContainerKind.Mp4, Ct));
            Assert.Equal(new ColorInfo(9, 16, 9, false), track.Config.Color);
            using var demuxer = MediaFormatRegistry.OpenDemuxer(reference, new DemuxOptions());
            Assert.Equal(demuxer.Tracks[0].Config.Extradata, track.Config.Extradata);
        }
        finally
        {
            MediaProbe.Delete(reference);
        }
    }

    [Fact]
    public void Obu_streams_without_timing_ask_for_a_frame_rate()
    {
        MediaRemux.EnsureRegistered();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(Svt("obu"), new DemuxOptions { FrameRate = 50 });
        Assert.True(MMW.Formats.Elementary.ElementaryFormat.RequiresFrameRate(demuxer));
        Assert.Equal(50, demuxer.Tracks[0].Config.FrameRate, 3);
        Assert.Equal(1.0, demuxer.Duration.TotalSeconds, 3);
    }
}
