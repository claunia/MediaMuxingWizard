using System.Security.Cryptography;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Matroska.Tests;

public sealed class MatroskaSaveTests
{
    private static readonly MatroskaHandler s_handler = new();
    private static readonly SaveOptions s_inPlace = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void FillTags(MetadataSet m)
    {
        m.Set(TagId.MediaKind, TagCatalog.MediaKindTvShow);
        m.Set(TagId.Name, "Episode Title");
        m.Set(TagId.TvShow, "My Show");
        m.Set(TagId.TvSeason, 3);
        m.Set(TagId.TvEpisodeNumber, 7);
        m.Set(TagId.TvEpisodeId, "S03E07");
        m.Set(TagId.TvNetwork, "Network");
        m.Set(TagId.Genre, "Drama");
        m.Set(TagId.ReleaseDate, "2021-03-04");
        m.Set(TagId.Description, "Short description");
        m.Set(TagId.LongDescription, "Long description");
        m.Set(TagId.SeriesDescription, "Series description");
        m.Set(TagId.Cast, (string[])["Alice", "Bob", "Carol"]);
        m.Set(TagId.Director, (string[])["Dan"]);
        m.Set(TagId.Screenwriters, (string[])["Eve", "Frank"]);
        m.Set(TagId.Rating, "us-tv|TV-14|500|");
        m.Set(TagId.HdVideo, 1);
        m.Set(TagId.SortName, "Episode Title, The");
        m.CustomItems["50/MY_CUSTOM"] = "custom value";
    }

    private static void AssertMetadataEqual(MetadataSet expected, MetadataSet actual)
    {
        Assert.Equal(expected.Keys.ToList(), actual.Keys.ToList());
        foreach (var key in expected.Keys)
        {
            if (expected[key] is IReadOnlyList<string> list)
                Assert.Equal(list, Assert.IsAssignableFrom<IReadOnlyList<string>>(actual[key]));
            else
                Assert.Equal(expected[key], actual[key]);
        }

        Assert.Equal(expected.CustomItems.OrderBy(kv => kv.Key), actual.CustomItems.OrderBy(kv => kv.Key));
    }

