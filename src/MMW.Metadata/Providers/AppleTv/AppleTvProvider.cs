using System.Globalization;
using MMW.Metadata.Certifications;
using MMW.Metadata.Http;
using MMW.Metadata.Providers.ITunes;
using MMW.Metadata.Search;

namespace MMW.Metadata.Providers.AppleTv;

/// <summary>
/// EXPERIMENTAL Apple TV app provider (undocumented <c>uts-api.itunes.apple.com/uts/v2</c> API used by tv.apple.com).
/// The API can change without notice: every failure is logged and turned into an empty result.
/// </summary>
public sealed class AppleTvProvider : IMetadataProvider
{
    /// <summary>Provider display name.</summary>
    public const string ProviderName = "Apple TV";

    private static readonly Uri s_defaultBase = new("https://uts-api.itunes.apple.com/uts/v2/");
    private const int PageSize = 100;
    private const int MaxPages = 10;
    private const int MaxShows = 3;

    private readonly ProviderHttp _http;
    private readonly Uri _baseUri;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="settings">Provider settings (cache).</param>
    /// <param name="baseUri">API root (tests).</param>
    public AppleTvProvider(HttpClient httpClient, ProviderSettings? settings = null, Uri? baseUri = null)
    {
        _http = new ProviderHttp(httpClient, settings ?? new ProviderSettings(), ProviderName);
        _baseUri = baseUri ?? s_defaultBase;
    }

