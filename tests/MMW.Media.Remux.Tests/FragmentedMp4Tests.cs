using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Fragmented MP4 (movie fragments: 'moof' / 'traf' / 'trun', as DASH, CMAF and streaming encoders write it): every
/// sample of the fragments is imported with its timing, and remuxes decode as the source does.
/// </summary>
public sealed class FragmentedMp4Tests
{
    private static string Make(string name, string flags)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=size=320x240:rate=25:duration=3 -f lavfi -i sine=f=440:duration=3 " +
            $"-c:v libx264 -bf 2 -g 25 -pix_fmt yuv420p -c:a aac -movflags {flags} {{out}}");
    }

    private static string Frames(string path, string stream) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:{stream}:0 -fps_mode passthrough -f framemd5 -")
            .Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',').Last()).Aggregate(string.Empty, (a, b) => a + b + "\n");

    [Theory]
    [InlineData("frag-keyframe.mp4", "frag_keyframe+empty_moov")]
    [InlineData("frag-base-moof.mp4", "frag_keyframe+empty_moov+default_base_moof")]
    [InlineData("frag-cmaf.mp4", "dash+cmaf")]
    public async Task Fragmented_files_remux_frame_exact(string name, string flags)
    {
        var source = Make(name, flags);
        MediaRemux.EnsureRegistered();
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        Assert.Equal([CodecType.H264, CodecType.Aac], tracks.Select(t => t.Config.Codec));
        Assert.Equal(25, tracks[0].Config.FrameRate, 1);
        Assert.All(tracks, t => Assert.InRange(t.Duration.TotalSeconds, 2.9, 3.1));
        foreach (var target in new[] { ContainerKind.Matroska, ContainerKind.Mp4 })
        {
            var doc = new MediaDocument(null, target);
            TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(source, target, Ct));
            var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
            try
            {
                await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
                Assert.Equal(Frames(source, "v"), Frames(output, "v"));
                // FFmpeg's MP4 reader trims the last audio frame by the track's edit (as with its own MP4s of these
                // streams), so in MP4 the last decoded audio frame is left out of the comparison.
                var expected = Frames(source, "a").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var actual = Frames(output, "a").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(expected.Length, actual.Length);
                var compared = target == ContainerKind.Mp4 ? expected.Length - 1 : expected.Length;
                Assert.Equal(expected[..compared], actual[..compared]);
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }

    /// <summary>The samples of a fragmented file match those of the same encode written unfragmented.</summary>
    [Fact]
    public async Task Fragment_samples_match_the_unfragmented_file()
    {
        MediaRemux.EnsureRegistered();
        var plain = Make("frag-none.mp4", "+faststart");
        var fragmented = Make("frag-keyframe.mp4", "frag_keyframe+empty_moov");
        static List<(long Dts, long Cts, bool Sync, int Size)> Samples(string path)
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(path);
            var list = new List<(long, long, bool, int)>();
            var track = demuxer.Tracks[0];
            while (track.ReadNext() is { } s)
                list.Add((s.Dts, s.CtsOffset, s.IsSync, s.Size));
            return list;
        }

        var a = Samples(plain);
        var b = Samples(fragmented);
        Assert.Equal(75, b.Count);
        Assert.Equal(a.Select(s => s.Size), b.Select(s => s.Size));
        Assert.Equal(a.Select(s => s.Sync), b.Select(s => s.Sync));
        Assert.Equal(a.Select(s => s.Dts + s.Cts - a[0].Dts), b.Select(s => s.Dts + s.Cts - b[0].Dts));
        await Task.CompletedTask;
    }
}