    [Fact]
    public async Task Tags_RoundTrip_InPlace()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Full());
        try
        {
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            FillTags(doc.Metadata);
            Assert.True(doc.IsDirty);
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            Assert.False(doc.IsDirty);

            var reread = await s_handler.ReadAsync(path, Ct);
            AssertMetadataEqual(doc.Metadata, reread.Metadata);
            Assert.Equal("keep me", reread.Metadata.CustomItems["50/X_UNKNOWN_THING"]);

            var id = MkvFixtures.AssertValid(path);
            Assert.Equal("Episode Title", id.GetProperty("container").GetProperty("properties").GetProperty("title").GetString());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));

            var xml = MkvFixtures.Extract(path, "tags");
            Assert.Contains("<String>Carol</String>", xml, StringComparison.Ordinal);
            Assert.Contains("<Name>SORT_WITH</Name>", xml, StringComparison.Ordinal);
            Assert.Contains("<Name>MMW_HdVideo</Name>", xml, StringComparison.Ordinal);

            // A second save with no edits does not touch the file.
            var hash = await HashAsync(path);
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            Assert.Equal(hash, await HashAsync(path));

            // Editing again after a save works on the refreshed layout.
            doc.Metadata.Set(TagId.Genre, "Comedy");
            doc.Metadata.Remove(TagId.Cast);
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            reread = await s_handler.ReadAsync(path, Ct);
            AssertMetadataEqual(doc.Metadata, reread.Metadata);
            MkvFixtures.AssertValid(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditThatFits_KeepsFileSize()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Full());
        try
        {
            var size = new FileInfo(path).Length;
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            doc.Tracks[1].Name = "Fr";
            doc.Metadata.Set(TagId.Artist, "S");
            doc.Metadata.Set(TagId.Name, "Short");
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);

            Assert.Equal(size, new FileInfo(path).Length);
            var id = MkvFixtures.AssertValid(path);
            Assert.Equal("Fr", MkvFixtures.Track(id, 2).GetProperty("properties").GetProperty("track_name").GetString());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal("S", reread.Metadata.GetString(TagId.Artist));
            Assert.Equal("Short", reread.Metadata.GetString(TagId.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditThatDoesNotFit_MovesElementAndKeepsMedia()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Basic());
        try
        {
            var size = new FileInfo(path).Length;
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);

            var big = new byte[200 * 1024];
            RandomNumberGenerator.Fill(big);
            big[0] = 0xFF;
            big[1] = 0xD8;
            big[2] = 0xFF;
            doc.Metadata.Artworks.Add(new Artwork(big));
            doc.Metadata.Set(TagId.Comments, new string('x', 5000));
            doc.Metadata.NotifyArtworksChanged();
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);

            Assert.True(new FileInfo(path).Length > size + big.Length);
            var id = MkvFixtures.AssertValid(path);
            var attachment = Assert.Single(id.GetProperty("attachments").EnumerateArray());
            Assert.Equal("cover.jpg", attachment.GetProperty("file_name").GetString());
            Assert.Equal(big.Length, attachment.GetProperty("size").GetInt32());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));

            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal(big, Assert.Single(reread.Metadata.Artworks).Data);
            Assert.Equal(5000, reread.Metadata.GetString(TagId.Comments)!.Length);
            Assert.Equal("Hello", reread.Metadata.GetString(TagId.Name));

            // Removing the artwork again voids the attachment; the file stays valid.
            doc.Metadata.Artworks.Clear();
            doc.Metadata.NotifyArtworksChanged();
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            id = MkvFixtures.AssertValid(path);
            Assert.Empty(id.GetProperty("attachments").EnumerateArray());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Artwork_Replacement_PreservesOtherAttachments()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Full());
        try
        {
            var doc = await s_handler.ReadAsync(path, Ct);
            var png = File.ReadAllBytes(MkvFixtures.Cover());
            doc.Metadata.Artworks.Add(new Artwork(png));
            doc.Metadata.NotifyArtworksChanged();
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);

            var id = MkvFixtures.AssertValid(path);
            var names = id.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("file_name").GetString()).ToList();
            Assert.Equal(["notes.txt", "cover.jpg", "cover_land.jpg"], names);

            var dir = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Fixtures.Run("mkvextract", $"{MkvFixtures.Q(path)} attachments 1:{MkvFixtures.Q(Path.Combine(dir, "notes.txt"))}");
                Assert.Equal("These are notes that must survive every save.\n", File.ReadAllText(Path.Combine(dir, "notes.txt")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Chapters_RoundTrip()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Basic());
        try
        {
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            Assert.Empty(doc.Chapters);
            doc.Chapters.Add(new Chapter(TimeSpan.Zero, "Start"));
            doc.Chapters.Add(new Chapter(TimeSpan.FromMilliseconds(750), "Middle ✓"));
            doc.Chapters.Add(new Chapter(TimeSpan.FromSeconds(1.5), "End"));
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);

            var id = MkvFixtures.AssertValid(path);
            Assert.Equal(3, id.GetProperty("chapters")[0].GetProperty("num_entries").GetInt32());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
            var xml = MkvFixtures.Extract(path, "chapters");
            Assert.Contains("Middle ✓", xml, StringComparison.Ordinal);

            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal(doc.Chapters.Select(c => (c.Start, c.Title)), reread.Chapters.Select(c => (c.Start, c.Title)));

            // Edit and then remove them.
            doc.Chapters.RemoveAt(1);
            doc.Chapters[0].Title = "Begin";
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal([(TimeSpan.Zero, "Begin"), (TimeSpan.FromSeconds(1.5), "End")], reread.Chapters.Select(c => (c.Start, c.Title)));

            doc.Chapters.Clear();
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            id = MkvFixtures.AssertValid(path);
            Assert.Empty(id.GetProperty("chapters").EnumerateArray());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TrackEdits_AreVisibleToMkvmerge()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Full());
        try
        {
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            var video = doc.Tracks[0];
            var audio = doc.Tracks[1];
            var subtitle = doc.Tracks[2];

            video.Name = string.Empty;
            audio.Name = "Deutsche Tonspur mit einem deutlich längeren Namen als vorher";
            audio.Language = "de-AT";
            audio.IsDefault = true;
            audio.MediaCharacteristics.Add(MediaCharacteristics.DescribesVideo);
            audio.MediaCharacteristics.Add(MediaCharacteristics.AuxiliaryContent);
            subtitle.IsForced = false;
            subtitle.MediaCharacteristics.Remove(MediaCharacteristics.TranscribesSpokenDialog);
            subtitle.MediaCharacteristics.Remove(MediaCharacteristics.DescribesMusicAndSound);
            subtitle.Enabled = false;
            subtitle.Language = "pt-BR";
            ((VideoTrack)video).Color = new ColorInfo(1, 1, 1, false);
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);

            var id = MkvFixtures.AssertValid(path);
            var v = MkvFixtures.Track(id, 1).GetProperty("properties");
            var a = MkvFixtures.Track(id, 2).GetProperty("properties");
            var s = MkvFixtures.Track(id, 3).GetProperty("properties");
            Assert.False(v.TryGetProperty("track_name", out _));
            Assert.Equal(audio.Name, a.GetProperty("track_name").GetString());
            Assert.Equal("de-AT", a.GetProperty("language_ietf").GetString());
            Assert.Equal("ger", a.GetProperty("language").GetString());
            Assert.True(a.GetProperty("default_track").GetBoolean());
            Assert.True(a.GetProperty("flag_visual_impaired").GetBoolean());
            Assert.True(a.GetProperty("flag_commentary").GetBoolean());
            Assert.False(s.GetProperty("forced_track").GetBoolean());
            Assert.False(s.GetProperty("enabled_track").GetBoolean());
            Assert.False(s.GetProperty("flag_hearing_impaired").GetBoolean());
            Assert.Equal("pt-BR", s.GetProperty("language_ietf").GetString());
            Assert.Equal("por", s.GetProperty("language").GetString());
            Assert.Equal(before, MkvFixtures.FrameMd5(path));

            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal(new ColorInfo(1, 1, 1, false), ((VideoTrack)reread.Tracks[0]).Color);
            Assert.Equal("de-AT", reread.Tracks[1].Language);
            Assert.Contains(MediaCharacteristics.DescribesVideo, reread.Tracks[1].MediaCharacteristics);
            Assert.False(reread.Tracks[2].IsForced);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAs_LeavesOriginalUnchanged()
    {
        var source = Fixtures.CopyToTemp(MkvFixtures.Full());
        var target = Path.Combine(Path.GetDirectoryName(source)!, Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var hash = await HashAsync(source);
            var doc = await s_handler.ReadAsync(source, Ct);
            doc.Metadata.Set(TagId.Name, "Saved elsewhere");
            doc.Tracks[0].Name = "Renamed";
            await s_handler.SaveAsync(doc, new SaveOptions { OutputPath = target }, null, Ct);

            Assert.Equal(hash, await HashAsync(source));
            Assert.Equal(target, doc.Path);
            var id = MkvFixtures.AssertValid(target);
            Assert.Equal("Saved elsewhere", id.GetProperty("container").GetProperty("properties").GetProperty("title").GetString());
            Assert.Equal(MkvFixtures.FrameMd5(source), MkvFixtures.FrameMd5(target));

            // The document now edits the new file.
            doc.Metadata.Set(TagId.Name, "Again");
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            Assert.Equal(hash, await HashAsync(source));
            Assert.Equal("Again", (await s_handler.ReadAsync(target, Ct)).Metadata.GetString(TagId.Name));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    [Fact]
    public async Task WebM_TagEdit_StaysValid()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.WebM());
        try
        {
            MkvFixtures.RequireTools("mkvmerge");
            var before = MkvFixtures.FrameMd5(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            FillTags(doc.Metadata);
            doc.Tracks[1].Language = "ja";
            await s_handler.SaveAsync(doc, s_inPlace, null, Ct);
            MkvFixtures.AssertValid(path);
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
            var reread = await s_handler.ReadAsync(path, Ct);
            AssertMetadataEqual(doc.Metadata, reread.Metadata);
            Assert.Equal("ja", reread.Tracks[1].Language);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Track list changes are remuxed (see <see cref="MatroskaRemuxTests"/>); a pending track that has no source file
    /// to read its samples from is still rejected, and the file is left untouched.
    /// </summary>
    [Fact]
    public async Task PendingTrackWithoutSource_IsRejected()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Basic());
        try
        {
            var hash = await HashAsync(path);
            var doc = await s_handler.ReadAsync(path, Ct);
            doc.Tracks.Add(new SubtitleTrack());
            await Assert.ThrowsAsync<InvalidOperationException>(() => s_handler.SaveAsync(doc, s_inPlace, null, Ct));
            Assert.Equal(hash, await HashAsync(path));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExternalModification_IsDetected()
    {
        var path = Fixtures.CopyToTemp(MkvFixtures.Basic());
        try
        {
            var doc = await s_handler.ReadAsync(path, Ct);
            await File.AppendAllTextAsync(path, "junk", Ct);
            doc.Metadata.Set(TagId.Name, "x");
            await Assert.ThrowsAsync<IOException>(() => s_handler.SaveAsync(doc, s_inPlace, null, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, Ct));
    }
}
