using System.Text.Json;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Matroska.Tests;

/// <summary>In-place editing of unusual layouts: unknown sizes, tight or missing SeekHead.</summary>
public sealed class MatroskaLayoutTests
{
    private static readonly MatroskaHandler s_handler = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task EditAsync(MediaDocument doc, bool editHeader)
    {
        if (editHeader)
        {
            doc.Metadata.Set(TagId.Name, "Synthetic");
            doc.Tracks[1].Name = "Audio";
        }

        doc.Metadata.Set(TagId.Genre, "Layout test");
        doc.Metadata.Set(TagId.Cast, (string[])["Somebody"]);
        doc.Chapters.Add(new Chapter(TimeSpan.Zero, "Only chapter"));
        doc.Metadata.Artworks.Add(new Artwork(await File.ReadAllBytesAsync(MkvFixtures.Cover(), Ct)));
        doc.Metadata.NotifyArtworksChanged();
    }

    private static async Task AssertEditedAsync(string path, bool editHeader)
    {
        var reread = await s_handler.ReadAsync(path, Ct);
        Assert.Equal(editHeader ? "Synthetic" : "Hello", reread.Metadata.GetString(TagId.Name));
        Assert.Equal(editHeader ? "Audio" : string.Empty, reread.Tracks[1].Name);
        Assert.Equal("Layout test", reread.Metadata.GetString(TagId.Genre));
        Assert.Equal(["Somebody"], reread.Metadata.GetList(TagId.Cast));
        Assert.Equal("Only chapter", Assert.Single(reread.Chapters).Title);
        Assert.Single(reread.Metadata.Artworks);

        var id = MkvFixtures.AssertValid(path);
        Assert.Single(id.GetProperty("attachments").EnumerateArray());
        Assert.Equal(1, id.GetProperty("chapters")[0].GetProperty("num_entries").GetInt32());

        // ffmpeg only finds elements after the Clusters through the SeekHead.
        var probe = Fixtures.Run("ffprobe", $"-v error -show_entries format_tags -show_chapters -of json {MkvFixtures.Q(path)}");
        using var json = JsonDocument.Parse(probe);
        var tags = json.RootElement.GetProperty("format").GetProperty("tags");
        Assert.Equal("Layout test", tags.EnumerateObject().First(p => p.Name.Equals("GENRE", StringComparison.OrdinalIgnoreCase)).Value.GetString());
        Assert.Single(json.RootElement.GetProperty("chapters").EnumerateArray());
    }

    [Theory]
    [InlineData(true, SeekHeadMode.Tight, false)]
    [InlineData(false, SeekHeadMode.Tight, false)]
    [InlineData(true, SeekHeadMode.Tight, true)]
    [InlineData(false, SeekHeadMode.Tight, true)]
    [InlineData(true, SeekHeadMode.NoneWithVoid, true)]
    [InlineData(false, SeekHeadMode.NoneWithVoid, false)]
    public async Task UnusualLayouts_AreEditedInPlace(bool unknownSizes, SeekHeadMode mode, bool editHeader)
    {
        MkvFixtures.RequireTools("ffmpeg", "ffprobe", "mkvmerge");
        var path = SyntheticFiles.Build(MkvFixtures.Basic(), unknownSizes, mode);
        try
        {
            var before = MkvFixtures.FrameMd5(path);
            Assert.Equal(MkvFixtures.FrameMd5(MkvFixtures.Basic()), before);
            MkvFixtures.AssertValid(path);

            var doc = await s_handler.ReadAsync(path, Ct);
            var layout = Assert.IsType<MatroskaLayout>(doc.ContainerState);
            Assert.Null(layout.ScanProblem);
            Assert.Equal(unknownSizes, layout.SegmentSizeUnknown);
            Assert.Equal(new FileInfo(path).Length, layout.SegmentEnd);
            Assert.Equal(2, doc.Tracks.Count);

            await EditAsync(doc, editHeader);
            await s_handler.SaveAsync(doc, new SaveOptions(), null, Ct);

            Assert.Equal(before, MkvFixtures.FrameMd5(path));
            await AssertEditedAsync(path, editHeader);

            layout = Assert.IsType<MatroskaLayout>(doc.ContainerState);
            if (mode == SeekHeadMode.Tight && !editHeader)
            {
                // The original SeekHead had no room (Info follows it unchanged): it now points to the full index
                // at the end of the file. When Info moves, the SeekHead grows into its old slot instead.
                var first = layout.SeekHeads.OrderBy(s => s.Position).First();
                var pointer = Assert.Single(first.Entries);
                Assert.Equal(MatroskaIds.SeekHead, pointer.Id);
                Assert.Contains(layout.SeekHeads, s => s.Entries.Any(e => e.Id == MatroskaIds.Tags));
            }
            else
            {
                Assert.Contains(layout.SeekHeads, s => s.Entries.Any(e => e.Id == MatroskaIds.Tags));
            }

            // A second round of edits on the new layout.
            doc.Metadata.Set(TagId.Genre, "Second pass");
            doc.Chapters.Add(new Chapter(TimeSpan.FromSeconds(1), "Two"));
            await s_handler.SaveAsync(doc, new SaveOptions(), null, Ct);
            MkvFixtures.AssertValid(path);
            Assert.Equal(before, MkvFixtures.FrameMd5(path));
            var reread = await s_handler.ReadAsync(path, Ct);
            Assert.Equal("Second pass", reread.Metadata.GetString(TagId.Genre));
            Assert.Equal(2, reread.Chapters.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NoSeekHeadAndNoRoom_IsRefusedWithoutWriting()
    {
        MkvFixtures.RequireTools("ffmpeg");
        var path = SyntheticFiles.Build(MkvFixtures.Basic(), false, SeekHeadMode.None);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, Ct);
            var doc = await s_handler.ReadAsync(path, Ct);
            doc.Metadata.Set(TagId.Genre, "Needs an index");
            await Assert.ThrowsAsync<NotSupportedException>(() => s_handler.SaveAsync(doc, new SaveOptions(), null, Ct));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TruncatedFile_ReadsButRefusesInPlaceSave()
    {
        var source = MkvFixtures.Full();
        var path = Fixtures.CopyToTemp(source);
        try
        {
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                fs.SetLength(fs.Length - 1000);

            var doc = await s_handler.ReadAsync(path, Ct);
            Assert.Equal(3, doc.Tracks.Count);
            Assert.NotNull(Assert.IsType<MatroskaLayout>(doc.ContainerState).ScanProblem);
            doc.Metadata.Set(TagId.Genre, "x");
            await Assert.ThrowsAsync<NotSupportedException>(() => s_handler.SaveAsync(doc, new SaveOptions(), null, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
