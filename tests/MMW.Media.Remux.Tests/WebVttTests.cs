using System.Globalization;
using System.Text;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// WebVTT cues keep their text, times and cue settings through .vtt files, Matroska (D_WEBVTT as FFmpeg writes it,
/// S_TEXT/WEBVTT with BlockAdditions as mkvmerge writes it) and MP4 (ISO/IEC 14496-30 'wvtt', as GPAC writes it).
/// </summary>
public sealed class WebVttTests
{
    public WebVttTests() => MediaRemux.EnsureRegistered();

    private const string Vtt = """
        WEBVTT
        Kind: captions

        STYLE
        ::cue { color: yellow }
        ::cue(b) { color: red }

        REGION
        id:top
        width:40%
        regionanchor:0%,0%

        NOTE a comment before the cues

        intro
        00:00:00.500 --> 00:00:03.000 position:10% align:start
        <b>Bold</b> opening

        00:00:01.000 --> 00:00:02.000 line:0 vertical:rl size:50%
        <i>Italic</i> overlap
        second line

        00:00:02.500 --> 00:00:04.000
        Plain cue

        00:00:04.000 --> 00:00:05.000 align:end
        Adjacent <b><i>both</i></b>

        """;

    private static readonly (long Start, long End, string Text, string? Settings)[] s_expected =
    [
        (500, 3000, "<b>Bold</b> opening", "position:10% align:start"),
        (1000, 2000, "<i>Italic</i> overlap\nsecond line", "line:0 vertical:rl size:50%"),
        (2500, 4000, "Plain cue", null),
        (4000, 5000, "Adjacent <b><i>both</i></b>", "align:end"),
    ];

    private static string Source() => Text("webvtt-settings.vtt", Vtt);

    private static string Q(string path) => Fixtures.Quote(path);

