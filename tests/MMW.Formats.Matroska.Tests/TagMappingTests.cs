using MMW.Core.Metadata;
using MMW.Formats.Matroska.Ebml;

namespace MMW.Formats.Matroska.Tests;

public sealed class TagMappingTests
{
    /// <summary>Encodes the set as Matroska tags, parses them back and maps them to a new set.</summary>
    private static MetadataSet RoundTrip(MetadataSet source, out List<MatroskaTag> tags)
    {
        tags = MatroskaTagMapping.Write(source, []);
        var w = new EbmlWriter();
        foreach (var tag in tags)
            tag.Write(w);

        var parsed = EbmlParser.Children(w.ToArray()).Select(c => MatroskaTag.Parse(c.Data)).ToList();
        var result = new MetadataSet();
        MatroskaTagMapping.Read(parsed, result, []);
        return result;
    }

    private static void AssertSame(MetadataSet expected, MetadataSet actual)
    {
        Assert.Equal(expected.Keys.ToList(), actual.Keys.ToList());
        foreach (var key in expected.Keys)
        {
            var e = expected[key];
            var a = actual[key];
            if (e is IReadOnlyList<string> list)
                Assert.Equal(list, Assert.IsAssignableFrom<IReadOnlyList<string>>(a));
            else
                Assert.Equal(e, a);
        }

        Assert.Equal(expected.CustomItems.OrderBy(kv => kv.Key), actual.CustomItems.OrderBy(kv => kv.Key));
    }

    private static string? Find(List<MatroskaTag> tags, int level, string name) =>
        tags.Where(t => t.TargetTypeValue == level).SelectMany(t => t.SimpleTags).FirstOrDefault(s => s.Name == name)?.Value;

    [Fact]
    public void Movie_RoundTrips_WithStandardNames()
    {
        var m = new MetadataSet();
        m.Set(TagId.MediaKind, TagCatalog.MediaKindMovie);
        m.Set(TagId.Name, "The Movie");
        m.Set(TagId.SortName, "Movie, The");
        m.Set(TagId.Artist, "Director Person");
        m.Set(TagId.Genre, "Drama");
        m.Set(TagId.ReleaseDate, "2024-05-01");
        m.Set(TagId.Description, "Short");
        m.Set(TagId.LongDescription, "A much longer description.");
        m.Set(TagId.Cast, (string[])["Actor One", "Actor Two", "Actor Three"]);
        m.Set(TagId.Director, (string[])["Director Person"]);
        m.Set(TagId.Producers, (string[])["P1", "P2"]);
        m.Set(TagId.Screenwriters, (string[])["W1"]);
        m.Set(TagId.Studio, "Studio X");
        m.Set(TagId.Rating, "mpaa|PG-13|300|");
        m.Set(TagId.HdVideo, 2);
        m.Set(TagId.Gapless, true);
        m.Set(TagId.ContentRating, 4);
        m.Set(TagId.TrackNumber, new IntPair(1, 3));
        m.Set(TagId.DiskNumber, new IntPair(1, 2));
        m.Set(TagId.Album, "The Collection");
        m.Set(TagId.SortAlbum, "Collection, The");
        m.Set(TagId.Copyright, "© 2024");
        m.Set(TagId.EncodingTool, "MMW");
        m.Set(TagId.Tempo, 120);
        m.CustomItems["50/SOMETHING_ELSE"] = "kept";
        m.CustomItems["70/ANOTHER"] = "also kept";

        var back = RoundTrip(m, out var tags);
        AssertSame(m, back);

        Assert.Equal("The Movie", Find(tags, 50, "TITLE"));
        Assert.Equal("Movie", Find(tags, 50, "CONTENT_TYPE"));
        Assert.Equal("The Collection", Find(tags, 70, "TITLE"));
        Assert.Equal("3", Find(tags, 70, "TOTAL_PARTS"));
        Assert.Equal("1", Find(tags, 60, "PART_NUMBER"));
        Assert.Equal("2", Find(tags, 60, "TOTAL_PARTS"));
        Assert.Equal("mpaa|PG-13|300|", Find(tags, 50, "LAW_RATING"));
        Assert.Equal(3, tags.Single(t => t.TargetTypeValue == 50).SimpleTags.Count(s => s.Name == "ACTOR"));
        Assert.Equal("2", Find(tags, 50, "MMW_HdVideo"));
        Assert.Equal("Movie, The", tags.Single(t => t.TargetTypeValue == 50).SimpleTags.Single(s => s.Name == "TITLE").Children.Single(c => c.Name == "SORT_WITH").Value);
    }

