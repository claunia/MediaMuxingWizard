using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Mp4.Tests;

/// <summary>Reads (and, for small files, edits copies of) every MP4/MOV file in the optional corpus.</summary>
public class Mp4CorpusTests
{
    private const long MaxEditSize = 64L * 1024 * 1024;

    private readonly Mp4Handler _handler = new();

    public static IEnumerable<TheoryDataRow<string>> Files() =>
        Corpus.Files(".mp4", ".m4v", ".m4a", ".m4b", ".mov", ".3gp").Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Reads_like_ffprobe(string file)
    {
        Corpus.Require(file);
        var doc = await _handler.ReadAsync(file, TestContext.Current.CancellationToken);
        var streams = Mp4Fixtures.Probe(file).GetProperty("streams").EnumerateArray().ToList();

        int Count(string type) => streams.Count(s =>
            s.GetProperty("codec_type").GetString() == type &&
            !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1));

        Assert.Equal(Count("video"), doc.Tracks.OfType<VideoTrack>().Count());
        Assert.Equal(Count("audio"), doc.Tracks.OfType<AudioTrack>().Count());
        Assert.All(doc.Tracks.Where(t => t is not ChapterTrack), t => Assert.False(string.IsNullOrEmpty(t.Format)));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Edits_a_copy_without_touching_media(string file)
    {
        Corpus.Require(file);
        if (new FileInfo(file).Length > MaxEditSize)
            Assert.Skip("Too large for an edit round trip.");

        var ct = TestContext.Current.CancellationToken;
        var copy = Fixtures.CopyToTemp(file);
        try
        {
            var before = Mp4Fixtures.PacketHashes(copy);
            var doc = await _handler.ReadAsync(copy, ct);
            doc.Metadata.Set(TagId.Name, "Corpus ✓");
            doc.Metadata.Set(TagId.Cast, new[] { "A", "B" });
            doc.Chapters.Clear();
            doc.Chapters.Add(new Chapter(TimeSpan.Zero, "Start"));
            if (doc.Duration > TimeSpan.FromSeconds(2))
                doc.Chapters.Add(new Chapter(TimeSpan.FromSeconds(1), "Later"));
            foreach (var t in doc.Tracks.Where(t => t is not ChapterTrack))
                t.Name = $"Track {t.Id}";
            await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

            var reread = await _handler.ReadAsync(copy, ct);
            Assert.Equal("Corpus ✓", reread.Metadata.GetString(TagId.Name));
            Assert.Equal(doc.Chapters.Count, reread.Chapters.Count);
            Assert.Equal(before, Mp4Fixtures.PacketHashes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }
}
