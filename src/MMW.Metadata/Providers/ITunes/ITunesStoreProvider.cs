using System.Globalization;
using System.Text.RegularExpressions;
using MMW.Metadata.Certifications;
using MMW.Metadata.Http;
using MMW.Metadata.Search;

namespace MMW.Metadata.Providers.ITunes;

/// <summary>
/// iTunes Store provider (iTunes Search API: <c>itunes.apple.com/search</c> and <c>/lookup</c>, no key needed).
/// Requests are throttled by the process-wide <see cref="RequestRateLimiter.ITunes"/> limiter.
/// </summary>
/// <remarks>Apple has largely moved movies to the Apple TV app; movie searches often return nothing today.</remarks>
public sealed partial class ITunesStoreProvider : IMetadataProvider
{
    /// <summary>Provider display name.</summary>
    public const string ProviderName = "iTunes Store";

    private static readonly Uri s_defaultBase = new("https://itunes.apple.com/");
    private const int MaxSeasons = 12;

    private readonly ProviderHttp _http;
    private readonly Uri _baseUri;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="settings">Provider settings (cache).</param>
    /// <param name="baseUri">API root (tests).</param>
    /// <param name="rateLimiter">Rate limiter; defaults to the shared 20 requests/minute limiter.</param>
    public ITunesStoreProvider(HttpClient httpClient, ProviderSettings? settings = null, Uri? baseUri = null, RequestRateLimiter? rateLimiter = null)
    {
        _http = new ProviderHttp(httpClient, settings ?? new ProviderSettings(), ProviderName, rateLimiter ?? RequestRateLimiter.ITunes);
        _baseUri = baseUri ?? s_defaultBase;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public bool SupportsMovies => true;

    /// <inheritdoc />
    public bool SupportsTv => true;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public ProviderLanguageType LanguageType => ProviderLanguageType.Storefront;

    /// <inheritdoc />
    public string DefaultLanguage => Storefronts.UnitedStates.Name;

    /// <inheritdoc />
    public IReadOnlyList<string> Languages { get; } = Storefronts.All.Select(s => s.Name).ToList();

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchMovieAsync(string title, int? year, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("movie search", async () =>
        {
            if (string.IsNullOrWhiteSpace(title))
                return [];
            var store = Storefronts.FindOrDefault(language);
            var response = await GetAsync("search", new QueryString().Add("term", title.Trim()).Add("country", store.Iso)
                .Add("media", "movie").Add("entity", "movie").Add("limit", 50), cancellationToken).ConfigureAwait(false);
            var movies = (response?.Results ?? []).Where(i => i.Kind == "feature-movie" && i.TrackId is not null).Select(i => Movie(i, store)).ToList();
            if (year is { } y)
            {
                // Results within a year of the requested one first (release dates differ between countries).
                movies = movies.OrderBy(m => m.Year is { } my ? Math.Min(Math.Abs(my - y), 2) : 2).ToList();
            }

            return movies;
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<string>>("series name search", async () =>
        {
            if (string.IsNullOrWhiteSpace(partial))
                return [];
            var store = Storefronts.FindOrDefault(language);
            var response = await GetAsync("search", new QueryString().Add("term", partial.Trim()).Add("country", store.Iso)
                .Add("media", "tvShow").Add("entity", "tvSeason").Add("limit", 50), cancellationToken).ConfigureAwait(false);
            return ProviderText.Names((response?.Results ?? []).Select(i => i.ArtistName));
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("TV search", async () =>
        {
            if (string.IsNullOrWhiteSpace(seriesName))
                return [];
            var store = Storefronts.FindOrDefault(language);
            var term = season is { } s ? $"{seriesName.Trim()} season {s}" : seriesName.Trim();
            var response = await GetAsync("search", new QueryString().Add("term", term).Add("country", store.Iso)
                .Add("media", "tvShow").Add("entity", "tvSeason").Add("limit", 200), cancellationToken).ConfigureAwait(false);

            var seasons = (response?.Results ?? [])
                .Where(i => i.CollectionId is not null && SeasonNumber(i.CollectionName) is not null)
                .Where(i => season is null || SeasonNumber(i.CollectionName) == season)
                .ToList();
            var exact = seasons.Where(i => ProviderText.SameTitle(i.ArtistName, seriesName)).ToList();
            if (exact.Count > 0)
                seasons = exact;
            seasons = seasons
                .OrderBy(i => SeasonNumber(i.CollectionName))
                .ThenBy(i => IsPlainSeason(i.CollectionName) ? 0 : 1) // prefer "Show, Season 1" over deluxe/box editions
                .DistinctBy(i => (i.ArtistId, SeasonNumber(i.CollectionName)))
                .Take(MaxSeasons)
                .ToList();

            var results = new List<MetadataResult>();
            foreach (var seasonItem in seasons)
            {
                var lookup = await GetAsync("lookup", new QueryString().Add("id", seasonItem.CollectionId).Add("country", store.Iso)
                    .Add("entity", "tvEpisode").Add("limit", 200), cancellationToken).ConfigureAwait(false);
                var episodes = (lookup?.Results ?? []).Where(i => i.Kind == "tv-episode" && i.TrackId is not null).ToList();
                var count = episodes.Count;
                foreach (var ep in episodes.Where(e => episode is null || e.TrackNumber == episode).OrderBy(e => e.TrackNumber))
                    results.Add(Episode(ep, seasonItem, store, count));
            }

            return results;
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<MetadataResult> LoadDetailsAsync(MetadataResult result, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _http.GuardAsync("load details", async () =>
        {
            if (result.ProviderId is null)
                return result;
            var store = Storefronts.FindOrDefault(language);
            var lookup = await GetAsync("lookup", new QueryString().Add("id", result.ProviderId).Add("country", store.Iso), cancellationToken).ConfigureAwait(false);
            var item = lookup?.Results?.FirstOrDefault(i => i.TrackId == result.ProviderId);
            if (item is not null)
            {
                result.SetIfMissing(MetadataTokens.LongDescription, ProviderText.StripHtml(item.LongDescription));
                result.SetIfMissing(MetadataTokens.Copyright, item.Copyright);
            }

            if (result.Kind == MediaSearchKind.TvEpisode && result.ProviderParentId is { } collectionId && !result.Values.ContainsKey(MetadataTokens.Copyright))
            {
                var season = await GetAsync("lookup", new QueryString().Add("id", collectionId).Add("country", store.Iso), cancellationToken).ConfigureAwait(false);
                var collection = season?.Results?.FirstOrDefault(i => i.CollectionId == collectionId);
                result.Set(MetadataTokens.Copyright, collection?.Copyright);
                result.SetIfMissing(MetadataTokens.SeriesDescription, ProviderText.StripHtml(collection?.LongDescription));
            }

            result.IsDetailed = true;
            return result;
        }, result, cancellationToken);
    }

    private static MetadataResult Movie(ITunesItem item, Storefront store)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.Movie) { ProviderId = item.TrackId };
        result.Set(MetadataTokens.Name, item.TrackName);
        result.Set(MetadataTokens.Director, item.ArtistName is { } d ? d.Split([" & ", ", "], StringSplitOptions.TrimEntries) : null);
        result.Set(MetadataTokens.Genre, item.PrimaryGenreName);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(item.ReleaseDate));
        result.Set(MetadataTokens.Description, ProviderText.StripHtml(item.ShortDescription ?? item.LongDescription));
        result.Set(MetadataTokens.LongDescription, ProviderText.StripHtml(item.LongDescription));
        result.Set(MetadataTokens.Rating, RatingMapper.Encode(item.ContentAdvisoryRating, store.Iso, MediaSearchKind.Movie));
        result.Set(MetadataTokens.Copyright, item.Copyright);
        SetStoreIds(result, item, store);
        AddArtwork(result, item, ArtworkKind.Poster);
        return result;
    }

    private static MetadataResult Episode(ITunesItem ep, ITunesItem seasonItem, Storefront store, int episodeCount)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.TvEpisode) { ProviderId = ep.TrackId, ProviderParentId = seasonItem.CollectionId };
        var seasonNumber = SeasonNumber(ep.CollectionName) ?? SeasonNumber(seasonItem.CollectionName) ?? 0;
        var number = ep.TrackNumber ?? 0;
        result.Set(MetadataTokens.Name, EpisodeTitle(ep.TrackName));
        result.Set(MetadataTokens.SeriesName, ep.ArtistName ?? seasonItem.ArtistName);
        result.Set(MetadataTokens.SeriesDescription, ProviderText.StripHtml(seasonItem.LongDescription));
        result.Set(MetadataTokens.Season, seasonNumber);
        result.Set(MetadataTokens.EpisodeNumber, number);
        result.Set(MetadataTokens.TrackNumber, episodeCount > 0 ? $"{number}/{episodeCount}" : number.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.DiskNumber, $"{ep.DiscNumber ?? 1}/{ep.DiscCount ?? 1}");
        result.Set(MetadataTokens.EpisodeId, string.Create(CultureInfo.InvariantCulture, $"{seasonNumber}{number:00}"));
        result.Set(MetadataTokens.Genre, ep.PrimaryGenreName ?? seasonItem.PrimaryGenreName);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(ep.ReleaseDate));
        result.Set(MetadataTokens.Description, ProviderText.StripHtml(ep.ShortDescription ?? ep.LongDescription));
        result.Set(MetadataTokens.LongDescription, ProviderText.StripHtml(ep.LongDescription));
        result.Set(MetadataTokens.Rating, RatingMapper.Encode(ep.ContentAdvisoryRating ?? seasonItem.ContentAdvisoryRating, store.Iso, MediaSearchKind.TvEpisode));
        result.Set(MetadataTokens.Copyright, seasonItem.Copyright);
        result.Set(MetadataTokens.ServiceSeriesId, ep.ArtistId);
        result.Set(MetadataTokens.ServiceEpisodeId, ep.TrackId);
        SetStoreIds(result, ep, store);
        AddArtwork(result, ep.ArtworkUrl100 is null ? seasonItem : ep, ArtworkKind.Square);
        return result;
    }

    private static void SetStoreIds(MetadataResult result, ITunesItem item, Storefront store)
    {
        if (int.TryParse(item.TrackId, NumberStyles.None, CultureInfo.InvariantCulture, out var contentId))
            result.Set(MetadataTokens.ContentId, contentId);
        if (int.TryParse(item.ArtistId, NumberStyles.None, CultureInfo.InvariantCulture, out var artistId))
            result.Set(MetadataTokens.ArtistId, artistId);
        if (int.TryParse(item.CollectionId, NumberStyles.None, CultureInfo.InvariantCulture, out var playlistId))
            result.Set(MetadataTokens.PlaylistId, playlistId);
        result.Set(MetadataTokens.ITunesCountry, store.Id);
        result.Set(MetadataTokens.ITunesUrl, item.TrackViewUrl ?? item.CollectionViewUrl);
    }

    private static void AddArtwork(MetadataResult result, ITunesItem item, ArtworkKind kind)
    {
        var source = item.ArtworkUrl100 ?? item.ArtworkUrl60;
        if (source is null)
            return;
        if (UpsizeArtwork(source, 600) is { } thumb && UpsizeArtwork(source, 3000) is { } full)
            result.Artworks.Add(new RemoteArtwork(thumb, full, kind, ProviderName));
    }

    /// <summary>Rewrites an iTunes artwork URL ("…/100x100bb.jpg") to the requested square size.</summary>
    internal static Uri? UpsizeArtwork(string url, int size)
    {
        var replaced = ArtworkSize().Replace(url, string.Create(CultureInfo.InvariantCulture, $"/{size}x{size}bb."), 1);
        return Uri.TryCreate(replaced, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Season number from a collection name such as "Breaking Bad, Season 5" (null when absent).</summary>
    internal static int? SeasonNumber(string? collectionName)
    {
        if (collectionName is null)
            return null;
        var m = SeasonPattern().Match(collectionName);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Strips the "Season 5, Episode 1: " prefix the search endpoint adds to episode names.</summary>
    internal static string? EpisodeTitle(string? trackName) =>
        trackName is null ? null : EpisodePrefix().Replace(trackName, string.Empty).Trim();

    private static bool IsPlainSeason(string? collectionName) =>
        collectionName is not null && PlainSeason().IsMatch(collectionName);

    private Task<ITunesResponse?> GetAsync(string path, QueryString query, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, path + query);
        return _http.GetJsonAsync(uri, ITunesJsonContext.Default.ITunesResponse, path + query, null, ct);
    }

    [GeneratedRegex(@"/\d+x\d+(bb|-\d+)?\.", RegexOptions.RightToLeft)]
    private static partial Regex ArtworkSize();

    [GeneratedRegex(@"\b(?:Seasons?|Saisons?|Staffeln?|Temporadas?|Stagioni|Stagione|Seizoen|Säsong|Sesong|Sæson|Kausi|Sezon)\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();

    [GeneratedRegex(@",\s*(?:Season|Saison|Staffel|Temporada|Stagione|Seizoen)\s+\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainSeason();

    [GeneratedRegex(@"^\s*(?:Season|Saison|Staffel|Temporada|Stagione)\s+\d+\s*,\s*(?:Episode|Épisode|Folge|Episodio|Afl\.?)\s+\d+\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePrefix();
}