    /// <summary>The cues of the file's subtitle track, read with this application's demuxers (milliseconds).</summary>
    private static List<(long Start, long End, string Text, string? Settings)> ReadCues(string path, out string header)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        var track = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Subtitle);
        Assert.Equal(CodecType.WebVtt, track.Config.Codec);
        header = track.Config.Extradata is { } extra ? Encoding.UTF8.GetString(extra) : string.Empty;
        var scale = 1000.0 / track.Config.Timescale;
        var cues = new List<(long, long, string, string?)>();
        track.Reset();
        while (track.ReadNext() is { } sample)
        {
            var start = (long)Math.Round(sample.Pts * scale);
            cues.Add((start, start + (long)Math.Round(sample.Duration * scale), Encoding.UTF8.GetString(sample.GetData().Span), sample.CueSettings));
        }

        return cues;
    }

    private static void AssertCues(string path, bool header = true)
    {
        Assert.Equal(s_expected, ReadCues(path, out var text));
        Assert.StartsWith("WEBVTT", text, StringComparison.Ordinal);
        if (!header)
            return;
        Assert.Contains("STYLE\n::cue { color: yellow }\n::cue(b) { color: red }", text, StringComparison.Ordinal);
        Assert.Contains("REGION\nid:top\nwidth:40%\nregionanchor:0%,0%", text, StringComparison.Ordinal);
    }

    private static async Task<string> SaveAsync(string source, ContainerKind target)
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var track = Assert.Single(tracks);
        Assert.Equal(TrackSupportLevel.Passthrough, track.Support.Level);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    private static async Task<string> ExportAsync(string path)
    {
        using var demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions());
        var track = demuxer.Tracks.Single(t => t.Config.Kind == TrackKind.Subtitle);
        Assert.Equal(".vtt", TrackExport.Extension(track.Config));
        var output = MediaProbe.TempPath(".vtt");
        await TrackExport.ExportAsync(track, output, null, Ct);
        return output;
    }

    /// <summary>Exported to .vtt, the cues keep their settings on the timing line, and the file reads back the same.</summary>
    private static async Task AssertExportAsync(string path, bool header = true)
    {
        var vtt = await ExportAsync(path);
        try
        {
            var text = await File.ReadAllTextAsync(vtt, Ct);
            Assert.Contains("00:00:00.500 --> 00:00:03.000 position:10% align:start\n<b>Bold</b> opening\n", text, StringComparison.Ordinal);
            Assert.Contains("00:00:01.000 --> 00:00:02.000 line:0 vertical:rl size:50%\n", text, StringComparison.Ordinal);
            Assert.Contains("00:00:02.500 --> 00:00:04.000\nPlain cue\n", text, StringComparison.Ordinal);
            AssertCues(vtt, header);
        }
        finally
        {
            MediaProbe.Delete(vtt);
        }
    }

    [Fact]
    public async Task Vtt_files_keep_cue_settings_and_header_blocks()
    {
        AssertCues(Source());
        ReadCues(Source(), out var header);
        Assert.Contains("NOTE a comment before the cues", header, StringComparison.Ordinal);
        await AssertExportAsync(Source());
    }

    [Fact]
    public async Task Matroska_keeps_cue_settings_as_ffmpeg_reads_them()
    {
        MediaProbe.RequireFfmpeg();
        var mkv = await SaveAsync(Source(), ContainerKind.Matroska);
        try
        {
            AssertCues(mkv);
            var codecId = Fixtures.Run("ffprobe", $"-v error -select_streams s -show_entries stream=codec_name -of csv=p=0 {Q(mkv)}").Trim();
            Assert.Equal("webvtt", codecId);

            // FFmpeg's WebVTT muxer writes the settings it reads from the blocks.
            var vtt = Fixtures.Run("ffmpeg", $"-v error -i {Q(mkv)} -map 0:s:0 -c:s copy -f webvtt -").Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("00:00.500 --> 00:03.000 position:10% align:start\n<b>Bold</b> opening", vtt, StringComparison.Ordinal);
            Assert.Contains("00:01.000 --> 00:02.000 line:0 vertical:rl size:50%\n<i>Italic</i> overlap\nsecond line", vtt, StringComparison.Ordinal);
            Assert.Contains("00:02.500 --> 00:04.000\nPlain cue", vtt, StringComparison.Ordinal);
            Assert.Contains("00:04.000 --> 00:05.000 align:end\nAdjacent", vtt, StringComparison.Ordinal);

            await AssertExportAsync(mkv);
        }
        finally
        {
            MediaProbe.Delete(mkv);
        }
    }

    [Fact]
    public async Task Mp4_stores_cues_as_iso_14496_30_samples()
    {
        MediaProbe.RequireFfmpeg();
        var mp4 = await SaveAsync(Source(), ContainerKind.Mp4);
        try
        {
            // Overlapping cues read back as the original cues.
            AssertCues(mp4);

            // One sample per interval of unchanged active cues; 'vtte' for gaps. FFmpeg reads 'wvtt' as a data stream.
            Assert.Equal("wvtt", Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_tag_string -of csv=p=0 {Q(mp4)}").Trim());
            var packets = Fixtures.Run("ffprobe", $"-v error -show_entries packet=pts_time,duration_time -of csv=p=0 {Q(mp4)}")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray())
                .Select(p => (Start: p[0], End: Math.Round(p[0] + p[1], 3)))
                .ToList();
            Assert.Equal([(0.0, 0.5), (0.5, 1.0), (1.0, 2.0), (2.0, 2.5), (2.5, 3.0), (3.0, 4.0), (4.0, 5.0)], packets);
            var data = Fixtures.Run("ffprobe", $"-v error -show_packets -show_data {Q(mp4)}");
            Assert.Contains("vtte", data, StringComparison.Ordinal);
            Assert.Contains("sttg", data, StringComparison.Ordinal);

            // GPAC rebuilds the cues with their settings.
            if (Fixtures.HasTool("MP4Box"))
            {
                var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var raw = Path.Combine(dir, "gpac.vtt");
                    Fixtures.Run("MP4Box", $"-quiet -raw 1 {Q(mp4)} -out {Q(raw)}");
                    var text = (await File.ReadAllTextAsync(raw, Ct)).Replace("\r\n", "\n", StringComparison.Ordinal);
                    Assert.Contains("00:00.500 --> 00:03.000 position:10% align:start\n<b>Bold</b> opening", text, StringComparison.Ordinal);
                    Assert.Contains("00:01.000 --> 00:02.000 line:0 vertical:rl size:50%\n<i>Italic</i> overlap\nsecond line", text, StringComparison.Ordinal);
                    Assert.Contains("00:04.000 --> 00:05.000 align:end\nAdjacent <b><i>both</i></b>", text, StringComparison.Ordinal);
                    Assert.Contains("::cue(b) { color: red }", text, StringComparison.Ordinal);
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            }

            await AssertExportAsync(mp4);

            // MP4 → Matroska → MP4 keeps them too.
            var mkv = await SaveAsync(mp4, ContainerKind.Matroska);
            string? back = null;
            try
            {
                AssertCues(mkv);
                back = await SaveAsync(mkv, ContainerKind.Mp4);
                AssertCues(back);
            }
            finally
            {
                MediaProbe.Delete(mkv);
                if (back is not null)
                    MediaProbe.Delete(back);
            }
        }
        finally
        {
            MediaProbe.Delete(mp4);
        }
    }

    [Fact]
    public async Task Gpac_mp4_imports_with_cue_settings()
    {
        if (!Fixtures.HasTool("MP4Box"))
            Assert.Skip("MP4Box is not installed.");
        var source = Source();
        var mp4 = Fixtures.Get("webvtt-gpac.mp4", "MP4Box", $"-quiet -add {Q(source)} -new {{out}}");

        // GPAC keeps only the first header block in 'vttC' and repeats a cue (with its 'iden') in every sample it spans.
        AssertCues(mp4, header: false);
        await AssertExportAsync(mp4, header: false);
        var copy = await SaveAsync(mp4, ContainerKind.Mp4);
        try
        {
            AssertCues(copy, header: false);
        }
        finally
        {
            MediaProbe.Delete(copy);
        }
    }

    [Fact]
    public async Task Ffmpeg_matroska_imports_with_cue_settings()
    {
        MediaProbe.RequireFfmpeg();
        var mkv = Fixtures.Get("webvtt-ffmpeg.mkv", "ffmpeg", $"-v error -y -i {Q(Source())} -c:s copy {{out}}");
        Assert.Equal(s_expected, ReadCues(mkv, out _));
        await AssertExportAsync(mkv, header: false);
        var mp4 = await SaveAsync(mkv, ContainerKind.Mp4);
        try
        {
            Assert.Equal(s_expected, ReadCues(mp4, out _));
        }
        finally
        {
            MediaProbe.Delete(mp4);
        }
    }

    [Fact]
    public async Task Mkvmerge_matroska_imports_with_cue_settings()
    {
        CrossContainerTests.MkvToolsRequired();
        var mkv = Fixtures.Get("webvtt-mkvmerge.mkv", "mkvmerge", $"-q -o {{out}} {Q(Source())}");
        AssertCues(mkv);
        await AssertExportAsync(mkv);

        // Matroska → Matroska keeps mkvmerge's S_TEXT/WEBVTT with its block additions; → MP4 keeps the settings.
        var copy = await SaveAsync(mkv, ContainerKind.Matroska);
        var mp4 = await SaveAsync(mkv, ContainerKind.Mp4);
        try
        {
            AssertCues(copy);
            AssertCues(mp4);
            var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var raw = Path.Combine(dir, "mkvextract.vtt");
                Fixtures.Run("mkvextract", $"{Q(copy)} tracks 0:{Q(raw)}");
                var text = (await File.ReadAllTextAsync(raw, Ct)).Replace("\r\n", "\n", StringComparison.Ordinal);
                Assert.Contains("intro\n00:00:00.500 --> 00:00:03.000 position:10% align:start\n<b>Bold</b> opening", text, StringComparison.Ordinal);
                Assert.Contains("00:00:01.000 --> 00:00:02.000 line:0 vertical:rl size:50%", text, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            MediaProbe.Delete(copy, mp4);
        }
    }
}
