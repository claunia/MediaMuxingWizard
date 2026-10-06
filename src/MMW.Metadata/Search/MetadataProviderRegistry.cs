using MMW.Metadata.Providers.AppleTv;
using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Providers.TheMovieDb;
using MMW.Metadata.Providers.TheTvDb;

namespace MMW.Metadata.Search;

/// <summary>The available metadata providers and the defaults per search kind (movies → TheMovieDB, TV → TheTVDB).</summary>
public sealed class MetadataProviderRegistry
{
    private readonly List<IMetadataProvider> _providers;

    /// <summary>Creates a registry from explicit providers.</summary>
    public MetadataProviderRegistry(IEnumerable<IMetadataProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToList();
    }

    /// <summary>Default movie provider name.</summary>
    public string DefaultMovieProviderName { get; set; } = TmdbProvider.ProviderName;

    /// <summary>Default TV provider name.</summary>
    public string DefaultTvProviderName { get; set; } = TvdbProvider.ProviderName;

    /// <summary>All providers.</summary>
    public IReadOnlyList<IMetadataProvider> Providers => _providers;

    /// <summary>Providers that can search movies.</summary>
    public IReadOnlyList<IMetadataProvider> MovieProviders => _providers.Where(p => p.SupportsMovies).ToList();

    /// <summary>Providers that can search TV episodes.</summary>
    public IReadOnlyList<IMetadataProvider> TvProviders => _providers.Where(p => p.SupportsTv).ToList();

    /// <summary>Providers supporting <paramref name="kind"/>.</summary>
    public IReadOnlyList<IMetadataProvider> For(MediaSearchKind kind) => kind == MediaSearchKind.Movie ? MovieProviders : TvProviders;

    /// <summary>Finds a provider by name (case-insensitive).</summary>
    public IMetadataProvider? Find(string? name) =>
        name is null ? null : _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default provider for <paramref name="kind"/>, falling back to the first configured one.</summary>
    public IMetadataProvider? DefaultFor(MediaSearchKind kind)
    {
        var candidates = For(kind);
        var preferred = Find(kind == MediaSearchKind.Movie ? DefaultMovieProviderName : DefaultTvProviderName);
        if (preferred is not null && preferred.Supports(kind) && preferred.IsConfigured)
            return preferred;
        return candidates.FirstOrDefault(p => p.IsConfigured) ?? preferred ?? (candidates.Count > 0 ? candidates[0] : null);
    }

    /// <summary>Creates the standard providers: TheMovieDB, TheTVDB, iTunes Store and Apple TV (experimental).</summary>
    public static MetadataProviderRegistry CreateDefault(HttpClient httpClient, ProviderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        settings ??= new ProviderSettings();
        return new MetadataProviderRegistry(
        [
            new TmdbProvider(httpClient, settings),
            new TvdbProvider(httpClient, settings),
            new ITunesStoreProvider(httpClient, settings),
            new AppleTvProvider(httpClient, settings),
        ]);
    }
}
