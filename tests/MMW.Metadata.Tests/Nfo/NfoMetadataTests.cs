using MMW.Core.Metadata;
using MMW.Metadata.Nfo;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Nfo;

public sealed class NfoMetadataTests
{
    [Fact]
    public void ParsesKodiMovie()
    {
        var result = NfoMetadata.ParseResult(Fixture.Read("nfo/movie.nfo"));

        Assert.Equal(MediaSearchKind.Movie, result.Kind);
        Assert.Equal("The Matrix", result.GetString(MetadataTokens.Name));
        Assert.Equal("1999-03-30", result.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal("A hacker learns the truth about reality.", result.GetString(MetadataTokens.Description));
        Assert.Equal("Set in the 22nd century, The Matrix tells the story of a computer hacker.", result.GetString(MetadataTokens.LongDescription));
        Assert.Equal("Action, Science Fiction", result.GetString(MetadataTokens.Genre));
        Assert.Equal("Village Roadshow Pictures, Silver Pictures", result.GetString(MetadataTokens.Studio));
        Assert.Equal("mpaa|R|400|", result.GetString(MetadataTokens.Rating));
        Assert.Equal(["Keanu Reeves", "Laurence Fishburne", "Carrie-Anne Moss"], result.GetList(MetadataTokens.Cast));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], result.GetList(MetadataTokens.Director));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], result.GetList(MetadataTokens.Screenwriters));
        Assert.Equal("tt0133093", result.GetString(MetadataTokens.ImdbId));
        Assert.Equal(2, result.Artworks.Count);
        Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", result.Artworks[0].ThumbnailUrl.ToString());
        Assert.Equal(ArtworkKind.Backdrop, result.Artworks[1].Kind);

        var set = NfoMetadata.Parse(Fixture.Read("nfo/movie.nfo"));
        Assert.Equal("The Matrix", set.GetString(TagId.Name));
        Assert.Equal(TagCatalog.MediaKindMovie, set.GetInt(TagId.MediaKind));
    }

    [Fact]
    public void ParsesEpisodeAndMergesTvShowNfo()
    {
        var root = Directory.CreateTempSubdirectory("mmw-nfo-");
        try
        {
            var show = Path.Combine(root.FullName, "Breaking Bad");
            var season = Path.Combine(show, "Season 1");
            Directory.CreateDirectory(season);
            File.Copy(Fixture.Path("nfo/tvshow.nfo"), Path.Combine(show, "tvshow.nfo"));
            File.Copy(Fixture.Path("nfo/episode.nfo"), Path.Combine(season, "Breaking.Bad.S01E02.nfo"));
            var media = Path.Combine(season, "Breaking.Bad.S01E02.mkv");

            Assert.Equal(2, NfoMetadata.FindNfoFiles(media).Count);
            var result = NfoMetadata.LoadResultForMedia(media)!;

            Assert.Equal(MediaSearchKind.TvEpisode, result.Kind);
            Assert.Equal("Cat's in the Bag...", result.GetString(MetadataTokens.Name));
            Assert.Equal("Breaking Bad", result.GetString(MetadataTokens.SeriesName));
            Assert.Equal(1, result.GetInt(MetadataTokens.Season));
            Assert.Equal(2, result.GetInt(MetadataTokens.EpisodeNumber));
            Assert.Equal("2008-01-27", result.GetString(MetadataTokens.ReleaseDate));
            Assert.Equal("us-tv|TV-MA|600|", result.GetString(MetadataTokens.Rating));
            Assert.Equal("349235", result.GetString(MetadataTokens.ServiceEpisodeId));
            Assert.Equal("A high school chemistry teacher turned methamphetamine producer.", result.GetString(MetadataTokens.SeriesDescription));
            Assert.Equal("AMC", result.GetString(MetadataTokens.Network));
            Assert.Equal("Drama, Crime", result.GetString(MetadataTokens.Genre));
            Assert.Contains(result.Artworks, a => a.Kind == ArtworkKind.Episode);
            Assert.Contains(result.Artworks, a => a.Kind == ArtworkKind.Season);

            var set = NfoMetadata.ImportForMedia(media)!;
            Assert.Equal("Breaking Bad, Season 1", set.GetString(TagId.Album));
            Assert.Equal(TagCatalog.MediaKindTvShow, set.GetInt(TagId.MediaKind));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void ImportForMedia_WithoutNfo_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("mmw-nfo-");
        try
        {
            Assert.Null(NfoMetadata.ImportForMedia(Path.Combine(dir.FullName, "movie.mkv")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void MovieRoundTrip_PreservesTags()
    {
        var set = new MetadataSet();
        set.Set(TagId.Name, "The Matrix");
        set.Set(TagId.SortName, "Matrix");
        set.Set(TagId.ReleaseDate, "1999-03-30");
        set.Set(TagId.Description, "Short.");
        set.Set(TagId.LongDescription, "Long description.");
        set.Set(TagId.Genre, "Action, Science Fiction");
        set.Set(TagId.Studio, "Silver Pictures");
        set.Set(TagId.Rating, "mpaa|R|400|");
        set.Set(TagId.Cast, new[] { "Keanu Reeves", "Laurence Fishburne" });
        set.Set(TagId.Director, new[] { "Lana Wachowski", "Lilly Wachowski" });
        set.Set(TagId.Screenwriters, new[] { "Lana Wachowski" });
        set.Set(TagId.MediaKind, 9);

        var xml = NfoMetadata.Export(set);
        Assert.Contains("<movie>", xml, StringComparison.Ordinal);
        Assert.Contains("<mpaa>Rated R</mpaa>", xml, StringComparison.Ordinal);
        Assert.Contains("<year>1999</year>", xml, StringComparison.Ordinal);

        var back = NfoMetadata.Parse(xml);
        foreach (var tag in new[] { TagId.Name, TagId.ReleaseDate, TagId.Description, TagId.LongDescription, TagId.Genre, TagId.Studio, TagId.Rating, TagId.MediaKind })
            Assert.Equal(set.GetString(tag), back.GetString(tag));
        Assert.Equal(set.GetList(TagId.Cast), back.GetList(TagId.Cast));
        Assert.Equal(set.GetList(TagId.Director), back.GetList(TagId.Director));
        Assert.Equal(set.GetList(TagId.Screenwriters), back.GetList(TagId.Screenwriters));
    }

    [Fact]
    public void EpisodeRoundTrip_PreservesTvTags()
    {
        var set = new MetadataSet();
        set.Set(TagId.MediaKind, 10);
        set.Set(TagId.Name, "Pilot");
        set.Set(TagId.TvShow, "Breaking Bad");
        set.Set(TagId.TvSeason, 1);
        set.Set(TagId.TvEpisodeNumber, 1);
        set.Set(TagId.TvNetwork, "AMC");
        set.Set(TagId.ReleaseDate, "2008-01-20");
        set.Set(TagId.Rating, "us-tv|TV-MA|600|");

        var xml = NfoMetadata.Export(set);
        Assert.Contains("<episodedetails>", xml, StringComparison.Ordinal);
        Assert.Contains("<aired>2008-01-20</aired>", xml, StringComparison.Ordinal);

        var back = NfoMetadata.Parse(xml);
        Assert.Equal("Pilot", back.GetString(TagId.Name));
        Assert.Equal("Breaking Bad", back.GetString(TagId.TvShow));
        Assert.Equal(1, back.GetInt(TagId.TvSeason));
        Assert.Equal(1, back.GetInt(TagId.TvEpisodeNumber));
        Assert.Equal("2008-01-20", back.GetString(TagId.ReleaseDate));
        Assert.Equal("us-tv|TV-MA|600|", back.GetString(TagId.Rating));
        Assert.Equal(10, back.GetInt(TagId.MediaKind));
    }

    [Fact]
    public void Write_CreatesFileNextToMedia()
    {
        var dir = Directory.CreateTempSubdirectory("mmw-nfo-");
        try
        {
            var media = Path.Combine(dir.FullName, "The Matrix (1999).mkv");
            var set = new MetadataSet();
            set.Set(TagId.Name, "The Matrix");
            NfoMetadata.Write(NfoMetadata.NfoPathFor(media), set);

            Assert.True(File.Exists(Path.Combine(dir.FullName, "The Matrix (1999).nfo")));
            Assert.Equal("The Matrix", NfoMetadata.ImportForMedia(media)!.GetString(TagId.Name));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<musicvideo><title>x</title></musicvideo>")]
    [InlineData("<movie><title>broken</movie>")]
    public void InvalidNfo_ThrowsFormatException(string text) =>
        Assert.Throws<FormatException>(() => NfoMetadata.ParseResult(text));
}
