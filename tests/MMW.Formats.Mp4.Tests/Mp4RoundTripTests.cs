using System.Security.Cryptography;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Mp4.Tests;

public class Mp4RoundTripTests
{
    private readonly Mp4Handler _handler = new();


    [Fact]
    public async Task Reads_tracks_chapters_and_tags()
    {
        var doc = await _handler.ReadAsync(Mp4Fixtures.MoovAtEnd(), TestContext.Current.CancellationToken);

        Assert.Single(doc.Tracks.OfType<VideoTrack>());
        Assert.Equal(["en", "fr"], doc.Tracks.OfType<AudioTrack>().Select(a => a.Language));
        var sub = Assert.Single(doc.Tracks.OfType<SubtitleTrack>());
        Assert.Equal("es", sub.Language);
        Assert.Equal("Tx3g", sub.Format);
        Assert.Single(doc.Tracks.OfType<ChapterTrack>());

        var video = doc.Tracks.OfType<VideoTrack>().Single();
        Assert.Equal(320, video.PixelWidth);
        Assert.Equal("H.264", video.Format);
        Assert.InRange(video.FrameRate, 24.9, 25.1);
        Assert.Equal(["Opening", "Middle", "Ending"], doc.Chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Chapters[1].Start);
        Assert.Equal("Fixture", doc.Metadata.GetString(TagId.Name));
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tags_round_trip_without_touching_media(bool fastStart)
    {
        var path = Fixtures.CopyToTemp(fastStart ? Mp4Fixtures.FastStart() : Mp4Fixtures.MoovAtEnd());
        var before = Mp4Fixtures.PacketHashes(path);
        var ct = TestContext.Current.CancellationToken;

        var doc = await _handler.ReadAsync(path, ct);
        var m = doc.Metadata;
        m.Set(TagId.Name, "Pilot");
        m.Set(TagId.TvShow, "Some Show");
        m.Set(TagId.TvSeason, 2);
        m.Set(TagId.TvEpisodeNumber, 5);
        m.Set(TagId.TvEpisodeId, "S02E05");
        m.Set(TagId.TrackNumber, new IntPair(5, 10));
        m.Set(TagId.DiskNumber, new IntPair(1, 2));
        m.Set(TagId.MediaKind, 10);
        m.Set(TagId.HdVideo, 2);
        m.Set(TagId.Compilation, true);
        m.Set(TagId.Cast, new[] { "Ana", "Bob" });
        m.Set(TagId.Director, new[] { "Dee" });
        m.Set(TagId.Studio, "Studio X");
        m.Set(TagId.Rating, "us-tv|TV-14|500|");
        m.Set(TagId.RatingAnnotation, "Some violence");
        m.Set(TagId.LongDescription, "A long\nmulti-line description ✓");
        m.Set(TagId.PlaylistId, 123456789);
        m.Set(TagId.Abridged, true);
        m.Set(TagId.Genre, "Drama");
        m.CustomItems["----:org.example:MOOD"] = "Happy";
        var jpeg = Mp4Fixtures.Jpeg();
        m.Artworks.Add(new Artwork(jpeg));
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

        var reread = await _handler.ReadAsync(path, ct);
        var r = reread.Metadata;
        foreach (var id in m.Keys)
            Assert.Equal(MetadataSet.FormatValue(id, m[id]!), MetadataSet.FormatValue(id, r[id]!));
        Assert.Equal(m.Keys, r.Keys);
        Assert.Equal("Happy", r.CustomItems["----:org.example:MOOD"]);
        Assert.Equal(jpeg, Assert.Single(r.Artworks).Data);

        Assert.Equal(before, Mp4Fixtures.PacketHashes(path));
        Assert.Empty(Mp4Fixtures.DemuxErrors(path));
        var probe = Mp4Fixtures.Probe(path);
        Assert.Equal("Pilot", probe.GetProperty("format").GetProperty("tags").GetProperty("title").GetString());
        Assert.Equal("Some Show", probe.GetProperty("format").GetProperty("tags").GetProperty("show").GetString());
        Assert.Equal(3, probe.GetProperty("chapters").GetArrayLength());

        if (fastStart)
            Assert.StartsWith("ftyp,moov", Mp4Fixtures.BoxOrder(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_edit_after_rewrite_fits_in_padding()
    {
        var path = Fixtures.CopyToTemp(Mp4Fixtures.FastStart());
        var ct = TestContext.Current.CancellationToken;
        var doc = await _handler.ReadAsync(path, ct);
        doc.Metadata.Set(TagId.Description, new string('x', 300));
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);
        var size = new FileInfo(path).Length;

        doc.Metadata.Set(TagId.Description, new string('y', 900));
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(new string('y', 900), (await _handler.ReadAsync(path, ct)).Metadata.GetString(TagId.Description));
        Assert.Empty(Mp4Fixtures.DemuxErrors(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chapters_can_be_replaced_and_removed(bool fastStart)
    {
        var path = Fixtures.CopyToTemp(fastStart ? Mp4Fixtures.FastStart() : Mp4Fixtures.MoovAtEnd());
        var ct = TestContext.Current.CancellationToken;
        var doc = await _handler.ReadAsync(path, ct);
        doc.Chapters.Clear();
        doc.Chapters.Add(new Chapter(TimeSpan.Zero, "Intro"));
        doc.Chapters.Add(new Chapter(TimeSpan.FromMilliseconds(1500), "Ünïcødé chapter"));
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

        var reread = await _handler.ReadAsync(path, ct);
        Assert.Equal(["Intro", "Ünïcødé chapter"], reread.Chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), reread.Chapters[1].Start);
        var chapters = Mp4Fixtures.Probe(path).GetProperty("chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Equal("Ünïcødé chapter", chapters[1].GetProperty("tags").GetProperty("title").GetString());
        Assert.Empty(Mp4Fixtures.DemuxErrors(path));

        reread.Chapters.Clear();
        await _handler.SaveAsync(reread, new SaveOptions(), cancellationToken: ct);
        var empty = await _handler.ReadAsync(path, ct);
        Assert.Empty(empty.Chapters);
        Assert.Equal(0, Mp4Fixtures.Probe(path).GetProperty("chapters").GetArrayLength());
    }

    [Fact]
    public async Task Track_properties_round_trip()
    {
        var path = Fixtures.CopyToTemp(Mp4Fixtures.MoovAtEnd());
        var ct = TestContext.Current.CancellationToken;
        var doc = await _handler.ReadAsync(path, ct);
        var audio = doc.Tracks.OfType<AudioTrack>().ToList();
        audio[0].Name = "Stereo Audio";
        audio[0].Language = "de";
        audio[0].AlternateGroup = 1;
        audio[1].Enabled = false;
        audio[1].AlternateGroup = 1;
        audio[1].Language = "pt-BR";
        audio[1].MediaCharacteristics.Add("public.auxiliary-content");
        audio[1].Fallback = audio[0];
        var sub = doc.Tracks.OfType<SubtitleTrack>().Single();
        sub.ForcedMode = MMW.Core.Model.ForcedSubtitleMode.AllSamplesForced;
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

        var reread = await _handler.ReadAsync(path, ct);
        var a = reread.Tracks.OfType<AudioTrack>().ToList();
        Assert.Equal("Stereo Audio", a[0].Name);
        Assert.Equal("de", a[0].Language);
        Assert.True(a[0].Enabled);
        Assert.False(a[1].Enabled);
        Assert.Equal(1, a[1].AlternateGroup);
        Assert.Equal("pt-BR", a[1].Language);
        Assert.Equal(["public.auxiliary-content"], a[1].MediaCharacteristics);
        Assert.Same(a[0], a[1].Fallback);
        Assert.Equal(MMW.Core.Model.ForcedSubtitleMode.AllSamplesForced, reread.Tracks.OfType<SubtitleTrack>().Single().ForcedMode);

        var streams = Mp4Fixtures.Probe(path).GetProperty("streams");
        Assert.Equal("deu", streams[1].GetProperty("tags").GetProperty("language").GetString());
        Assert.Equal("por", streams[2].GetProperty("tags").GetProperty("language").GetString());
    }

    [Fact]
    public async Task Removing_a_track_keeps_the_rest_playable()
    {
        var path = Fixtures.CopyToTemp(Mp4Fixtures.FastStart());
        var ct = TestContext.Current.CancellationToken;
        var doc = await _handler.ReadAsync(path, ct);
        doc.Tracks.Remove(doc.Tracks.OfType<AudioTrack>().Last());
        await _handler.SaveAsync(doc, new SaveOptions(), cancellationToken: ct);

        // ffprobe also lists the chapter text track as a "data" stream; count only media streams.
        var streams = Mp4Fixtures.Probe(path).GetProperty("streams").EnumerateArray()
            .Count(s => s.GetProperty("codec_type").GetString() is "video" or "audio" or "subtitle");
        Assert.Equal(3, streams);
        Assert.Empty(Mp4Fixtures.DemuxErrors(path));
    }

    [Fact]
    public async Task Save_as_writes_a_new_file_and_leaves_the_source_alone()
    {
        var path = Fixtures.CopyToTemp(Mp4Fixtures.MoovAtEnd());
        var ct = TestContext.Current.CancellationToken;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var dest = Path.ChangeExtension(path, ".m4v");

        var doc = await _handler.ReadAsync(path, ct);
        doc.Metadata.Set(TagId.Name, "Copy");
        await _handler.SaveAsync(doc, new SaveOptions { OutputPath = dest, Optimize = true }, cancellationToken: ct);

        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(dest, doc.Path);
        Assert.StartsWith("ftyp,moov", Mp4Fixtures.BoxOrder(dest), StringComparison.Ordinal);
        Assert.Equal("M4V ", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(dest), 8, 4));
        Assert.Equal(Mp4Fixtures.PacketHashes(path), Mp4Fixtures.PacketHashes(dest));
        Assert.Equal("Copy", (await _handler.ReadAsync(dest, ct)).Metadata.GetString(TagId.Name));
    }

    [Fact]
    public async Task Forced_64bit_offsets_produce_a_valid_file()
    {
        var path = Fixtures.CopyToTemp(Mp4Fixtures.FastStart());
        var ct = TestContext.Current.CancellationToken;
        var before = Mp4Fixtures.PacketHashes(path);
        var doc = await _handler.ReadAsync(path, ct);
        await _handler.SaveAsync(doc, new SaveOptions { Optimize = true, Use64BitOffsets = true }, cancellationToken: ct);

        var moov = Boxes.Mp4Layout.Read(path).Moov.Loaded!;
        Assert.All(moov.FindAll("trak"), t => Assert.NotNull(t.FindPath("mdia/minf/stbl/co64")));
        Assert.Equal(before, Mp4Fixtures.PacketHashes(path));
    }
}
