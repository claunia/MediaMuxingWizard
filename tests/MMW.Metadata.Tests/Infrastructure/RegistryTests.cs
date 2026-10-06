using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Providers.TheMovieDb;
using MMW.Metadata.Providers.TheTvDb;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests.Infrastructure;

[Collection(EnvironmentCollection.Name)]
public sealed class RegistryTests
{
    [Fact]
    public void Defaults_AreTmdbForMoviesAndTvdbForTv()
    {
        var registry = MetadataProviderRegistry.CreateDefault(new HttpClient(new FakeHttpHandler()), TestSettings.WithKeys());

        Assert.Equal(4, registry.Providers.Count);
        Assert.Equal(TmdbProvider.ProviderName, registry.DefaultFor(MediaSearchKind.Movie)!.Name);
        Assert.Equal(TvdbProvider.ProviderName, registry.DefaultFor(MediaSearchKind.TvEpisode)!.Name);
        Assert.DoesNotContain(registry.MovieProviders, p => p.Name == TvdbProvider.ProviderName);
        Assert.Equal(4, registry.TvProviders.Count);
        Assert.Same(registry.Find("itunes store"), registry.Providers.Single(p => p is ITunesStoreProvider));
        Assert.Null(registry.Find("nope"));
    }

    [Fact]
    public void UnconfiguredDefault_FallsBackToFirstConfiguredProvider()
    {
        var settings = new ProviderSettings { FileApiKeys = ApiKeys.Empty, UserApiKeys = ApiKeys.Empty };
        var registry = new MetadataProviderRegistry(
        [
            new TmdbProvider(new HttpClient(new FakeHttpHandler()), new ProviderSettings { FileApiKeys = ApiKeys.Empty }),
            new ITunesStoreProvider(new HttpClient(new FakeHttpHandler()), settings),
        ]);

        if (registry.Providers[0].IsConfigured)
            Assert.Skip("A TMDb key is configured in the environment.");
        Assert.Equal(ITunesStoreProvider.ProviderName, registry.DefaultFor(MediaSearchKind.Movie)!.Name);
    }
}
