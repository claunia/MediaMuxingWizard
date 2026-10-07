using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// A subtitle track duplicated with <see cref="TrackImporter.Duplicate"/>: the original (already in the file) and its
/// pending copy read the same source track, each with its own action, and both are written.
/// </summary>
public sealed class DuplicateTrackTests
{
    /// <summary>The cues of a subtitle stream as SubRip text (ffmpeg's conversion, so MKV SRT and MP4 tx3g compare).</summary>
    private static string Cues(string path, int subtitleIndex) =>
        Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:s:{subtitleIndex} -c:s srt -f srt -").Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [InlineData(".mkv", ContainerKind.Matroska, "subrip")]
    [InlineData(".mp4", ContainerKind.Mp4, "mov_text")]
    public async Task Original_and_duplicate_are_both_written(string extension, ContainerKind target, string codec)
    {
        var path = Fixtures.CopyToTemp(MkvH264AacSrt());
        var output = MediaProbe.TempPath(extension);
        try
        {
            var doc = await Mkv.ReadAsync(path, Ct);
            var original = doc.Tracks.OfType<SubtitleTrack>().Single();
            original.Name = "Original";
            var inspected = await TrackImporter.InspectAsync(path, doc.Container, Ct);
            var copy = TrackImporter.Duplicate(doc, original, inspected.Single(t => t.TrackId == original.Source!.TrackId));
            copy.Name = "Copy";
            Assert.Equal(doc.Tracks.IndexOf(original) + 1, doc.Tracks.IndexOf(copy));
            Assert.Equal(original.Source!.TrackId, copy.Source!.TrackId);
            Assert.True(RemuxPolicy.HasImportedTracks(doc));

            TrackConversions.SetAction(doc, original, ImportAction.Passthrough);
            TrackConversions.SetAction(doc, copy, ImportAction.Passthrough);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, cancellationToken: Ct);

            var subtitles = MediaProbe.Streams(output).Where(s => s.Type == "subtitle").ToList();
            Assert.Equal(2, subtitles.Count);
            Assert.All(subtitles, s => Assert.Equal(codec, s.Codec));
            Assert.All(subtitles, s => Assert.Equal("spa", s.Language));
            var cues = Cues(output, 0);
            Assert.Contains("Overlap", cues, StringComparison.Ordinal);
            Assert.Equal(cues, Cues(output, 1));
            if (target == ContainerKind.Matroska)
                Assert.Equal(Cues(path, 0), cues); // tx3g simplifies the styling: compared with the source only in MKV
            else
                Assert.All(["Hello", "World"], word => Assert.Contains(word, cues, StringComparison.Ordinal));
            Assert.Empty(MediaProbe.DemuxErrors(output));

            // The document follows the new file: both tracks are in it, the copy still disabled.
            var tracks = doc.Tracks.OfType<SubtitleTrack>().ToList();
            Assert.Equal(2, tracks.Count);
            Assert.All(tracks, t => Assert.False(t.IsPending));
            Assert.NotEqual(tracks[0].Id, tracks[1].Id);
            Assert.False(copy.Enabled);
        }
        finally
        {
            MediaProbe.Delete(path, output);
        }
    }

    [Fact]
    public async Task Duplicate_of_a_pending_track_shares_its_source_and_takes_another_action()
    {
        var path = Fixtures.CopyToTemp(MkvH264AacSrt());
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var doc = await Mkv.ReadAsync(path, Ct);
            var imported = await TrackImporter.InspectAsync(Text("duplicate-import.srt", Srt), doc.Container, Ct);
            var pending = Assert.IsType<SubtitleTrack>(Assert.Single(TrackImporter.AddToDocument(doc, imported)));
            var copy = await TrackImporter.DuplicateAsync(doc, pending, Ct);
            Assert.Equal(pending.Source, copy.Source);

            TrackConversions.SetAction(doc, copy, ImportAction.ConvertToTx3g);
            Assert.Equal(ImportAction.Passthrough, pending.Source!.Import!.Action);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, cancellationToken: Ct);

            var subtitles = MediaProbe.Streams(output).Where(s => s.Type == "subtitle").Select(s => s.Codec).ToList();
            // Matroska keeps timed text as SubRip: the three tracks (in-file, pending, its copy) are all SRT.
            Assert.Equal(["subrip", "subrip", "subrip"], subtitles);
            Assert.Equal(Cues(output, 1), Cues(output, 2));
        }
        finally
        {
            MediaProbe.Delete(path, output);
        }
    }
}
