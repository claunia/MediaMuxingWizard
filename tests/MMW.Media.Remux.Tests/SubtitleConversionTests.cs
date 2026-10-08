using MMW.Core.Media;
using MMW.Core.Media.Subtitles;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Text subtitles offered as they are or converted, per container: SubRip, ASS/SSA and WebVTT pass through Matroska
/// and convert among themselves; MP4 takes tx3g and WebVTT. Conversions keep what the target can hold.
/// </summary>
public sealed class SubtitleConversionTests
{
    private const string Ass = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 640
        PlayResY: 360

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,1,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.50,0:00:02.00,Default,,0,0,0,,{\b1}Bold{\b0} and {\c&H0000FF&\fnCourier New}red courier
        Dialogue: 0,0:00:02.50,0:00:04.00,Default,,0,0,0,,{\an8\pos(320,40)}{\kf50}Ka{\kf50}ra{\kf50}oke
        Dialogue: 0,0:00:03.00,0:00:05.00,Default,,0,0,0,,{\i1}overlapping{\i0}
        """;

    private const string Srt = "1\n00:00:00,500 --> 00:00:02,000\n<b>Bold</b> and <font color=\"#ff0000\">red</font>\n\n2\n00:00:02,500 --> 00:00:04,000\n{\\an8}On top\n";

    private const string Vtt = "WEBVTT\n\nSTYLE\n::cue(.hot) { color: #ff8000; }\n\n00:00:00.500 --> 00:00:02.000 line:0 align:start\n<i>Top left</i> <c.hot>hot</c>\n\n00:00:02.500 --> 00:00:04.000\nPlain\n";

    private static string Write(string name, string content)
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Video()
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("subconv-video.mp4", "ffmpeg", "-v error -y -f lavfi -i testsrc2=size=640x360:rate=25:duration=6 -c:v libx264 -pix_fmt yuv420p {out}");
    }

    /// <summary>The cues FFmpeg reads from a file's first subtitle track (start, end, text with tags stripped).</summary>
    private static List<(double Start, double End, string Text)> Cues(string path)
    {
        var srt = MediaProbe.TempPath(".srt");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:s:0 -c:s srt {Fixtures.Quote(srt)}");
            var cues = new List<(double, double, string)>();
            foreach (var block in File.ReadAllText(srt).Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n');
                if (lines.Length < 3)
                    continue;
                var times = lines[1].Split(" --> ");
                static double T(string t) => TimeSpan.ParseExact(t.Trim(), @"hh\:mm\:ss\,fff", System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
                var text = System.Text.RegularExpressions.Regex.Replace(string.Join(' ', lines[2..]), "<[^>]*>|\\{[^}]*\\}", string.Empty).Trim();
                if (text.Length > 0)
                    cues.Add((T(times[0]), T(times[1]), text));
            }

            return cues;
        }
        finally
        {
            MediaProbe.Delete(srt);
        }
    }

    private static async Task<string> SaveAsync(string subtitles, ContainerKind target, ImportAction action)
    {
        MediaRemux.EnsureRegistered();
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(Video(), target, Ct));
        var tracks = await TrackImporter.InspectAsync(subtitles, target, Ct);
        var sub = Assert.Single(tracks);
        sub.Choice = Assert.Single(sub.Choices, c => c.Action == action);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    /// <summary>The container's own name of the first subtitle track's codec: the Matroska CodecID or the MP4 sample entry.</summary>
    private static string Codec(string path)
    {
        if (path.EndsWith(".mkv", StringComparison.Ordinal))
        {
            var info = System.Text.Json.JsonDocument.Parse(Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(path)}"));
            return info.RootElement.GetProperty("tracks").EnumerateArray().First(t => t.GetProperty("type").GetString() == "subtitles")
                .GetProperty("properties").GetProperty("codec_id").GetString()!;
        }

        // FFmpeg lists MP4 'wvtt' as a data stream: the track that is not the video's.
        return Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_type,codec_tag_string -of csv=p=0 {Fixtures.Quote(path)}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(',')).First(f => f[0] != "video")[1];
    }

    /// <summary>The cues of a file's subtitle track as this application reads them (FFmpeg cannot decode MP4 'wvtt').</summary>
    private static List<(double Start, double End, string Text)> OwnCues(string path)
    {
        MediaRemux.EnsureRegistered();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path);
        var script = TextSubtitleConverter.Read(demuxer.Tracks.First(t => t.Config.Kind == TrackKind.Subtitle));
        return script.Events.Select(e => (e.Start / 1000.0, e.End / 1000.0, e.Text.Replace('\n', ' '))).ToList();
    }

    [Theory]
    [InlineData("subconv.srt", ContainerKind.Matroska, new[] { ImportAction.Passthrough, ImportAction.ConvertToAss, ImportAction.ConvertToSsa, ImportAction.ConvertToWebVtt })]
    [InlineData("subconv.srt", ContainerKind.Mp4, new[] { ImportAction.ConvertToTx3g, ImportAction.ConvertToWebVtt })]
    [InlineData("subconv.ass", ContainerKind.Matroska, new[] { ImportAction.Passthrough, ImportAction.ConvertToSsa, ImportAction.ConvertToSrt, ImportAction.ConvertToWebVtt })]
    [InlineData("subconv.ass", ContainerKind.Mp4, new[] { ImportAction.ConvertToTx3g, ImportAction.ConvertToWebVtt })]
    [InlineData("subconv.vtt", ContainerKind.Matroska, new[] { ImportAction.Passthrough, ImportAction.ConvertToSrt, ImportAction.ConvertToAss, ImportAction.ConvertToSsa })]
    [InlineData("subconv.vtt", ContainerKind.Mp4, new[] { ImportAction.ConvertToTx3g, ImportAction.Passthrough })]
    public async Task Each_format_offers_its_conversions(string name, ContainerKind target, ImportAction[] expected)
    {
        var path = Write(name, Path.GetExtension(name) switch
        {
            ".srt" => Srt,
            ".ass" => Ass,
            _ => Vtt,
        });
        MediaRemux.EnsureRegistered();
        var track = Assert.Single(await TrackImporter.InspectAsync(path, target, Ct));
        Assert.Equal([.. expected, ImportAction.Skip], track.Choices.Select(c => c.Action));
        Assert.Equal(expected[0], track.Choice.Action);
    }

    [Theory]
    [InlineData("subconv.srt", ContainerKind.Matroska, ImportAction.Passthrough, "S_TEXT/UTF8")]
    [InlineData("subconv.srt", ContainerKind.Matroska, ImportAction.ConvertToAss, "S_TEXT/ASS")]
    [InlineData("subconv.srt", ContainerKind.Matroska, ImportAction.ConvertToSsa, "S_TEXT/SSA")]
    [InlineData("subconv.srt", ContainerKind.Matroska, ImportAction.ConvertToWebVtt, "D_WEBVTT/SUBTITLES")]
    [InlineData("subconv.srt", ContainerKind.Mp4, ImportAction.ConvertToTx3g, "tx3g")]
    [InlineData("subconv.srt", ContainerKind.Mp4, ImportAction.ConvertToWebVtt, "wvtt")]
    [InlineData("subconv.ass", ContainerKind.Matroska, ImportAction.ConvertToSrt, "S_TEXT/UTF8")]
    [InlineData("subconv.ass", ContainerKind.Matroska, ImportAction.ConvertToWebVtt, "D_WEBVTT/SUBTITLES")]
    [InlineData("subconv.ass", ContainerKind.Mp4, ImportAction.ConvertToTx3g, "tx3g")]
    [InlineData("subconv.ass", ContainerKind.Mp4, ImportAction.ConvertToWebVtt, "wvtt")]
    [InlineData("subconv.vtt", ContainerKind.Matroska, ImportAction.ConvertToAss, "S_TEXT/ASS")]
    [InlineData("subconv.vtt", ContainerKind.Mp4, ImportAction.Passthrough, "wvtt")]
    [InlineData("subconv.vtt", ContainerKind.Mp4, ImportAction.ConvertToTx3g, "tx3g")]
    public async Task Conversions_keep_the_cues(string name, ContainerKind target, ImportAction action, string codec)
    {
        var source = Write(name, Path.GetExtension(name) switch
        {
            ".srt" => Srt,
            ".ass" => Ass,
            _ => Vtt,
        });
        var output = await SaveAsync(source, target, action);
        try
        {
            Assert.Equal(codec, Codec(output));
            var expected = Cues(source);
            var actual = codec == "wvtt" ? OwnCues(output) : Cues(output);
            if (codec == "tx3g")
            {
                // tx3g shows one sample at a time: overlapping cues are stacked into one per interval.
                Assert.All(expected, e => Assert.Contains(actual, a => a.Text.Contains(e.Text, StringComparison.Ordinal) && a.Start <= e.Start + 0.01 && a.End >= e.Start + 0.02));
            }
            else
            {
                Assert.Equal(expected.Select(e => e.Text), actual.Select(a => a.Text));
                Assert.Equal(expected.Select(e => (Math.Round(e.Start, 2), Math.Round(e.End, 2))), actual.Select(a => (Math.Round(a.Start, 2), Math.Round(a.End, 2))));
            }
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>ASS → tx3g in MP4 → read back: fonts, colours, bold, karaoke and placement survive.</summary>
    [Fact]
    public async Task Ass_to_tx3g_keeps_its_formatting()
    {
        var output = await SaveAsync(Write("subconv.ass", Ass), ContainerKind.Mp4, ImportAction.ConvertToTx3g);
        try
        {
            MediaRemux.EnsureRegistered();
            using var demuxer = MediaFormatRegistry.OpenDemuxer(output);
            var track = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Subtitle);
            Assert.Equal(CodecType.Tx3g, track.Config.Codec);
            var script = TextSubtitleConverter.Read(track);
            var first = script.Events.First(e => e.Text.StartsWith("Bold", StringComparison.Ordinal));
            Assert.True(script.StyleAt(first, 0).Bold);
            var red = script.StyleAt(first, first.Text.IndexOf("red", StringComparison.Ordinal));
            Assert.Equal(("Courier New", new SubtitleColor(255, 0, 0)), (red.Font, red.Primary!.Value));
            var karaoke = script.Events.First(e => e.Text.StartsWith("Karaoke", StringComparison.Ordinal));
            Assert.Equal(3, karaoke.Karaoke.Count);
            Assert.Equal([500L, 500L, 500L], karaoke.Karaoke.Select(k => k.DurationMs));
            Assert.NotNull(karaoke.Position);
            Assert.True(karaoke.Position!.Value.Y < script.Height / 2.0); // at the top
            Assert.Contains(script.Events, e => e.Text.Contains("Karaoke\noverlapping", StringComparison.Ordinal)); // stacked while both show
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>tx3g from MP4 into Matroska is recommended as ASS, keeping what tx3g had.</summary>
    [Fact]
    public async Task Tx3g_goes_to_matroska_as_ass()
    {
        var mp4 = await SaveAsync(Write("subconv.ass", Ass), ContainerKind.Mp4, ImportAction.ConvertToTx3g);
        string? mkv = null;
        try
        {
            MediaRemux.EnsureRegistered();
            var track = (await TrackImporter.InspectAsync(mp4, ContainerKind.Matroska, Ct)).Single(t => t.Config.Kind == TrackKind.Subtitle);
            Assert.Equal([ImportAction.ConvertToAss, ImportAction.ConvertToSsa, ImportAction.ConvertToSrt, ImportAction.ConvertToWebVtt, ImportAction.Skip],
                track.Choices.Select(c => c.Action));
            Assert.Equal(ImportAction.ConvertToAss, track.Choice.Action);
            var doc = new MediaDocument(null, ContainerKind.Matroska);
            TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(mp4, ContainerKind.Matroska, Ct));
            mkv = MediaProbe.TempPath(".mkv");
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = mkv }, ContainerKind.Matroska, null, Ct);
            Assert.Equal("S_TEXT/ASS", Codec(mkv));
            using var demuxer = MediaFormatRegistry.OpenDemuxer(mkv);
            var script = TextSubtitleConverter.Read(demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Subtitle));
            var karaoke = script.Events.First(e => e.Text.StartsWith("Karaoke", StringComparison.Ordinal));
            Assert.Equal(3, karaoke.Karaoke.Count);
            var red = script.Events.First(e => e.Text.StartsWith("Bold", StringComparison.Ordinal));
            Assert.Equal("Courier New", script.StyleAt(red, red.Text.IndexOf("red", StringComparison.Ordinal)).Font);
        }
        finally
        {
            MediaProbe.Delete(mp4);
            if (mkv is not null)
                MediaProbe.Delete(mkv);
        }
    }

    /// <summary>A subtitle track and its duplicate saved in two formats at once.</summary>
    [Fact]
    public async Task A_duplicated_track_is_saved_in_another_format()
    {
        MediaRemux.EnsureRegistered();
        var doc = new MediaDocument(null, ContainerKind.Matroska);
        TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(Video(), ContainerKind.Matroska, Ct));
        TrackImporter.AddToDocument(doc, await TrackImporter.InspectAsync(Write("subconv.srt", Srt), ContainerKind.Matroska, Ct));
        var original = doc.Tracks.OfType<SubtitleTrack>().Single();
        var copy = TrackImporter.Duplicate(doc, original);
        copy.Source = copy.Source! with { Import = copy.Source.Import! with { Action = ImportAction.ConvertToAss } };
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, null, Ct);
            var codecs = Fixtures.Run("ffprobe", $"-v error -select_streams s -show_entries stream=codec_name -of csv=p=0 {Fixtures.Quote(output)}")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(["subrip", "ass"], codecs);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>
    /// FFmpeg writes tx3g tracks without a size (0×0): their text is laid out on the video, so an 18-pixel font stays
    /// 18 pixels on a 240-line video instead of shrinking as if the canvas were 1080 lines.
    /// </summary>
    [Fact]
    public void Sizeless_tx3g_is_laid_out_on_the_video()
    {
        MediaProbe.RequireFfmpeg();
        var srt = MediaProbe.TempPath(".srt");
        var mp4 = MediaProbe.TempPath(".mp4");
        try
        {
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nHello\n");
            Fixtures.Run("ffmpeg", $"-v error -y -f lavfi -i testsrc=duration=3:size=320x240:rate=25 -i {Fixtures.Quote(srt)} -c:v libx264 -preset ultrafast -c:s mov_text {Fixtures.Quote(mp4)}");
            using var demuxer = MediaFormatRegistry.OpenDemuxer(mp4);
            var text = demuxer.Tracks.Single(t => t.Config.Codec == CodecType.Tx3g);
            var script = TextSubtitleConverter.Read(text, 320, 240);
            Assert.Equal((320, 240), (script.Width, script.Height));
            Assert.Equal("Hello", Assert.Single(script.Events).Text);
            Assert.InRange(script.StyleAt(script.Events[0], 0).Size ?? 0, 12, 30);
        }
        finally
        {
            File.Delete(srt);
            File.Delete(mp4);
        }
    }
}
