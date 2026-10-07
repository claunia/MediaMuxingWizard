using System.Globalization;
using System.Text.Json;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Remuxes every small file of the optional corpus (<c>MMW_CORPUS</c>) to the other container family and checks
/// streams, codecs, durations and per-packet payload hashes. Corpus files are only read; outputs go to temp files.
/// </summary>
public sealed class CorpusRemuxTests
{
    /// <summary>Largest file remuxed (MiB); override with <c>MMW_CORPUS_MAX_MB</c>.</summary>
    private static long MaxSize =>
        (long.TryParse(Environment.GetEnvironmentVariable("MMW_CORPUS_MAX_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : 64) * 1024 * 1024;

    public static IEnumerable<TheoryDataRow<string>> MatroskaFiles() =>
        Corpus.Files(".mkv", ".mka", ".webm").Select(f => new TheoryDataRow<string>(f));

    public static IEnumerable<TheoryDataRow<string>> Mp4Files() =>
        Corpus.Files(".mp4", ".m4v", ".m4a", ".mov").Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(MatroskaFiles))]
    public Task Matroska_file_remuxes_to_mp4(string file) => RemuxAsync(file, Mkv, ContainerKind.Mp4, ".mp4");

    [Theory]
    [MemberData(nameof(Mp4Files))]
    public Task Mp4_file_remuxes_to_matroska(string file) => RemuxAsync(file, Mp4, ContainerKind.Matroska, ".mkv");

    private static async Task RemuxAsync(string file, IContainerHandler handler, ContainerKind target, string extension)
    {
        Corpus.Require(file);
        MediaProbe.RequireFfmpeg();
        if (new FileInfo(file).Length > MaxSize)
            Assert.Skip("Larger than the corpus remux limit.");

        var doc = await handler.ReadAsync(file, Ct);
        var sourceKind = doc.Container;
        var importable = await TrackImporter.InspectAsync(file, target, Ct);
        var dropped = new List<string>();
        var sourceIndexes = SourceStreamIndexes(file, doc);
        var kept = new List<(Track Track, int SourceIndex)>();
        foreach (var track in doc.Tracks.Where(t => t is not ChapterTrack).ToList())
        {
            var info = importable.FirstOrDefault(i => i.TrackId == track.Id);
            if (info is null || !info.Support.CanMux)
            {
                dropped.Add($"{track.Format} ({info?.Support.Reason ?? "not demuxable"})");
                doc.Tracks.Remove(track);
                continue;
            }

            kept.Add((track, sourceIndexes[track.Id]));
        }

        if (dropped.Count > 0)
            TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)}: not remuxed to {target}: {string.Join(", ", dropped)}");
        if (!kept.Any(k => k.Track is VideoTrack or AudioTrack))
            Assert.Skip($"No track can be written to {target}: {string.Join(", ", dropped)}");

