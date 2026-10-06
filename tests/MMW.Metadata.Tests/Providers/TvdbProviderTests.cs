using System.Net;
using MMW.Metadata.Providers.TheTvDb;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Providers;

public sealed class TvdbProviderTests
{
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson("tvdb/login.json", "/v4/login")
        .OnJson("tvdb/search.json", "/v4/search")
        .OnJson("tvdb/series_extended.json", "/v4/series/81189/extended")
        .OnJson("tvdb/episodes_s1.json", "/v4/series/81189/episodes/default/")
        .OnJson("tvdb/episode_extended.json", "/v4/episodes/349235/extended")
        .OnJson("tvdb/artworks.json", "/v4/series/81189/artworks");

    private static TvdbProvider Create(FakeHttpHandler handler, ProviderSettings? settings = null) =>
        new(new HttpClient(handler), settings ?? TestSettings.WithKeys());

    [Fact]
    public async Task Login_UsesProjectKeyOnly_AndTokenIsReused()
    {
        var handler = Handler();
        var settings = TestSettings.WithKeys();
        var provider = Create(handler, settings);

        await provider.SearchSeriesNamesAsync("breaking", "eng", TestContext.Current.CancellationToken);
        await provider.SearchSeriesNamesAsync("breaking bad", "eng", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count("/v4/login"));
        var loginIndex = handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal));
        var body = handler.Bodies[loginIndex]!;
        Assert.Contains($"\"apikey\":\"{settings.ApiKeys.TheTvDb}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", body, StringComparison.Ordinal);
        var search = handler.Requests.Last();
        Assert.Equal("Bearer", search.Headers.Authorization?.Scheme);
        Assert.Equal("eyJhbGciOiJSUzI1NiJ9.fake-token-1.signature", search.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task OptionalPin_IsSentWhenConfigured()
    {
        var handler = Handler();
        var settings = TestSettings.WithKeys();
        settings.TvdbPin = "1234";

        await Create(handler, settings).SearchSeriesNamesAsync("breaking", "eng", TestContext.Current.CancellationToken);

        Assert.Contains("\"pin\":\"1234\"", handler.Bodies[0]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_RefreshesTokenOnceAndRetries()
    {
        var searches = 0;
        var logins = 0;
        var handler = new FakeHttpHandler()
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(Fixture.Read(++logins == 1 ? "tvdb/login.json" : "tvdb/login2.json")))
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal), r =>
            {
                searches++;
                return r.Headers.Authorization?.Parameter == "eyJhbGciOiJSUzI1NiJ9.fake-token-1.signature"
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }
                    : FakeHttpHandler.Json(Fixture.Read("tvdb/search.json"));
            });

        var names = await Create(handler).SearchSeriesNamesAsync("breaking", "eng", TestContext.Current.CancellationToken);

        Assert.Equal(2, logins);
        Assert.Equal(2, searches);
        Assert.Contains("Breaking Bad", names);
    }

    [Fact]
    public async Task SearchSeriesNames_UsesTranslations()
    {
        var names = await Create(Handler()).SearchSeriesNamesAsync("breaking", "deu", TestContext.Current.CancellationToken);

        Assert.Equal(["Breaking Bad – Die Serie", "Breaking Bad Habits"], names);
    }

    [Fact]
    public async Task SearchTv_SeasonAndEpisode_BuildsEpisodeResult()
    {
        var handler = Handler();

        var results = await Create(handler).SearchTvAsync("Breaking Bad", 1, 2, "en", TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.Equal("S1E02 - Cat's in the Bag...", r.DisplayTitle);
        Assert.Equal("Breaking Bad", r.GetString(MetadataTokens.SeriesName));
        Assert.Equal("AMC", r.GetString(MetadataTokens.Network));
        Assert.Equal("Crime, Drama", r.GetString(MetadataTokens.Genre));
        Assert.Equal("Sony Pictures Television, High Bridge Productions", r.GetString(MetadataTokens.Studio));
        Assert.Equal("us-tv|TV-MA|600|", r.GetString(MetadataTokens.Rating));
        Assert.Equal(["Bryan Cranston", "Aaron Paul"], r.GetList(MetadataTokens.Cast));
        Assert.Equal("2008-01-27", r.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal("81189", r.GetString(MetadataTokens.ServiceSeriesId));
        Assert.Equal("349235", r.GetString(MetadataTokens.ServiceEpisodeId));
        Assert.Equal("102", r.GetString(MetadataTokens.EpisodeId));
        var still = Assert.Single(r.Artworks, a => a.Kind == ArtworkKind.Episode);
        Assert.Equal("https://artworks.thetvdb.com/banners/episodes/81189/349235.jpg", still.FullUrl.ToString());
        var season = Assert.Single(r.Artworks, a => a.Kind == ArtworkKind.Season);
        Assert.Equal("https://artworks.thetvdb.com/banners/seasons/81189-1.jpg", season.FullUrl.ToString());

        var episodesQuery = handler.Requests.Single(x => x.RequestUri!.AbsolutePath.Contains("/episodes/default/", StringComparison.Ordinal)).RequestUri!;
        Assert.EndsWith("/episodes/default/eng", episodesQuery.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("season=1", episodesQuery.Query, StringComparison.Ordinal);
        Assert.Contains("episodeNumber=2", episodesQuery.Query, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count("/v4/series/999999"));
    }

    [Fact]
    public async Task SearchTv_WholeSeason_SetsTrackTotals()
    {
        var results = await Create(Handler()).SearchTvAsync("Breaking Bad", 1, null, "eng", TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal(["1/3", "2/3", "3/3"], results.Select(r => r.GetString(MetadataTokens.TrackNumber)));
        Assert.Equal("https://artworks.thetvdb.com/banners/episodes/81189/349235.jpg", results[1].Artworks.First(a => a.Kind == ArtworkKind.Episode).FullUrl.ToString());
    }

    [Fact]
    public async Task EpisodesInMissingLanguage_FallBackToEnglish()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpStatusCode.NotFound, "/episodes/default/fra")
            .OnJson("tvdb/login.json", "/v4/login")
            .OnJson("tvdb/search.json", "/v4/search")
            .OnJson("tvdb/series_extended.json", "/v4/series/81189/extended")
            .OnJson("tvdb/episodes_s1.json", "/episodes/default/eng");

        var results = await Create(handler).SearchTvAsync("Breaking Bad", 1, 1, "fr", TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(1, handler.Count("/episodes/default/fra"));
        Assert.Equal(1, handler.Count("/episodes/default/eng"));
    }

    [Fact]
    public async Task LoadDetails_FillsCrewTranslationAndArtwork()
    {
        var provider = Create(Handler());
        var result = (await provider.SearchTvAsync("Breaking Bad", 1, 2, "deu", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(result, "deu", TestContext.Current.CancellationToken);

        Assert.True(result.IsDetailed);
        Assert.Equal("Die Katze ist im Sack...", result.GetString(MetadataTokens.Name));
        Assert.Equal("Breaking Bad – Die Serie", result.GetString(MetadataTokens.SeriesName));
        Assert.Equal("Walt und Jesse...", result.GetString(MetadataTokens.Description));
        Assert.Equal("BB-102", result.GetString(MetadataTokens.EpisodeId));
        Assert.Equal(["Bryan Cranston", "Aaron Paul", "Max Arciniega"], result.GetList(MetadataTokens.Cast));
        Assert.Equal(["Adam Bernstein"], result.GetList(MetadataTokens.Director));
        Assert.Equal(["Vince Gilligan"], result.GetList(MetadataTokens.Screenwriters));
        Assert.Equal("Mark Johnson", result.GetString(MetadataTokens.ExecutiveProducer));

        Assert.Equal(ArtworkKind.Episode, result.Artworks[0].Kind);
        Assert.Equal(ArtworkKind.Season, result.Artworks[1].Kind);
        var posters = result.Artworks.Where(a => a.Kind == ArtworkKind.Poster).ToList();
        Assert.Equal("deu", posters[0].Language); // requested language first
        Assert.Equal("https://artworks.thetvdb.com/banners/posters/81189-2_t.jpg", posters[0].ThumbnailUrl.ToString());
        Assert.Single(result.Artworks, a => a.Kind == ArtworkKind.Backdrop);
        Assert.Single(result.Artworks, a => a.Kind == ArtworkKind.Rectangle);
        Assert.DoesNotContain(result.Artworks, a => a.FullUrl.ToString().Contains("clearlogo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Movies_AreNotSupported()
    {
        var handler = Handler();
        var provider = Create(handler);

        Assert.False(provider.SupportsMovies);
        Assert.Empty(await provider.SearchMovieAsync("The Matrix", 1999, "eng", TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("", "eng")]
    [InlineData("en", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("de", "deu")]
    [InlineData("ger", "deu")]
    [InlineData("fr-FR", "fra")]
    [InlineData("pt-BR", "pt")]
    public void ApiLanguage_UsesIso639_2(string input, string expected) =>
        Assert.Equal(expected, TvdbProvider.ApiLanguage(input));

    [Theory]
    [InlineData(2, ArtworkKind.Poster)]
    [InlineData(7, ArtworkKind.Season)]
    [InlineData(11, ArtworkKind.Episode)]
    [InlineData(3, ArtworkKind.Backdrop)]
    [InlineData(1, ArtworkKind.Rectangle)]
    [InlineData(23, null)]
    public void ArtworkTypes_MapToKinds(int type, ArtworkKind? expected) =>
        Assert.Equal(expected, TvdbProvider.KindOf(type));
}
