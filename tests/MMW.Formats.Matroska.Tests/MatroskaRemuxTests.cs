using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Matroska.Tests;

/// <summary>Structural Matroska saves (track removal and reordering), which remux the file.</summary>
public sealed class MatroskaRemuxTests
{
    private static readonly MatroskaHandler s_handler = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_a_track_remuxes_and_keeps_the_rest()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Full());
        try
        {
            var before = MediaProbe.PacketHashes(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            var audio = doc.Tracks.OfType<AudioTrack>().Single();
            doc.Tracks.Remove(audio);
            doc.Tracks.OfType<VideoTrack>().Single().Name = "Renamed video";
            await s_handler.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);

            var id = MkvFixtures.AssertValid(path);
            var tracks = id.GetProperty("tracks").EnumerateArray().ToList();
            Assert.Equal(["video", "subtitles"], tracks.Select(t => t.GetProperty("type").GetString()!));
            Assert.Equal("Renamed video", tracks[0].GetProperty("properties").GetProperty("track_name").GetString());
            Assert.Equal("spa", tracks[1].GetProperty("properties").GetProperty("language").GetString());
            Assert.Equal(2, id.GetProperty("attachments").GetArrayLength());

            var after = MediaProbe.PacketHashes(path);
            Assert.Equal(before[0], after[0]);
            Assert.False(doc.IsDirty);
            Assert.Equal(2, doc.Tracks.Count);
            Assert.All(doc.Tracks, t => Assert.False(t.IsPending));

            // The new file can be edited in place again.
            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal(2, reread.Chapters.Count);
            Assert.Equal("Someone", reread.Metadata.GetString(Core.Metadata.TagId.Artist));
            doc.Tracks[0].Name = "Again";
            await s_handler.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);
            Assert.Equal("Again", (await s_handler.ReadAsync(path, Ct)).Tracks[0].Name);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }

    [Fact]
    public async Task Reordering_tracks_remuxes()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Basic());
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var before = MediaProbe.PacketHashes(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            doc.Tracks.Move(1, 0);
            await s_handler.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);

            var id = MkvFixtures.AssertValid(output);
            Assert.Equal(["audio", "video"], id.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("type").GetString()!));
            // framemd5 numbers streams in mapping order (video first, then audio) in both files.
            var after = MediaProbe.PacketHashes(output);
            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[1], after[1]);
            Assert.Equal(output, doc.Path);
            Assert.Empty(MediaProbe.DemuxErrors(output));

            var streams = MediaProbe.Streams(output);
            var source = MediaProbe.Streams(path);
            Assert.InRange(streams.First(s => s.Type == "video").Duration, source.First(s => s.Type == "video").Duration - 0.05, source.First(s => s.Type == "video").Duration + 0.05);
        }
        finally
        {
            MediaProbe.Delete(path, output);
        }
    }
}
