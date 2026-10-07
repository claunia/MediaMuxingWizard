using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Frames an MP4 source's edit list hides stay hidden after a remux.</summary>
public sealed class EditListTests
{
    /// <summary>H.264, one key frame every 2 s, cut at 0.6 s by stream copy: the edit list skips the first 15 frames.</summary>
    private static string TrimmedMp4()
    {
        MediaProbe.RequireFfmpeg();
        var full = Fixtures.Get("editlist-full.mp4", "ffmpeg",
            "-v error -y -f lavfi -i testsrc=duration=4:size=320x240:rate=25 -c:v libx264 -preset fast -g 50 -bf 0 {out}");
        return Fixtures.Get("editlist-trimmed.mp4", "ffmpeg", $"-v error -y -ss 0.6 -i {Fixtures.Quote(full)} -c copy {{out}}");
    }

    [Fact]
    public async Task Frames_hidden_by_the_source_edit_stay_hidden_when_the_track_is_delayed()
    {
        var source = TrimmedMp4();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mp4.ReadAsync(source, Ct);
            // Delayed by 1 s, the skipped frames land in positive time (0.4–1 s): they were shown before the empty edit ended.
            Assert.Single(doc.Tracks.OfType<VideoTrack>()).StartOffset = TimeSpan.FromSeconds(1);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            var expected = DecodedFrameHashes(source);
            Assert.Equal(85, expected.Count);
            Assert.Equal(expected, DecodedFrameHashes(output));
            var start = Fixtures.Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=start_time -of csv=p=0 {Fixtures.Quote(output)}");
            Assert.Equal("1.000000", start.Trim());
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
