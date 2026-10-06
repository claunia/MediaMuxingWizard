using MMW.Metadata.Caching;
using MMW.Metadata.Http;
using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Infrastructure;

public sealed class CacheTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void SearchCache_StoresExpiresAndClears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmw-cache-" + Guid.NewGuid().ToString("N"));
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var cache = new SearchCache(dir, time);
        try
        {
            Assert.Null(cache.TryGet("k", TimeSpan.FromHours(1)));
            cache.Set("k", "{\"a\":1}");
            cache.Set("other", "x");
            Assert.Equal("{\"a\":1}", cache.TryGet("k", TimeSpan.FromHours(1)));
            Assert.Equal(2, cache.Count);
            Assert.True(cache.SizeInBytes > 0);

            time.Now = time.Now.AddHours(2);
            Assert.Null(cache.TryGet("k", TimeSpan.FromHours(1)));
            Assert.Equal(1, cache.Count); // expired entry removed

            cache.Clear();
            Assert.Equal(0, cache.Count);
            Assert.Null(cache.TryGet("other", TimeSpan.FromDays(1)));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Providers_UseTheResponseCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmw-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new FakeHttpHandler().OnJson("itunes/search_movie.json", "/search");
            var settings = new ProviderSettings { FileApiKeys = ApiKeys.Empty, Cache = new SearchCache(dir) };
            var provider = new ITunesStoreProvider(new HttpClient(handler), settings, rateLimiter: RequestRateLimiter.Unlimited);

            var first = await provider.SearchMovieAsync("The Matrix", null, "US", TestContext.Current.CancellationToken);
            var second = await provider.SearchMovieAsync("The Matrix", null, "US", TestContext.Current.CancellationToken);

            Assert.Single(handler.Requests);
            Assert.Equal(first.Count, second.Count);
            Assert.Equal(first[0].DisplayTitle, second[0].DisplayTitle);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RecentSearches_DeduplicatesCapsAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmw-recent-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var recent = new RecentSearches(path, capacity: 3);
            recent.Add(new SearchQuery(MediaSearchKind.Movie, "The Matrix", 1999, null, null, "en"));
            recent.Add(new SearchQuery(MediaSearchKind.TvEpisode, "Lost", null, 1, 2, "eng"));
            recent.Add(new SearchQuery(MediaSearchKind.Movie, "the matrix", 1999, null, null, "en"));
            recent.Add(new SearchQuery(MediaSearchKind.Movie, "Alien", null, null, null, "en"));
            recent.Add(new SearchQuery(MediaSearchKind.Movie, "Heat", 1995, null, null, "en"));
            recent.Add(new SearchQuery(MediaSearchKind.Movie, "  ", null, null, null, "en"));

            Assert.Equal(["Heat", "Alien", "the matrix"], recent.Items.Select(q => q.Title));
            Assert.Equal("Heat (1995)", recent.Items[0].ToString());

            var reloaded = new RecentSearches(path, capacity: 3);
            Assert.Equal(recent.Items, reloaded.Items);
            Assert.Equal(["Heat", "Alien", "the matrix"], reloaded.Titles(MediaSearchKind.Movie));

            reloaded.Clear();
            Assert.Empty(new RecentSearches(path).Items);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SearchQuery_ToStringForEpisodes() =>
        Assert.Equal("Lost S01E02", new SearchQuery(MediaSearchKind.TvEpisode, "Lost", null, 1, 2, "").ToString());
}
