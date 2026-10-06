using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Metadata.Mapping;

namespace MMW.Metadata.Tests.Mapping;

public sealed class MetadataApplierTests
{
    [Theory]
    [InlineData(720, 480, false, 0)]
    [InlineData(1024, 576, false, 0)]
    [InlineData(960, 720, false, 1)]
    [InlineData(1280, 720, false, 1)]
    [InlineData(1280, 536, false, 1)]
    [InlineData(1280, 721, false, 2)]
    [InlineData(1440, 1080, false, 2)]
    [InlineData(1920, 800, false, 2)]
    [InlineData(1920, 1080, false, 2)]
    [InlineData(1920, 1088, false, 2)]
    [InlineData(1920, 1088, true, 2)]
    [InlineData(1920, 1090, false, 2)]
    [InlineData(1920, 1090, true, 3)]
    [InlineData(3840, 2160, false, 2)]
    [InlineData(3840, 2160, true, 3)]
    [InlineData(3840, 1600, true, 3)]
    public void HdVideoKind_FollowsSublerRules(int width, int height, bool autodetect4K, int expected) =>
        Assert.Equal(expected, MetadataApplier.HdVideoKind(width, height, autodetect4K));

    private static MediaDocument Document(int width, int height)
    {
        var doc = new MediaDocument("/tmp/movie.mp4", ContainerKind.Mp4);
        doc.Tracks.Add(new VideoTrack { PixelWidth = width, PixelHeight = height });
        doc.Metadata.Set(TagId.Name, "Old name");
        doc.Metadata.Set(TagId.Comments, "keep me");
        doc.Metadata.Set(TagId.Studio, "Old studio");
        return doc;
    }

    private static MetadataSet Incoming()
    {
        var set = new MetadataSet();
        set.Set(TagId.Name, "The Matrix");
        set.Set(TagId.MediaKind, 9);
        return set;
    }

    [Fact]
    public void Apply_SetsHdVideoFromMainVideoTrack()
    {
        var doc = Document(3840, 2160);
        MetadataApplier.Apply(doc, Incoming(), new ApplyOptions { Autodetect4K = true });
        Assert.Equal(3, doc.Metadata.GetInt(TagId.HdVideo));

        doc = Document(3840, 2160);
        MetadataApplier.Apply(doc, Incoming(), new ApplyOptions { Autodetect4K = false });
        Assert.Equal(2, doc.Metadata.GetInt(TagId.HdVideo));

        doc = Document(640, 480);
        MetadataApplier.Apply(doc, Incoming());
        Assert.False(doc.Metadata.Contains(TagId.HdVideo));
    }

    [Fact]
    public void Apply_OverwriteReplacesAndKeepsUnrelatedTags()
    {
        var doc = Document(1920, 1080);

        MetadataApplier.Apply(doc, Incoming(), new ApplyOptions { Overwrite = true });

        Assert.Equal("The Matrix", doc.Metadata.GetString(TagId.Name));
        Assert.Equal("keep me", doc.Metadata.GetString(TagId.Comments));
        Assert.Equal("Old studio", doc.Metadata.GetString(TagId.Studio));
        Assert.Equal(9, doc.Metadata.GetInt(TagId.MediaKind));
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Apply_WithoutOverwriteOnlyFillsMissingTags()
    {
        var doc = Document(1920, 1080);

        MetadataApplier.Apply(doc, Incoming(), new ApplyOptions { Overwrite = false });

        Assert.Equal("Old name", doc.Metadata.GetString(TagId.Name));
        Assert.Equal(9, doc.Metadata.GetInt(TagId.MediaKind));
        Assert.Equal(2, doc.Metadata.GetInt(TagId.HdVideo));
    }

    [Fact]
    public void Apply_KeepEmptyFalseClearsMappedTagsMissingFromResult()
    {
        var doc = Document(1920, 1080);
        var options = new ApplyOptions { KeepEmpty = false, MappedTags = [.. MetadataMap.DefaultMovie().MappedTags] };

        MetadataApplier.Apply(doc, Incoming(), options);

        Assert.False(doc.Metadata.Contains(TagId.Studio));            // mapped but empty in result → cleared
        Assert.Equal("keep me", doc.Metadata.GetString(TagId.Comments)); // not mapped → untouched
    }

    [Fact]
    public void Apply_KeepEmptyTrueKeepsMappedTags()
    {
        var doc = Document(1920, 1080);

        MetadataApplier.Apply(doc, Incoming(), new ApplyOptions { KeepEmpty = true, MappedTags = [.. MetadataMap.DefaultMovie().MappedTags] });

        Assert.Equal("Old studio", doc.Metadata.GetString(TagId.Studio));
    }

    [Fact]
    public void Apply_ArtworkAppendsOrReplaces()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 };
        var doc = Document(1280, 720);
        doc.Metadata.Artworks.Add(new MMW.Core.Metadata.Artwork(jpeg));
        var incoming = Incoming();
        incoming.Artworks.Add(new MMW.Core.Metadata.Artwork(jpeg));

        MetadataApplier.Apply(doc, incoming);
        Assert.Equal(2, doc.Metadata.Artworks.Count);

        MetadataApplier.Apply(doc, incoming, new ApplyOptions { ReplaceArtworks = true });
        Assert.Single(doc.Metadata.Artworks);
    }

    [Fact]
    public void Apply_DoesNotMutateTheIncomingSet()
    {
        var incoming = Incoming();
        MetadataApplier.Apply(Document(3840, 2160), incoming, new ApplyOptions { Autodetect4K = true });
        Assert.False(incoming.Contains(TagId.HdVideo));
    }
}
