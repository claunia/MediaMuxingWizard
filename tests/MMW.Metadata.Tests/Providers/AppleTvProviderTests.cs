using MMW.Core.Diagnostics;
using MMW.Metadata.Providers.AppleTv;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Providers;

public sealed class AppleTvProviderTests
{
    private static (AppleTvProvider Provider, FakeHttpHandler Handler) Create()
    {
        var handler = new FakeHttpHandler()
            .OnJson("appletv/search.json", "/uts/v2/search/incremental")
            .OnJson("appletv/product_movie.json", "/uts/v2/view/product/umc.cmc.af8k9kcq9r1s1qmmdxpq4itn")
            .OnJson("appletv/show_episodes.json", "/uts/v2/view/show/umc.cmc.1v90fu25sgywa1e14jwnrt9uc/episodes")
            .OnJson("appletv/product_episode.json", "/uts/v2/view/product/umc.cmc.ep2")
            .OnJson("appletv/product_show.json", "/uts/v2/view/product/umc.cmc.1v90fu25sgywa1e14jwnrt9uc");
        return (new AppleTvProvider(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task SearchMovie_ReturnsMoviesOnly()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchMovieAsync("The Matrix", 1999, "United States", TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.Equal("The Matrix", r.DisplayTitle);
        Assert.Equal("1999-03-31", r.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal("mpaa|R|400|", r.GetString(MetadataTokens.Rating));
        Assert.Equal(143441, r.GetInt(MetadataTokens.ITunesCountry));
        var poster = r.Artworks.Single(a => a.Kind == ArtworkKind.Poster);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Video115/v4/a2/pr_source.lsr/2000x3000.jpg", poster.FullUrl.ToString());
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Video115/v4/a2/pr_source.lsr/342x513.jpg", poster.ThumbnailUrl.ToString());
        Assert.Single(r.Artworks, a => a.Kind == ArtworkKind.Backdrop);

        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains("sf=143441", query, StringComparison.Ordinal);
        Assert.Contains("locale=en-US", query, StringComparison.Ordinal);
        Assert.Contains("caller=wta", query, StringComparison.Ordinal);
        Assert.Contains("pfm=appletv", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDetails_Movie_FillsRolesGenreAndStudio()
    {
        var (provider, _) = Create();
        var movie = (await provider.SearchMovieAsync("The Matrix", null, "US", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(movie, "US", TestContext.Current.CancellationToken);

        Assert.True(movie.IsDetailed);
        Assert.Equal(["Keanu Reeves", "Laurence Fishburne"], movie.GetList(MetadataTokens.Cast));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], movie.GetList(MetadataTokens.Director));
        Assert.Equal(["Joel Silver"], movie.GetList(MetadataTokens.Producers));
        Assert.Equal(["Lana Wachowski"], movie.GetList(MetadataTokens.Screenwriters));
        Assert.Equal("Sci-Fi", movie.GetString(MetadataTokens.Genre));
        Assert.Equal("Warner Home Video", movie.GetString(MetadataTokens.Studio));
    }

    [Fact]
    public async Task SearchTv_FiltersSeasonAndEpisode()
    {
        var (provider, _) = Create();

        var results = await provider.SearchTvAsync("Breaking Bad", 1, 2, "US", TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.Equal("S1E02 - Cat's in the Bag...", r.DisplayTitle);
        Assert.Equal("Breaking Bad", r.GetString(MetadataTokens.SeriesName));
        Assert.Equal("2/2", r.GetString(MetadataTokens.TrackNumber));
        Assert.Equal("2008-01-27", r.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal("us-tv|TV-MA|600|", r.GetString(MetadataTokens.Rating)); // inherited from the show
        Assert.Equal("umc.cmc.1v90fu25sgywa1e14jwnrt9uc", r.ProviderParentId);
    }

    [Fact]
    public async Task SearchTv_WholeShow_ListsEveryEpisodeWithArtwork()
    {
        var (provider, _) = Create();

        var results = await provider.SearchTvAsync("breaking bad", null, null, "US", TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        var pilot = results[0];
        Assert.Contains(pilot.Artworks, a => a.Kind == ArtworkKind.Episode);
        Assert.Contains(pilot.Artworks, a => a.Kind == ArtworkKind.Season && a.Season == 1);
        Assert.Contains(pilot.Artworks, a => a.Kind == ArtworkKind.Square);
    }

    [Fact]
    public async Task LoadDetails_Episode_CombinesShowAndEpisodeRoles()
    {
        var (provider, _) = Create();
        var ep = (await provider.SearchTvAsync("Breaking Bad", 1, 2, "US", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(ep, "US", TestContext.Current.CancellationToken);

        Assert.Equal(["Bryan Cranston", "Aaron Paul", "Max Arciniega"], ep.GetList(MetadataTokens.Cast));
        Assert.Equal(["Adam Bernstein"], ep.GetList(MetadataTokens.Director));
        Assert.Equal("Vince Gilligan", ep.GetString(MetadataTokens.ExecutiveProducer));
        Assert.Equal("AMC", ep.GetString(MetadataTokens.Network));
        Assert.Equal("Breaking Bad follows protagonist Walter White.", ep.GetString(MetadataTokens.SeriesDescription));
        Assert.Contains(ep.Artworks, a => a.FullUrl.ToString() == "https://is1-ssl.mzstatic.com/image/thumb/ep2/1920x1080.jpg");
    }

    [Fact]
    public async Task MalformedResponse_IsLoggedNotThrown()
    {
        var handler = new FakeHttpHandler().On(_ => true, _ => FakeHttpHandler.Json("{\"data\": {\"canvas\": [not json"));
        var provider = new AppleTvProvider(new HttpClient(handler));

        var results = await provider.SearchMovieAsync("Malformed Apple TV", null, "US", TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Contains(AppLog.Snapshot(), e => e.Level == LogLevel.Warning && e.Message.StartsWith("Apple TV:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://x/{w}x{h}.{f}", "https://x/600x900.jpg")]
    [InlineData("https://x/{w}x{h}{c}.{f}", "https://x/600x900.jpg")]
    [InlineData("https://x/{w}x{h}bb.{f}", "https://x/600x900bb.jpg")]
    public void Expand_FillsTemplate(string template, string expected) =>
        Assert.Equal(expected, AppleTvProvider.Expand(template, 600, 900)?.ToString());
}