    /// <summary>The provider relies on an undocumented API.</summary>
    public static bool IsExperimental => true;

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
            var items = await SearchAsync(title, store, cancellationToken).ConfigureAwait(false);
            var movies = items.Where(i => i.Type == "Movie" && i.Id is not null).Select(i => Movie(i, store)).ToList();
            if (year is { } y)
                movies = movies.OrderBy(m => m.Year is { } my ? Math.Min(Math.Abs(my - y), 2) : 2).ToList();
            return movies;
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<string>>("series name search", async () =>
        {
            if (string.IsNullOrWhiteSpace(partial))
                return [];
            var items = await SearchAsync(partial, Storefronts.FindOrDefault(language), cancellationToken).ConfigureAwait(false);
            return ProviderText.Names(items.Where(i => i.Type == "Show").Select(i => i.Title));
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("TV search", async () =>
        {
            if (string.IsNullOrWhiteSpace(seriesName))
                return [];
            var store = Storefronts.FindOrDefault(language);
            var shows = (await SearchAsync(seriesName, store, cancellationToken).ConfigureAwait(false))
                .Where(i => i.Type == "Show" && i.Id is not null).ToList();
            var exact = shows.Where(s => ProviderText.SameTitle(s.Title, seriesName)).Take(MaxShows).ToList();
            var chosen = exact.Count > 0 ? exact : shows.Take(1).ToList();

            var results = new List<MetadataResult>();
            foreach (var show in chosen)
            {
                var episodes = await EpisodesAsync(show.Id!, store, cancellationToken).ConfigureAwait(false);
                var counts = episodes.GroupBy(e => e.SeasonNumber ?? 0).ToDictionary(g => g.Key, g => g.Count());
                foreach (var ep in episodes.Where(e => (season is null || e.SeasonNumber == season) && (episode is null || e.EpisodeNumber == episode)))
                    results.Add(Episode(ep, show, store, counts.GetValueOrDefault(ep.SeasonNumber ?? 0)));
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
            var product = await ProductAsync(result.ProviderId, store, cancellationToken).ConfigureAwait(false);
            if (product?.Content is not { } content)
                return result;

            var kind = result.Kind == MediaSearchKind.Movie ? MediaSearchKind.Movie : MediaSearchKind.TvEpisode;
            result.Set(MetadataTokens.Genre, ProviderText.Join(content.Genres?.Select(g => g.Name)));
            result.SetIfMissing(MetadataTokens.Studio, content.Studio);
            result.SetIfMissing(MetadataTokens.Rating, Rating(content.Rating, store, kind));
            var roles = product.Roles ?? [];

            if (result.Kind == MediaSearchKind.TvEpisode && result.ProviderParentId is { } showId)
            {
                // Series-level information (network, regular cast, series description) lives on the show product.
                var showProduct = await ProductAsync(showId, store, cancellationToken).ConfigureAwait(false);
                if (showProduct?.Content is { } show)
                {
                    result.SetIfMissing(MetadataTokens.Network, show.Network);
                    result.SetIfMissing(MetadataTokens.SeriesDescription, show.Description);
                    result.SetIfMissing(MetadataTokens.Genre, ProviderText.Join(show.Genres?.Select(g => g.Name)));
                    result.SetIfMissing(MetadataTokens.Studio, show.Studio);
                    AddImages(result, show.Images, ArtworkKind.Poster);
                    roles = [.. showProduct.Roles ?? [], .. roles];
                }
            }

            string[] Of(params string[] types) => ProviderText.Names(roles.Where(r => types.Contains(r.Type, StringComparer.OrdinalIgnoreCase)).Select(r => r.PersonName));
            result.Set(MetadataTokens.Cast, Of("Actor", "GuestStar", "Voice"));
            result.Set(MetadataTokens.Director, Of("Director"));
            result.Set(MetadataTokens.Producers, Of("Producer"));
            result.Set(MetadataTokens.Screenwriters, Of("Writer", "Screenwriter"));
            result.Set(MetadataTokens.ExecutiveProducer, ProviderText.Join(Of("ExecutiveProducer")));
            result.Set(MetadataTokens.Composer, ProviderText.Join(Of("Composer", "Music")));
            AddImages(result, content.Images, result.Kind == MediaSearchKind.Movie ? ArtworkKind.Poster : ArtworkKind.Episode);
            result.IsDetailed = true;
            return result;
        }, result, cancellationToken);
    }

    private async Task<List<AtvItem>> SearchAsync(string term, Storefront store, CancellationToken ct)
    {
        var response = await GetAsync("search/incremental", Common(store).Add("q", term.Trim()), AppleTvJsonContext.Default.AtvEnvelopeAtvSearchData, ct).ConfigureAwait(false);
        return (response?.Data?.Canvas?.Shelves ?? []).SelectMany(s => s.Items ?? []).ToList();
    }

    private async Task<AtvProductData?> ProductAsync(string id, Storefront store, CancellationToken ct) =>
        (await GetAsync($"view/product/{Uri.EscapeDataString(id)}", Common(store), AppleTvJsonContext.Default.AtvEnvelopeAtvProductData, ct).ConfigureAwait(false))?.Data;

    private async Task<List<AtvItem>> EpisodesAsync(string showId, Storefront store, CancellationToken ct)
    {
        var all = new List<AtvItem>();
        for (var page = 0; page < MaxPages; page++)
        {
            var query = Common(store).Add("skip", page * PageSize).Add("count", PageSize);
            var response = await GetAsync($"view/show/{Uri.EscapeDataString(showId)}/episodes", query, AppleTvJsonContext.Default.AtvEnvelopeAtvEpisodesData, ct).ConfigureAwait(false);
            var batch = response?.Data?.Episodes ?? [];
            all.AddRange(batch);
            if (batch.Count < PageSize)
                break;
        }

        return all.Where(e => e.Id is not null).DistinctBy(e => e.Id).OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.EpisodeNumber ?? 0).ToList();
    }

    private static MetadataResult Movie(AtvItem item, Storefront store)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.Movie) { ProviderId = item.Id };
        result.Set(MetadataTokens.Name, item.Title);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.DateFromUnixMilliseconds(item.ReleaseDate));
        result.Set(MetadataTokens.Description, item.Description);
        result.Set(MetadataTokens.LongDescription, item.Description);
        result.Set(MetadataTokens.Rating, Rating(item.Rating, store, MediaSearchKind.Movie));
        result.Set(MetadataTokens.Genre, ProviderText.Join(item.Genres?.Select(g => g.Name)));
        result.Set(MetadataTokens.Studio, item.Studio);
        result.Set(MetadataTokens.ITunesUrl, item.Url);
        result.Set(MetadataTokens.ITunesCountry, store.Id);
        if (int.TryParse(item.AdamId, NumberStyles.None, CultureInfo.InvariantCulture, out var adam))
            result.Set(MetadataTokens.ContentId, adam);
        AddImages(result, item.Images, ArtworkKind.Poster);
        return result;
    }

    private static MetadataResult Episode(AtvItem ep, AtvItem show, Storefront store, int seasonCount)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.TvEpisode) { ProviderId = ep.Id, ProviderParentId = ep.ShowId ?? show.Id };
        var season = ep.SeasonNumber ?? 0;
        var number = ep.EpisodeNumber ?? 0;
        result.Set(MetadataTokens.Name, ep.Title);
        result.Set(MetadataTokens.SeriesName, ep.ShowTitle ?? show.Title);
        result.Set(MetadataTokens.Season, season);
        result.Set(MetadataTokens.EpisodeNumber, number);
        result.Set(MetadataTokens.TrackNumber, seasonCount > 0 ? $"{number}/{seasonCount}" : number.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.DiskNumber, "1/1");
        result.Set(MetadataTokens.EpisodeId, string.Create(CultureInfo.InvariantCulture, $"{season}{number:00}"));
        result.Set(MetadataTokens.ReleaseDate, ProviderText.DateFromUnixMilliseconds(ep.ReleaseDate));
        result.Set(MetadataTokens.Description, ep.Description);
        result.Set(MetadataTokens.LongDescription, ep.Description);
        result.Set(MetadataTokens.Rating, Rating(ep.Rating ?? show.Rating, store, MediaSearchKind.TvEpisode));
        result.Set(MetadataTokens.ServiceSeriesId, ep.ShowId ?? show.Id);
        result.Set(MetadataTokens.ServiceEpisodeId, ep.Id);
        result.Set(MetadataTokens.ITunesUrl, ep.Url);
        result.Set(MetadataTokens.ITunesCountry, store.Id);
        AddImages(result, ep.Images, ArtworkKind.Episode);
        AddImages(result, ep.SeasonImages, ArtworkKind.Season, season);
        AddImages(result, ep.ShowImages ?? show.Images, ArtworkKind.Poster);
        return result;
    }

    private static string? Rating(AtvRating? rating, Storefront store, MediaSearchKind kind)
    {
        if (rating is null)
            return null;
        var cert = rating.DisplayName ?? rating.Name;
        return RatingMapper.Encode(cert, store.Iso, kind);
    }

    /// <summary>
    /// Adds the useful images of an Apple TV image dictionary. Keys: coverArt (poster / square), coverArt16X9,
    /// previewFrame (episode still or backdrop).
    /// </summary>
    private static void AddImages(MetadataResult result, Dictionary<string, AtvImage>? images, ArtworkKind primaryKind, int? season = null)
    {
        if (images is null)
            return;
        foreach (var (key, image) in images)
        {
            ArtworkKind? kind = key switch
            {
                "coverArt" => primaryKind == ArtworkKind.Episode ? null : image.Width == image.Height && primaryKind == ArtworkKind.Poster ? ArtworkKind.Square : primaryKind,
                "coverArt16X9" => primaryKind == ArtworkKind.Episode ? ArtworkKind.Episode : ArtworkKind.Rectangle,
                "previewFrame" => primaryKind == ArtworkKind.Episode ? ArtworkKind.Episode : ArtworkKind.Backdrop,
                _ => null,
            };
            if (kind is not { } k || image.Url is null)
                continue;
            var full = Expand(image.Url, image.Width ?? 3000, image.Height ?? 3000);
            var thumbWidth = 342;
            var thumbHeight = image.Width is > 0 && image.Height is > 0 ? (int)Math.Round(342.0 * image.Height.Value / image.Width.Value) : 342;
            var thumb = Expand(image.Url, thumbWidth, thumbHeight);
            if (full is null || thumb is null || result.Artworks.Any(a => a.FullUrl == full))
                continue;
            result.Artworks.Add(new RemoteArtwork(thumb, full, k, ProviderName, image.Width, image.Height) { Season = season });
        }
    }

    /// <summary>Fills the Apple image URL template ("…/{w}x{h}{c}.{f}").</summary>
    internal static Uri? Expand(string template, int width, int height)
    {
        var url = template
            .Replace("{w}", width.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{h}", height.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{c}", string.Empty, StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static QueryString Common(Storefront store) => new QueryString()
        .Add("sf", store.Id).Add("locale", store.Locale).Add("utsk", "0").Add("caller", "wta").Add("v", "58").Add("pfm", "appletv");

    private Task<T?> GetAsync<T>(string path, QueryString query, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) =>
        _http.GetJsonAsync(new Uri(_baseUri, path + query), type, path + query, null, ct);
}