        var output = MediaProbe.TempPath(extension);
        try
        {
            await handler.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
            var source = MediaProbe.Streams(file);
            var result = MediaProbe.Streams(output).Where(s => s.Type != "attachment" && s.Type != "data").ToList();
            // WebVTT is kept natively in MP4 ('wvtt'), which FFmpeg lists as a data stream, like the chapter text tracks.
            var probed = kept.Where(k => !(target == ContainerKind.Mp4 && k.Track is SubtitleTrack && k.Track.Format == "WebVTT")).ToList();
            Assert.Equal(probed.Count, result.Count);
            // Timestamp complaints that ffmpeg also has with its own remux of the same streams (e.g. TrueHD units
            // 1/1200 s apart in Matroska's 1 ms timestamps, VVC without DTS in Matroska, VFR sources) are a property
            // of the source/target pair, not of this muxer.
            var errors = MediaProbe.DemuxErrors(output);
            if (errors.Length > 0)
            {
                var reference = MediaProbe.TempPath(extension);
                try
                {
                    var maps = string.Join(' ', kept.Select(k => $"-map 0:{k.SourceIndex}"));
                    Fixtures.Run("ffmpeg", $"-v quiet -y -i {Fixtures.Quote(file)} {maps} -c copy {Fixtures.Quote(reference)}");
                    var referenceErrors = File.Exists(reference) ? MediaProbe.DemuxErrors(reference) : "ffmpeg could not remux the source";
                    Assert.True(referenceErrors.Length > 0, "Demuxing errors in the remuxed file only: " + errors);
                    TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)}: ffmpeg reports timestamp issues for its own remux too.");
                }
                catch (InvalidOperationException ex)
                {
                    TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)}: ffmpeg cannot remux the source either ({ex.Message.Split('\n')[0]}).");
                }
                finally
                {
                    MediaProbe.Delete(reference);
                }
            }

            if (target == ContainerKind.Matroska && Fixtures.HasTool("mkvmerge"))
            {
                var id = MediaProbe.MkvIdentify(output);
                Assert.Empty(id.GetProperty("errors").EnumerateArray());
            }

            double? presentationShift = null;
            for (var i = 0; i < probed.Count; i++)
            {
                var (track, sourceIndex) = probed[i];
                var src = source.First(s => s.Index == sourceIndex);
                var dst = result[i];
                Assert.Equal(src.Type, dst.Type);
                // FFmpeg's MP4 demuxer has no mapping for the 'avst' (AVS2) sample entry.
                if (src.Type != "subtitle" && !(src.Codec == "avs2" && target == ContainerKind.Mp4 && dst.Codec.Length == 0))
                    Assert.Equal(src.Codec, dst.Codec);
                if (track is not (VideoTrack or AudioTrack))
                    continue;

                if (src.Codec.StartsWith("pcm_", StringComparison.Ordinal))
                {
                    // PCM packetisation differs between containers (and ffmpeg keeps whole packets across the end of
                    // an edit list where the importer cuts exactly): compare the whole stream when nothing was cut.
                    var a = StreamMd5(file, sourceIndex);
                    var b = StreamMd5(output, dst.Index);
                    if (a != b)
                        AssertDuration(src.Duration + (presentationShift ?? 0), dst.Duration, 0.05);
                }
                else
                {
                    var a = MediaProbe.PacketHashes(file, $"-map 0:{sourceIndex}")[0];
                    var b = MediaProbe.PacketHashes(output, $"-map 0:{dst.Index}")[0];

                    // ffmpeg drops MP4 samples hidden by an edit list; the remux keeps them (like mkvmerge does), and
                    // a Matroska target then shows them (shifting every track), so the durations differ by design.
                    if (!a.SequenceEqual(b) && sourceKind == ContainerKind.Mp4)
                        a = MediaProbe.PacketHashes(file, $"-map 0:{sourceIndex}", "-ignore_editlist 1")[0];

                    Assert.True(a.SequenceEqual(b), $"{src.Codec} stream {sourceIndex}: {a.Count} packets in, {b.Count} out, first difference at {FirstDifference(a, b)}.");
                }

                if (src.Duration > 0 && dst.Duration > 0)
                {
                    var frame = track is VideoTrack v && v.FrameRate > 0 ? 1.0 / v.FrameRate : 0.05;
                    var tolerance = Math.Max(frame, 0.05) + 0.002;
                    // A Matroska target shows video frames an MP4 edit list hides before time zero, moving every track
                    // by the same amount (as mkvmerge does): the shift is measured on the video track, the others must
                    // follow it.
                    if (presentationShift is null && track is VideoTrack && sourceKind == ContainerKind.Mp4 && target == ContainerKind.Matroska &&
                        dst.Duration - src.Duration > tolerance)
                    {
                        presentationShift = dst.Duration - src.Duration;
                        Assert.InRange(presentationShift.Value, 0, 10);
                        TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)}: video hidden by the edit list is shown; presentation shifted by {presentationShift:0.###} s.");
                        continue;
                    }

                    AssertDuration(src.Duration + (presentationShift ?? 0), dst.Duration, tolerance);
                }
            }
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    private static int FirstDifference(List<string> a, List<string> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i])
                return i;
        }

        return Math.Min(a.Count, b.Count);
    }

    /// <summary>ffprobe stream index of each document track (MP4: by track ID; Matroska: by position).</summary>
    private static Dictionary<uint, int> SourceStreamIndexes(string file, MediaDocument doc)
    {
        var json = Fixtures.Run("ffprobe", $"-v error -show_entries stream=index,id,codec_type -of json {Fixtures.Quote(file)}");
        using var probe = JsonDocument.Parse(json);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var result = new Dictionary<uint, int>();
        var tracks = doc.Tracks.Where(t => t is not ChapterTrack).ToList();
        if (doc.Container == ContainerKind.Mp4)
        {
            foreach (var s in streams)
            {
                if (s.TryGetProperty("id", out var id) && id.GetString() is { } hex && hex.StartsWith("0x", StringComparison.Ordinal))
                    result[uint.Parse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = s.GetProperty("index").GetInt32();
            }
        }
        else
        {
            for (var i = 0; i < tracks.Count; i++)
                result[tracks[i].Id] = i;
        }

        return result;
    }

    private static string StreamMd5(string path, int index) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:{index} -c copy -f md5 -").Trim();
}