    [Fact]
    public void TvEpisode_UsesCollectionAndSeasonTargets()
    {
        var m = new MetadataSet();
        m.Set(TagId.MediaKind, TagCatalog.MediaKindTvShow);
        m.Set(TagId.Name, "Pilot");
        m.Set(TagId.TvShow, "Some Show");
        m.Set(TagId.SortTvShow, "Show, Some");
        m.Set(TagId.TvSeason, 2);
        m.Set(TagId.TvEpisodeNumber, 5);
        m.Set(TagId.TvEpisodeId, "S02E05");
        m.Set(TagId.TvNetwork, "Network");
        m.Set(TagId.SeriesDescription, "About the show");
        m.Set(TagId.TrackNumber, new IntPair(5, 10));
        m.Set(TagId.Album, "Not the show");

        var back = RoundTrip(m, out var tags);
        AssertSame(m, back);

        Assert.Equal("Some Show", Find(tags, 70, "TITLE"));
        Assert.Equal("Network", Find(tags, 70, "PUBLISHER"));
        Assert.Equal("About the show", Find(tags, 70, "SUMMARY"));
        Assert.Equal("2", Find(tags, 60, "PART_NUMBER"));
        Assert.Equal("5", Find(tags, 50, "PART_NUMBER"));
        Assert.Equal("TV Show", Find(tags, 50, "CONTENT_TYPE"));
    }

    [Fact]
    public void TvWithoutMediaKind_IsDetectedFromSeason()
    {
        var m = new MetadataSet();
        m.Set(TagId.TvShow, "Show");
        m.Set(TagId.TvSeason, 1);
        m.Set(TagId.TvEpisodeNumber, 3);
        AssertSame(m, RoundTrip(m, out _));
    }

    [Fact]
    public void TvFieldsWithoutSeasonOrKind_StillRoundTrip()
    {
        var m = new MetadataSet();
        m.Set(TagId.TvShow, "Show");
        m.Set(TagId.Album, "Album");
        m.Set(TagId.DiskNumber, new IntPair(2, 0));
        m.Set(TagId.TvEpisodeNumber, 3);
        AssertSame(m, RoundTrip(m, out _));
    }

    [Fact]
    public void AllCatalogTags_RoundTrip()
    {
        var m = new MetadataSet();
        foreach (var def in TagCatalog.All)
        {
            object value = def.Kind switch
            {
                TagValueKind.StringList => (string[])[def.Name + " A", def.Name + " B"],
                TagValueKind.Bool => true,
                TagValueKind.Integer => 7,
                TagValueKind.Enum => def.Choices![^1].Value,
                TagValueKind.IntegerPair => new IntPair(3, 9),
                TagValueKind.Date => "2020-01-02",
                _ => def.Name + " value",
            };
            m.Set(def.Id, value);
        }

        // TV Show kind, so TV fields use their standard places and Album/TrackNumber fall back to MMW_ names.
        m.Set(TagId.MediaKind, TagCatalog.MediaKindTvShow);
        AssertSame(m, RoundTrip(m, out _));

        m.Set(TagId.MediaKind, TagCatalog.MediaKindMovie);
        AssertSame(m, RoundTrip(m, out _));
    }

    [Fact]
    public void UnknownTags_BecomeCustomItems_WithLanguageAndNesting()
    {
        var tag = new MatroskaTag { TargetTypeValue = 50 };
        var parent = new MatroskaSimpleTag("ORIGINAL", "x");
        parent.Children.Add(new MatroskaSimpleTag("TITLE", "Original title"));
        tag.SimpleTags.Add(parent);
        tag.SimpleTags.Add(new MatroskaSimpleTag("TITLE", "English"));
        tag.SimpleTags.Add(new MatroskaSimpleTag("TITLE", "Français") { LanguageBcp47 = "fr" });
        tag.SimpleTags.Add(new MatroskaSimpleTag("MOOD", "a"));
        tag.SimpleTags.Add(new MatroskaSimpleTag("MOOD", "b"));

        var m = new MetadataSet();
        MatroskaTagMapping.Read([tag], m, []);

        Assert.Equal("English", m.GetString(TagId.Name));
        Assert.Equal("x", m.CustomItems["50/ORIGINAL"]);
        Assert.Equal("Original title", m.CustomItems["50/ORIGINAL/TITLE"]);
        Assert.Equal("Français", m.CustomItems["50/TITLE@fr"]);
        Assert.Equal("a", m.CustomItems["50/MOOD"]);
        Assert.Equal("b", m.CustomItems["50/MOOD#2"]);

        AssertSame(m, RoundTrip(m, out var tags));
        var item = tags.Single(t => t.TargetTypeValue == 50);
        Assert.Equal("fr", item.SimpleTags.Single(s => s.Value == "Français").LanguageBcp47);
        Assert.Equal("Original title", item.SimpleTags.Single(s => s.Name == "ORIGINAL").Children.Single().Value);
    }

    [Fact]
    public void InvalidNumbers_AreKeptAsCustomItems()
    {
        var tag = new MatroskaTag { TargetTypeValue = 50 };
        tag.SimpleTags.Add(new MatroskaSimpleTag("PART_NUMBER", "one"));
        tag.SimpleTags.Add(new MatroskaSimpleTag("CONTENT_TYPE", "Documentary"));
        var m = new MetadataSet();
        MatroskaTagMapping.Read([tag], m, []);
        Assert.False(m.Contains(TagId.TrackNumber));
        Assert.False(m.Contains(TagId.MediaKind));
        Assert.Equal("one", m.CustomItems["50/PART_NUMBER"]);
        Assert.Equal("Documentary", m.CustomItems["50/CONTENT_TYPE"]);
    }
}
