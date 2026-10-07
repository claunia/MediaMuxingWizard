using System.Text.Json;
using MMW.TestSupport;

namespace MMW.Cli.Tests;

/// <summary>
/// The track commands: probe (tracks and their actions), import with picked tracks and per-track options, remux with
/// kept tracks, actions and duplicates, tracks --action, and extract of several tracks.
/// </summary>
public class TrackCommandTests
{
    private const string Srt = "1\n00:00:00,200 --> 00:00:01,000\nHello\n\n2\n00:00:01,200 --> 00:00:01,800\n<i>World</i>\n";

    private const string Ass = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 320
        PlayResY: 240

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,16,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.20,0:00:01.00,Default,,0,0,0,,{\b1}Bold{\b0} line
        """;

    /// <summary>An MKV with H.264 (1), AAC (2), SubRip (3) and ASS (4).</summary>
    private static string Source()
    {
        if (!Fixtures.HasTool("ffmpeg"))
            Assert.Skip("ffmpeg not installed.");
        var srt = Path.Combine(Fixtures.GeneratedDirectory, "cli-tracks.srt");
        var ass = Path.Combine(Fixtures.GeneratedDirectory, "cli-tracks.ass");
        File.WriteAllText(srt, Srt);
        File.WriteAllText(ass, Ass);
        var mkv = Fixtures.Get("cli-tracks.mkv", "ffmpeg",
            "-v error -y -f lavfi -i testsrc2=size=320x240:rate=25:duration=2 -f lavfi -i sine=f=440:duration=2 " +
            $"-i {Fixtures.Quote(srt)} -i {Fixtures.Quote(ass)} -map 0 -map 1 -map 2 -map 3 -c:v libx264 -pix_fmt yuv420p -c:a aac " +
            "-c:s:0 srt -c:s:1 ass -metadata:s:a:0 language=eng {out}");
        return Fixtures.CopyToTemp(mkv);
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var queue = Path.Combine(Path.GetTempPath(), "mmw-tests", "cli-queue-" + Guid.NewGuid().ToString("N") + ".json");
        var code = await CommandLine.RunAsync(args, output, error, queue);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>The (codec ID, language, name) of each track of a Matroska file, from mkvmerge.</summary>
    private static List<(string Codec, string Language, string Name)> Tracks(string mkv) =>
        JsonDocument.Parse(Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(mkv)}")).RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("properties"))
            .Select(p => (p.GetProperty("codec_id").GetString()!, p.TryGetProperty("language", out var l) ? l.GetString()! : "und",
                p.TryGetProperty("track_name", out var n) ? n.GetString()! : string.Empty))
            .ToList();

    private static void RequireMkvmerge()
    {
        if (!Fixtures.HasTool("mkvmerge"))
            Assert.Skip("mkvmerge not installed.");
    }

    [Fact]
    public async Task Probe_lists_tracks_and_their_actions()
    {
        var source = Source();
        var (code, text, err) = await Run("probe", source);
        Assert.True(code == 0, err);
        Assert.Contains("Subtitle  SRT", text, StringComparison.Ordinal);
        Assert.Contains("MP4: *tx3g webvtt skip", text, StringComparison.Ordinal);
        Assert.Contains("MKV: *copy ass ssa webvtt skip", text, StringComparison.Ordinal);

        var (_, json, _) = await Run("probe", source, "--target", "mkv", "--json");
        var tracks = JsonDocument.Parse(json).RootElement[0].GetProperty("tracks");
        Assert.Equal(4, tracks.GetArrayLength());
        Assert.Equal("copy", tracks[3].GetProperty("actions").GetProperty("mkv").GetProperty("recommended").GetString());
    }

    [Fact]
    public async Task Import_picks_tracks_with_their_own_actions_and_duplicates()
    {
        RequireMkvmerge();
        var source = Source();
        var target = Source();
        var (code, text, err) = await Run("import", target, source + ":3,4", "--action", "4=srt", "--language", "3=fra", "--name", "4=From ASS",
            "--duplicate", "3=webvtt");
        Assert.True(code == 0, err + text);
        var tracks = Tracks(target);
        Assert.Equal(["V_MPEG4/ISO/AVC", "A_AAC", "S_TEXT/UTF8", "S_TEXT/ASS", "S_TEXT/UTF8", "D_WEBVTT/SUBTITLES", "S_TEXT/UTF8"], tracks.Select(t => t.Codec));
        Assert.Equal("fre", tracks[4].Language); // picked track 3, renamed language
        Assert.Equal("fre", tracks[5].Language); // its duplicate
        Assert.Equal("From ASS", tracks[6].Name); // picked track 4, converted to SubRip
    }

    [Fact]
    public async Task Import_with_several_sources_names_tracks_by_source()
    {
        RequireMkvmerge();
        var target = Source();
        var (code, text, err) = await Run("import", target, Source(), Source(), "--track", "1:3", "--track", "2:4", "--action", "2:4=webvtt", "--dry-run");
        Assert.True(code == 0, err + text);
        Assert.Contains("track 3: Subtitle SRT (und) → copy", text, StringComparison.Ordinal);
        Assert.Contains("track 4: Subtitle ASS (und) → webvtt", text, StringComparison.Ordinal);
        Assert.Equal(4, Tracks(target).Count); // dry run: nothing saved
    }

    [Fact]
    public async Task Remux_keeps_picked_tracks_converts_and_duplicates()
    {
        RequireMkvmerge();
        var source = Source();
        var mp4 = Path.ChangeExtension(source, ".mp4");
        var (code, text, err) = await Run("remux", source, mp4, "--track", "1,3,4", "--action", "4=webvtt", "--duplicate", "3=webvtt", "--forced", "3=true");
        Assert.True(code == 0, err + text);
        Assert.Contains("Leaving out track 2 (AAC)", text, StringComparison.Ordinal);
        var tags = Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_tag_string -of csv=p=0 {Fixtures.Quote(mp4)}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["avc1", "tx3g", "wvtt", "wvtt"], tags);
    }

    [Fact]
    public async Task Remux_can_still_drop_what_the_target_cannot_hold()
    {
        RequireMkvmerge();
        var source = Source();
        var (code, text, _) = await Run("remux", source, Path.ChangeExtension(source, ".mp4"), "--drop-unsupported", "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("track 3 (SRT) → tx3g", text, StringComparison.Ordinal); // converted, not dropped: tx3g is MP4's own
        Assert.False(File.Exists(Path.ChangeExtension(source, ".mp4")));
    }

    [Fact]
    public async Task Tracks_action_converts_in_place()
    {
        RequireMkvmerge();
        var source = Source();
        var (code, text, err) = await Run("tracks", source, "--action", "3=ass", "--track", "2", "--name", "Main audio");
        Assert.True(code == 0, err + text);
        var tracks = Tracks(source);
        Assert.Equal("S_TEXT/ASS", tracks[2].Codec);
        Assert.Equal("Main audio", tracks[1].Name);
        Assert.Equal(2, (await Run("tracks", source, "--action", "ass")).Code); // the track must be named
    }

    [Fact]
    public async Task Extract_writes_several_or_all_tracks()
    {
        var source = Source();
        var dir = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "-extract");
        var (code, _, err) = await Run("extract", source, "3,4", "--output-dir", dir);
        Assert.True(code == 0, err);
        Assert.Equal(["srt", "ass"], Directory.GetFiles(dir).Order().Select(f => Path.GetExtension(f).TrimStart('.')));
        (code, _, err) = await Run("extract", source, "--all", "--output-dir", dir);
        Assert.True(code == 0, err);
        Assert.Equal(4, Directory.GetFiles(dir).Length);
        var single = Path.Combine(dir, "only.srt");
        Assert.Equal(0, (await Run("extract", source, "3", single)).Code);
        Assert.StartsWith("1\n", File.ReadAllText(single).Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_track_references_are_usage_errors()
    {
        var source = Source();
        var (code, _, err) = await Run("import", Source(), source + ":9");
        Assert.Equal(2, code);
        Assert.Contains("has no track 9", err, StringComparison.Ordinal);
        (code, _, err) = await Run("remux", source, Path.ChangeExtension(source, ".mp4"), "--action", "1=aac");
        Assert.Equal(2, code);
        Assert.Contains("choose one of: copy, skip", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Srt_ocr_for_an_mp4_is_its_tx3g_ocr()
    {
        // MP4 offers OCR to tx3g; OCR to SubRip there becomes tx3g anyway, so "srt-ocr" names it.
        MMW.Core.Media.ImportChoice[] mp4 =
        [
            new(MMW.Core.Media.ImportAction.Passthrough, "Passthru"),
            new(MMW.Core.Media.ImportAction.ConvertToTx3g, "Tx3g (OCR)", Ocr: true),
            new(MMW.Core.Media.ImportAction.Skip, "Skip"),
        ];
        Assert.Equal(MMW.Core.Media.ImportAction.ConvertToTx3g, TrackCommands.Resolve(mp4, "srt-ocr", 0, "track 3").Action);
        Assert.Equal(MMW.Core.Media.ImportAction.ConvertToTx3g, TrackCommands.Resolve(mp4, "tx3g-ocr", 0, "track 3").Action);
    }
}
