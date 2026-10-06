namespace MMW.Metadata.Search;

/// <summary>How the values of <see cref="IMetadataProvider.Languages"/> must be interpreted.</summary>
public enum ProviderLanguageType
{
    /// <summary>ISO 639 language codes (show with <see cref="MMW.Core.Languages.LanguageTable.DisplayName"/>).</summary>
    Language,

    /// <summary>iTunes storefront country names.</summary>
    Storefront,
}

/// <summary>An online (or local) source of movie and TV metadata.</summary>
/// <remarks>
/// Implementations never throw for network or parsing problems: they log a warning through
/// <see cref="MMW.Core.Diagnostics.AppLog"/> and return an empty list (or the unchanged result). Only
/// <see cref="OperationCanceledException"/> caused by the caller's token propagates.
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>Display name ("TheMovieDB", "TheTVDB", "iTunes Store", "Apple TV").</summary>
    string Name { get; }

    /// <summary>True when the provider can search movies.</summary>
    bool SupportsMovies { get; }

    /// <summary>True when the provider can search TV episodes.</summary>
    bool SupportsTv { get; }

    /// <summary>False when a required API key is missing; searches then return nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>Selectable languages (codes) or storefronts (names), see <see cref="LanguageType"/>.</summary>
    IReadOnlyList<string> Languages { get; }

    /// <summary>How to display <see cref="Languages"/>.</summary>
    ProviderLanguageType LanguageType { get; }

    /// <summary>Language used when the caller passes an empty one.</summary>
    string DefaultLanguage { get; }

    /// <summary>Searches movies by title (and optional year).</summary>
    Task<IReadOnlyList<MetadataResult>> SearchMovieAsync(string title, int? year, string language, CancellationToken cancellationToken = default);

    /// <summary>Returns series names matching <paramref name="partial"/> (for the search box autocompletion).</summary>
    Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default);

    /// <summary>Searches episodes of a series; season and episode narrow the list when given.</summary>
    Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default);

    /// <summary>Completes a search result with cast and crew, ratings and the full artwork list.</summary>
    /// <returns>The same (now detailed) instance.</returns>
    Task<MetadataResult> LoadDetailsAsync(MetadataResult result, string language, CancellationToken cancellationToken = default);
}

/// <summary>Convenience extensions for <see cref="IMetadataProvider"/>.</summary>
public static class MetadataProviderExtensions
{
    /// <summary>Runs the search matching <see cref="SearchQuery.Kind"/>.</summary>
    public static Task<IReadOnlyList<MetadataResult>> SearchAsync(this IMetadataProvider provider, SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(query);
        return query.Kind == MediaSearchKind.Movie
            ? provider.SearchMovieAsync(query.Title, query.Year, query.Language, cancellationToken)
            : provider.SearchTvAsync(query.Title, query.Season, query.Episode, query.Language, cancellationToken);
    }

    /// <summary>True when the provider supports <paramref name="kind"/>.</summary>
    public static bool Supports(this IMetadataProvider provider, MediaSearchKind kind)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return kind == MediaSearchKind.Movie ? provider.SupportsMovies : provider.SupportsTv;
    }
}
