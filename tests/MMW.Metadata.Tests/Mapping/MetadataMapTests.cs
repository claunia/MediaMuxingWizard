using MMW.Core.Metadata;
using MMW.Metadata.Mapping;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Mapping;

public sealed class MetadataMapTests
{
    private static MetadataResult Episode()
    {
        var r = new MetadataResult("Test", MediaSearchKind.TvEpisode);
        r.Set(MetadataTokens.Name, "Cat's in the Bag...");
        r.Set(MetadataTokens.SeriesName, "Breaking Bad");
        r.Set(MetadataTokens.Season, 1);
        r.Set(MetadataTokens.EpisodeNumber, 2);
        r.Set(MetadataTokens.EpisodeId, "102");
        r.Set(MetadataTokens.Network, "AMC");
        r.Set(MetadataTokens.Genre, "Drama");
        r.Set(MetadataTokens.ReleaseDate, "2008-01-27");
        r.Set(MetadataTokens.Cast, new[] { "Bryan Cranston", "Aaron Paul" });
        r.Set(MetadataTokens.Director, new[] { "Adam Bernstein" });
        r.Set(MetadataTokens.Rating, "us-tv|TV-MA|600|");
        r.Set(MetadataTokens.ContentId, 271383871);
        r.Set(MetadataTokens.ITunesCountry, 143441);
        return r;
    }

    [Fact]
    public void DefaultTvMap_MatchesSublerLayout()
    {
        var set = MetadataMap.DefaultTv().Apply(Episode());

        Assert.Equal("Cat's in the Bag...", set.GetString(TagId.Name));
        Assert.Equal("Breaking Bad", set.GetString(TagId.Artist));
        Assert.Equal("Breaking Bad", set.GetString(TagId.AlbumArtist));
        Assert.Equal("Breaking Bad, Season 1", set.GetString(TagId.Album));
        Assert.Equal("Breaking Bad", set.GetString(TagId.TvShow));
        Assert.Equal(1, set.GetInt(TagId.TvSeason));
        Assert.Equal(2, set.GetInt(TagId.TvEpisodeNumber));
        Assert.Equal("102", set.GetString(TagId.TvEpisodeId));
        Assert.Equal("AMC", set.GetString(TagId.TvNetwork));
        Assert.Equal(new IntPair(2, 0), set.GetPair(TagId.TrackNumber));
        Assert.Equal(new IntPair(1, 0), set.GetPair(TagId.DiskNumber));
        Assert.Equal(["Bryan Cranston", "Aaron Paul"], set.GetList(TagId.Cast));
        Assert.Equal(["Adam Bernstein"], set.GetList(TagId.Director));
        Assert.Equal("us-tv|TV-MA|600|", set.GetString(TagId.Rating));
        Assert.Equal(271383871, set.GetInt(TagId.ContentId));
        Assert.Equal(143441, set.GetInt(TagId.ITunesCountry));
        Assert.Equal(TagCatalog.MediaKindTvShow, set.GetInt(TagId.MediaKind));
        Assert.False(set.Contains(TagId.Studio)); // missing token: entry skipped
    }

    [Fact]
    public void DefaultMovieMap_JoinsListsIntoStringTags()
    {
        var r = new MetadataResult("Test", MediaSearchKind.Movie);
        r.Set(MetadataTokens.Name, "The Matrix");
        r.Set(MetadataTokens.Director, new[] { "Lana Wachowski", "Lilly Wachowski" });
        r.Set(MetadataTokens.ReleaseDate, "1999-03-30");

        var set = MetadataMap.DefaultMovie().Apply(r);

        Assert.Equal("Lana Wachowski, Lilly Wachowski", set.GetString(TagId.Artist));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], set.GetList(TagId.Director));
        Assert.Equal("1999-03-30", set.GetString(TagId.ReleaseDate));
        Assert.Equal(TagCatalog.MediaKindMovie, set.GetInt(TagId.MediaKind));
        Assert.False(set.Contains(TagId.TvShow));
    }

    [Fact]
    public void CompositeTemplate_IsSkippedWhenAnyTokenIsMissing()
    {
        var r = Episode();
        r.Set(MetadataTokens.Season, null);

        var set = MetadataMap.DefaultTv().Apply(r);

        Assert.False(set.Contains(TagId.Album));
        Assert.Equal("Breaking Bad", set.GetString(TagId.TvShow));
    }

    [Fact]
    public void CustomTemplates_AndConstants()
    {
        var map = new MetadataMap
        {
            Kind = MediaSearchKind.TvEpisode,
            Entries =
            [
                new(TagId.Name, "{Series Name} S{Season}E{Episode #} - {Name}"),
                new(TagId.Comments, "Tagged by MMW"),
                new(TagId.Keywords, "{Cast}"),
                new(TagId.MediaKind, "9"),
                new(TagId.TvSeason, "{Name}"), // not a number: logged and skipped
            ],
        };

        var set = map.Apply(Episode());

        Assert.Equal("Breaking Bad S1E2 - Cat's in the Bag...", set.GetString(TagId.Name));
        Assert.Equal("Tagged by MMW", set.GetString(TagId.Comments));
        Assert.Equal("Bryan Cranston, Aaron Paul", set.GetString(TagId.Keywords));
        Assert.Equal(9, set.GetInt(TagId.MediaKind)); // explicit row wins over automatic media kind
        Assert.False(set.Contains(TagId.TvSeason));
    }

    [Fact]
    public void Json_RoundTripsWithTagNames()
    {
        var json = MetadataMap.DefaultTv().ToJson();
        var back = MetadataMap.FromJson(json);

        Assert.Contains("\"TvShow\"", json, StringComparison.Ordinal);
        Assert.Contains("{Series Name}, Season {Season}", json, StringComparison.Ordinal);
        Assert.Equal(MediaSearchKind.TvEpisode, back.Kind);
        Assert.Equal(MetadataMap.DefaultTv().Entries, back.Entries);
    }

    [Fact]
    public void Maps_SaveLoadAndRestoreDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("mmw-maps-");
        try
        {
            var path = Path.Combine(dir.FullName, "maps.json");
            var maps = new MetadataMaps();
            maps.Movie.Entries.RemoveAll(e => e.Tag == TagId.Artist);
            maps.Save(path);

            var loaded = MetadataMaps.Load(path);
            Assert.DoesNotContain(loaded.Movie.Entries, e => e.Tag == TagId.Artist);
            Assert.Equal(MetadataMap.DefaultTv().Entries, loaded.Tv.Entries);

            loaded.RestoreDefault(MediaSearchKind.Movie);
            Assert.Contains(loaded.For(MediaSearchKind.Movie).Entries, e => e.Tag == TagId.Artist);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(MetadataMap.DefaultMovie().Entries, MetadataMaps.Load(path).Movie.Entries);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void TokensOf_ListsPlaceholders() =>
        Assert.Equal(["Series Name", "Season"], MetadataMap.TokensOf("{Series Name}, Season {Season}"));
}
