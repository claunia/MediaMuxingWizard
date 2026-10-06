using MMW.Metadata.Providers.AppleTv;
using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Providers.TheMovieDb;
using MMW.Metadata.Providers.TheTvDb;
using MMW.Metadata.Search;
using Xunit.Sdk;

namespace MMW.Metadata.Tests.Providers;

/// <summary>Real network tests. Opt-in: MMW_LIVE_TESTS=1 (TMDb/TVDB also need keys in appsettings.json or the environment).</summary>
public sealed class LiveProviderTests
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static ProviderSettings Settings() => new();

    [Fact]
    public async Task ITunes_FindsBreakingBadPilot()
    {
        Live.RequireEnabled();
        var provider = new ITunesStoreProvider(s_http, Settings());

        var results = await provider.SearchTvAsync("Breaking Bad", 1, 1, "United States", TestContext.Current.CancellationToken);

        var pilot = Assert.Single(results);
        Assert.Equal("Breaking Bad", pilot.GetString(MetadataTokens.SeriesName));
        Assert.Equal("Pilot", pilot.GetString(MetadataTokens.Name));
        Assert.Equal(1, pilot.GetInt(MetadataTokens.EpisodeNumber));
        Assert.Equal("us-tv|TV-MA|600|", pilot.GetString(MetadataTokens.Rating));
        Assert.NotNull(pilot.GetInt(MetadataTokens.ContentId));
        Assert.Contains("3000x3000bb", pilot.Artworks[0].FullUrl.ToString(), StringComparison.Ordinal);

        await provider.LoadDetailsAsync(pilot, "United States", TestContext.Current.CancellationToken);
        Assert.NotNull(pilot.GetString(MetadataTokens.Copyright));
    }

    [Fact]
    public async Task AppleTv_FindsTheMatrixAndBreakingBad()
    {
        Live.RequireEnabled();
        var provider = new AppleTvProvider(s_http, Settings());

        var movies = await provider.SearchMovieAsync("The Matrix", 1999, "US", TestContext.Current.CancellationToken);
        var matrix = movies.FirstOrDefault(m => m.DisplayTitle == "The Matrix") ?? throw new XunitException("The Matrix not found");
        await provider.LoadDetailsAsync(matrix, "US", TestContext.Current.CancellationToken);
        Assert.Contains("Keanu Reeves", matrix.GetList(MetadataTokens.Cast));
        Assert.Equal("mpaa|R|400|", matrix.GetString(MetadataTokens.Rating));

        var episodes = await provider.SearchTvAsync("Breaking Bad", 1, 1, "US", TestContext.Current.CancellationToken);
        var pilot = Assert.Single(episodes);
        Assert.Equal("Pilot", pilot.GetString(MetadataTokens.Name));
        await provider.LoadDetailsAsync(pilot, "US", TestContext.Current.CancellationToken);
        Assert.Equal("AMC", pilot.GetString(MetadataTokens.Network));
    }

    [Fact]
    public async Task TheMovieDb_FindsTheMatrix()
    {
        Live.RequireEnabled();
        var provider = new TmdbProvider(s_http, Settings());
        if (!provider.IsConfigured)
            Assert.Skip("No TMDb key configured.");

        var results = await provider.SearchMovieAsync("The Matrix", 1999, "en", TestContext.Current.CancellationToken);
        var matrix = results.First(r => r.DisplayTitle == "The Matrix");
        await provider.LoadDetailsAsync(matrix, "en", TestContext.Current.CancellationToken);

        Assert.Contains("Keanu Reeves", matrix.GetList(MetadataTokens.Cast));
        Assert.Equal("mpaa|R|400|", matrix.GetString(MetadataTokens.Rating));
        Assert.NotEmpty(matrix.Artworks);
    }

    [Fact]
    public async Task TheTvDb_FindsBreakingBadPilot()
    {
        Live.RequireEnabled();
        var provider = new TvdbProvider(s_http, Settings());
        if (!provider.IsConfigured)
            Assert.Skip("No TheTVDB key configured.");

        var results = await provider.SearchTvAsync("Breaking Bad", 1, 1, "eng", TestContext.Current.CancellationToken);
        var pilot = Assert.Single(results);
        await provider.LoadDetailsAsync(pilot, "eng", TestContext.Current.CancellationToken);

        Assert.Equal("Pilot", pilot.GetString(MetadataTokens.Name));
        Assert.Contains("Bryan Cranston", pilot.GetList(MetadataTokens.Cast));
        Assert.NotEmpty(pilot.Artworks);
    }
}
