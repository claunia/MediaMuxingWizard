using System.Diagnostics;
using MMW.Metadata.Http;
using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Providers;

public sealed class ITunesStoreProviderTests
{
    private static (ITunesStoreProvider Provider, FakeHttpHandler Handler) Create()
    {
        var handler = new FakeHttpHandler()
            .OnJson("itunes/search_movie.json", "/search", "entity=movie")
            .OnJson("itunes/search_season.json", "/search", "entity=tvSeason")
            .OnJson("itunes/lookup_season1.json", "/lookup", "id=271383858")
            .OnJson("itunes/lookup_movie.json", "/lookup", "id=271469518");
        return (new ITunesStoreProvider(new HttpClient(handler), new ProviderSettings { FileApiKeys = ApiKeys.Empty }, rateLimiter: RequestRateLimiter.Unlimited), handler);
    }

    [Fact]
    public async Task SearchMovie_MapsStoreFields()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchMovieAsync("The Matrix", 1999, "United States", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var r = results[0];
        Assert.Equal("The Matrix", r.DisplayTitle);
        Assert.Equal(["Lana Wachowski", "Lilly Wachowski"], r.GetList(MetadataTokens.Director));
        Assert.Equal("Action & Adventure", r.GetString(MetadataTokens.Genre));
        Assert.Equal("1999-03-31", r.GetString(MetadataTokens.ReleaseDate));
        Assert.Equal("mpaa|R|400|", r.GetString(MetadataTokens.Rating));
        Assert.Equal(271469518, r.GetInt(MetadataTokens.ContentId));
        Assert.Equal(1452425437, r.GetInt(MetadataTokens.PlaylistId));
        Assert.Equal(143441, r.GetInt(MetadataTokens.ITunesCountry));
        Assert.Equal("https://itunes.apple.com/us/movie/the-matrix/id271469518?uo=4", r.GetString(MetadataTokens.ITunesUrl));
        Assert.Equal("Thomas Anderson is a computer programmer by day and hacker by night.\nNeo & Trinity fight the machines.", r.GetString(MetadataTokens.LongDescription));
        var art = Assert.Single(r.Artworks);
        Assert.EndsWith("/600x600bb.jpg", art.ThumbnailUrl.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("/3000x3000bb.jpg", art.FullUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal("mpaa|NR|000|", results[1].GetString(MetadataTokens.Rating));

        var query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        Assert.Contains("country=US", query, StringComparison.Ordinal);
        Assert.Contains("media=movie", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchTv_PrefersPlainSeasonAndStripsEpisodePrefix()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchTvAsync("Breaking Bad", 1, 2, "US", TestContext.Current.CancellationToken);

        var r = Assert.Single(results);
        Assert.Equal("S1E02 - Cat's in the Bag...", r.DisplayTitle);
        Assert.Equal("Breaking Bad", r.GetString(MetadataTokens.SeriesName));
        Assert.Equal("2/3", r.GetString(MetadataTokens.TrackNumber));
        Assert.Equal("1/1", r.GetString(MetadataTokens.DiskNumber));
        Assert.Equal("us-tv|TV-14|500|", r.GetString(MetadataTokens.Rating));
        Assert.Equal(271382034, r.GetInt(MetadataTokens.ArtistId));
        Assert.Equal(271383858, r.GetInt(MetadataTokens.PlaylistId));
        Assert.Equal(271383871, r.GetInt(MetadataTokens.ContentId));
        Assert.Equal("© 2008 Sony Pictures Television Inc. All Rights Reserved.", r.GetString(MetadataTokens.Copyright));
        Assert.Equal("Walter White, a high school chemistry teacher, learns he has terminal cancer.", r.GetString(MetadataTokens.SeriesDescription));
        Assert.Equal(ArtworkKind.Square, Assert.Single(r.Artworks).Kind);

        Assert.Contains("term=Breaking Bad season 1", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query), StringComparison.Ordinal);
        Assert.Equal(0, handler.Count("/lookup", "id=594959484")); // deluxe box set ignored
        Assert.Equal(0, handler.Count("/lookup", "id=777"));        // other series ignored
    }

    [Fact]
    public async Task SearchTv_EpisodeWithoutSearchPrefix_KeepsTitle()
    {
        var (provider, _) = Create();

        var results = await provider.SearchTvAsync("Breaking Bad", 1, null, "US", TestContext.Current.CancellationToken);

        Assert.Equal(["Pilot", "Cat's in the Bag...", "...And the Bag's in the River"], results.Select(r => r.GetString(MetadataTokens.Name)));
        Assert.Equal("us-tv|TV-MA|600|", results[2].GetString(MetadataTokens.Rating)); // falls back to the season rating
    }

    [Fact]
    public async Task Storefront_SelectsCountryAndStoreId()
    {
        var (provider, handler) = Create();

        var results = await provider.SearchMovieAsync("Matrix", null, "Germany", TestContext.Current.CancellationToken);

        Assert.Contains("country=DE", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(143443, results[0].GetInt(MetadataTokens.ITunesCountry));
    }

    [Fact]
    public async Task SeriesNames_AreDistinctArtistNames()
    {
        var (provider, _) = Create();

        var names = await provider.SearchSeriesNamesAsync("breaking", "US", TestContext.Current.CancellationToken);

        Assert.Equal(["Breaking Bad", "Breaking Bad Habits"], names);
    }

    [Fact]
    public async Task LoadDetails_AddsCopyright()
    {
        var (provider, _) = Create();
        var movie = (await provider.SearchMovieAsync("The Matrix", null, "US", TestContext.Current.CancellationToken))[0];

        await provider.LoadDetailsAsync(movie, "US", TestContext.Current.CancellationToken);

        Assert.True(movie.IsDetailed);
        Assert.Equal("© 1999 Warner Bros. Entertainment Inc.", movie.GetString(MetadataTokens.Copyright));
    }

    [Theory]
    [InlineData("https://a.mzstatic.com/image/thumb/x/y.jpg/100x100bb.jpg", 600, "https://a.mzstatic.com/image/thumb/x/y.jpg/600x600bb.jpg")]
    [InlineData("https://a.mzstatic.com/image/thumb/x/60x60bb.png", 3000, "https://a.mzstatic.com/image/thumb/x/3000x3000bb.png")]
    [InlineData("https://a1.mzstatic.com/us/r30/Video/abc.100x100-75.jpg", 600, "https://a1.mzstatic.com/us/r30/Video/abc.100x100-75.jpg")]
    public void UpsizeArtwork_ReplacesLastSizeComponent(string input, int size, string expected) =>
        Assert.Equal(expected, ITunesStoreProvider.UpsizeArtwork(input, size)?.ToString());

    [Theory]
    [InlineData("Breaking Bad, Season 5", 5)]
    [InlineData("Breaking Bad, Deluxe Edition: Seasons 1 & 2", 1)]
    [InlineData("Dark, Staffel 2", 2)]
    [InlineData("Sherlock", null)]
    public void SeasonNumber_IsParsedFromCollectionName(string name, int? expected) =>
        Assert.Equal(expected, ITunesStoreProvider.SeasonNumber(name));

    [Fact]
    public void Storefronts_TableIsConsistent()
    {
        Assert.True(Storefronts.All.Count > 100);
        Assert.Equal(Storefronts.All.Count, Storefronts.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(Storefronts.All.Count, Storefronts.All.Select(s => s.Iso).Distinct().Count());
        Assert.Equal(143441, Storefronts.Find("US")!.Id);
        Assert.Equal(143444, Storefronts.Find("uk")!.Id);
        Assert.Equal("GB", Storefronts.Find("United Kingdom")!.Iso);
        Assert.Equal("Japan", Storefronts.Find("143462")!.Name);
        Assert.Equal("ja-JP", Storefronts.Find("JP")!.Locale);
        Assert.Null(Storefronts.Find("Atlantis"));
        Assert.Equal("US", Storefronts.FindOrDefault(null).Iso);
    }

    [Fact]
    public async Task RateLimiter_DelaysRequestsBeyondTheWindowBudget()
    {
        using var limiter = new RequestRateLimiter(3, TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            await limiter.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds < 300, $"first three calls should not wait ({sw.ElapsedMilliseconds} ms)");

        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds >= 350, $"fourth call should wait for the window ({sw.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void SharedITunesLimiter_AllowsTwentyPerMinute()
    {
        Assert.Equal(20, RequestRateLimiter.ITunes.Permits);
        Assert.Equal(TimeSpan.FromMinutes(1), RequestRateLimiter.ITunes.Window);
    }
}
