using System.Net;
using MMW.Core.Diagnostics;
using MMW.Metadata.Providers.TheMovieDb;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Providers;

public sealed class TmdbProviderTests
{
    private static (TmdbProvider Provider, FakeHttpHandler Handler) Create(ProviderSettings? settings = null)
    {
        var handler = new FakeHttpHandler()
            .OnJson("tmdb/search_movie.json", "/3/search/movie")
            .OnJson("tmdb/movie_603.json", "/3/movie/603")
            .OnJson("tmdb/search_tv.json", "/3/search/tv")
            .OnJson("tmdb/tv_1396_s1e2.json", "/3/tv/1396/season/1/episode/2")
            .OnJson("tmdb/tv_1396_season_1.json", "/3/tv/1396/season/1")
            .OnJson("tmdb/tv_1396.json", "/3/tv/1396");
        return (new TmdbProvider(new HttpClient(handler), settings ?? TestSettings.WithKeys()), handler);
    }

    [Fact]
    public async Task SearchMovie_ParsesResultsAndSkipsEntriesWithoutId()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchMovieAsync("The Matrix", 1999, "en", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var matrix = results[0];
        Assert.Equal("603", matrix.ProviderId);
        Assert.Equal("The Matrix", matrix.DisplayTitle);
        Assert.Equal("1999", matrix.Subtitle);
        Assert.Equal("1999-03-30", matrix.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal(MediaSearchKind.Movie, matrix.Kind);
        Assert.Equal(9, matrix.MediaKind);
        var poster = Assert.Single(matrix.Artworks);
        Assert.Equal("https://image.tmdb.org/t/p/w342/f89U3ADr1oiB1s9GkdPOEpXUk5H.jpg", poster.ThumbnailUrl.ToString());
        Assert.Equal("https://image.tmdb.org/t/p/original/f89U3ADr1oiB1s9GkdPOEpXUk5H.jpg", poster.FullUrl.ToString());
        Assert.Null(results[1].Year);

        var query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        Assert.Contains("query=The Matrix", query, StringComparison.Ordinal);
        Assert.Contains("year=1999", query, StringComparison.Ordinal);
        Assert.Contains("api_key=tmdb-test-key", query, StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task SearchMovie_RetriesWithoutYearWhenStrictYearFindsNothing()
    {
        var handler = new FakeHttpHandler()
            .On(r => r.RequestUri!.Query.Contains("year=", StringComparison.Ordinal), _ => FakeHttpHandler.Json("{\"page\":1,\"results\":[]}"))
            .OnJson("tmdb/search_movie.json", "/3/search/movie");
        var provider = new TmdbProvider(new HttpClient(handler), TestSettings.WithKeys());

        var results = await provider.SearchMovieAsync("The Matrix", 1998, "en", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task V4ReadToken_IsSentAsBearerHeader()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIwMTIzNDU2Nzg5YWJjZGVmIiwic3ViIjoiNjAwMCJ9.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWduYXR1cmU";
        var (provider, handler) = Create(TestSettings.WithKeys(tmdb: token));

        await provider.SearchMovieAsync("The Matrix", null, "en", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(token, request.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("api_key", request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDetails_Movie_FillsCreditsRatingAndArtwork()
    {
        var (provider, handler) = Create();
        var result = (await provider.SearchMovieAsync("The Matrix", null, "en", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(result, "en", TestContext.Current.CancellationToken);

        Assert.True(result.IsDetailed);
        Assert.Equal(["Keanu Reeves", "Laurence Fishburne", "Carrie-Anne Moss", "Hugo Weaving"], result.GetList(MetadataTokens.Cast));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], result.GetList(MetadataTokens.Director));
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], result.GetList(MetadataTokens.Screenwriters));
        Assert.Equal(["Joel Silver"], result.GetList(MetadataTokens.Producers));
        Assert.Equal("Barrie M. Osborne", result.GetString(MetadataTokens.ExecutiveProducer));
        Assert.Equal("Don Davis", result.GetString(MetadataTokens.Composer));
        Assert.Equal("Action, Science Fiction", result.GetString(MetadataTokens.Genre));
        Assert.Equal("Village Roadshow Pictures, Groucho II Film Partnership, Silver Pictures", result.GetString(MetadataTokens.Studio));
        Assert.Equal("mpaa|R|400|", result.GetString(MetadataTokens.Rating));
        Assert.Equal("tt0133093", result.GetString(MetadataTokens.ImdbId));
        Assert.Equal(2, result.Artworks.Count(a => a.Kind == ArtworkKind.Poster));
        Assert.Single(result.Artworks, a => a.Kind == ArtworkKind.Backdrop);
        Assert.Equal(3000, result.Artworks[0].Height);

        var detailsQuery = Uri.UnescapeDataString(handler.Requests[^1].RequestUri!.Query);
        Assert.Contains("append_to_response=credits,release_dates,images", detailsQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDetails_UsesRatingCountryFromSettings()
    {
        var settings = TestSettings.WithKeys();
        settings.RatingCountry = "DE";
        var (provider, _) = Create(settings);
        var result = (await provider.SearchMovieAsync("The Matrix", null, "de", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(result, "de", TestContext.Current.CancellationToken);

        Assert.Equal("de-movie|Ab 16 Jahren|500|", result.GetString(MetadataTokens.Rating));
    }

    [Fact]
    public async Task SearchTv_WithSeasonAndEpisode_ReturnsOneEpisodeOfExactSeries()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchTvAsync("breaking bad", 1, 2, "en", TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.Equal("S1E02 - Cat's in the Bag...", r.DisplayTitle);
        Assert.Equal("Breaking Bad (2008)", r.Subtitle);
        Assert.Equal("Breaking Bad", r.GetString(MetadataTokens.SeriesName));
        Assert.Equal(1, r.GetInt(MetadataTokens.Season));
        Assert.Equal(2, r.GetInt(MetadataTokens.EpisodeNumber));
        Assert.Equal("2/3", r.GetString(MetadataTokens.TrackNumber));
        Assert.Equal("102", r.GetString(MetadataTokens.EpisodeId));
        Assert.Equal("AMC", r.GetString(MetadataTokens.Network));
        Assert.Equal("Drama, Crime", r.GetString(MetadataTokens.Genre));
        Assert.Equal("us-tv|TV-MA|600|", r.GetString(MetadataTokens.Rating));
        Assert.Equal("1396", r.GetString(MetadataTokens.ServiceSeriesId));
        Assert.Equal("62086", r.GetString(MetadataTokens.ServiceEpisodeId));
        Assert.Equal(10, r.MediaKind);
        Assert.Contains(r.Artworks, a => a.Kind == ArtworkKind.Season && a.Season == 1);
        Assert.Equal(0, handler.Count("/3/tv/155537"));
    }

    [Fact]
    public async Task SearchTv_WithoutSeason_ListsAllRegularSeasonsAndToleratesMissingOnes()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchTvAsync("Breaking Bad", null, null, "en", TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count); // season 2 has no fixture (404) and is skipped; specials are never listed
        Assert.Equal("103", results[2].GetString(MetadataTokens.EpisodeId)); // production code wins
        Assert.Equal(0, handler.Count("/season/0"));
        Assert.Equal(1, handler.Count("/season/2"));
    }

    [Fact]
    public async Task LoadDetails_Episode_MergesSeriesCastGuestStarsAndCrew()
    {
        var (provider, _) = Create();
        var result = (await provider.SearchTvAsync("Breaking Bad", 1, 2, "en", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(result, "en", TestContext.Current.CancellationToken);

        Assert.Equal(["Bryan Cranston", "Aaron Paul", "Max Arciniega"], result.GetList(MetadataTokens.Cast));
        Assert.Equal(["Adam Bernstein"], result.GetList(MetadataTokens.Director));
        Assert.Equal(["Vince Gilligan"], result.GetList(MetadataTokens.Screenwriters));
        Assert.Equal("Mark Johnson", result.GetString(MetadataTokens.ExecutiveProducer));
        Assert.Equal("Walter White, a New Mexico chemistry teacher, is diagnosed with Stage III cancer.", result.GetString(MetadataTokens.SeriesDescription));
        Assert.Equal(ArtworkKind.Episode, result.Artworks[0].Kind);
        Assert.Equal("https://image.tmdb.org/t/p/original/still_hi.jpg", result.Artworks[0].FullUrl.ToString());
        Assert.Contains(result.Artworks, a => a.Kind == ArtworkKind.Season);
        Assert.Contains(result.Artworks, a => a.Kind == ArtworkKind.Poster);
        Assert.Contains(result.Artworks, a => a.Kind == ArtworkKind.Backdrop);
    }

    [Fact]
    public async Task ServerError_IsLoggedAndReturnsEmpty()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpStatusCode.InternalServerError, "/3/search/movie");
        var provider = new TmdbProvider(new HttpClient(handler), TestSettings.WithKeys());

        var results = await provider.SearchMovieAsync("Matrix 500 test", null, "en", TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Contains(AppLog.Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains("TheMovieDB", StringComparison.Ordinal) && e.Message.Contains("500", StringComparison.Ordinal));
        Assert.DoesNotContain(AppLog.Snapshot(), e => e.Message.Contains("tmdb-test-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var (provider, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchMovieAsync("The Matrix", null, "en", cts.Token));
    }

    [Theory]
    [InlineData("", "en")]
    [InlineData("en", "en")]
    [InlineData("eng", "en")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("pt-BR", "pt-BR")]
    public void ApiLanguage_NormalisesCodes(string input, string expected) =>
        Assert.Equal(expected, TmdbProvider.ApiLanguage(input));
}
