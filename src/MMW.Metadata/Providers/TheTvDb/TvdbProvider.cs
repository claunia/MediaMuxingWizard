using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Metadata.Certifications;
using MMW.Metadata.Http;
using MMW.Metadata.Search;

namespace MMW.Metadata.Providers.TheTvDb;

/// <summary>
/// TheTVDB v4 API provider for TV episodes. Authenticates with the project API key only (the subscriber PIN is
/// optional); the bearer token is cached per process and refreshed on HTTP 401.
/// </summary>
public sealed class TvdbProvider : IMetadataProvider
{
    /// <summary>Provider display name.</summary>
    public const string ProviderName = "TheTVDB";

    private static readonly Uri s_defaultBase = new("https://api4.thetvdb.com/v4/");
    private const int MaxCast = 30;
    private const int MaxEpisodePages = 20;

    private readonly ProviderHttp _http;
    private readonly ProviderSettings _settings;
    private readonly Uri _baseUri;
    private readonly TimeProvider _time;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="settings">Provider settings (keys, rating country, cache).</param>
    /// <param name="baseUri">API root (tests); defaults to https://api4.thetvdb.com/v4/.</param>
    /// <param name="timeProvider">Clock used for token expiry.</param>
    public TvdbProvider(HttpClient httpClient, ProviderSettings? settings = null, Uri? baseUri = null, TimeProvider? timeProvider = null)
    {
        _settings = settings ?? new ProviderSettings();
        _http = new ProviderHttp(httpClient, _settings, ProviderName);
        _baseUri = baseUri ?? s_defaultBase;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public bool SupportsMovies => false;

    /// <inheritdoc />
    public bool SupportsTv => true;

    /// <inheritdoc />
    public bool IsConfigured => _settings.ApiKeys.HasTheTvDb;

    /// <inheritdoc />
    public ProviderLanguageType LanguageType => ProviderLanguageType.Language;

    /// <inheritdoc />
    public string DefaultLanguage => "eng";

    /// <inheritdoc />
    public IReadOnlyList<string> Languages { get; } =
    [
        "ara", "bul", "ces", "dan", "deu", "ell", "eng", "est", "fas", "fin", "fra", "heb", "hrv", "hun", "ita", "jpn", "kor",
        "lav", "lit", "nld", "nor", "pol", "por", "pt", "ron", "rus", "slk", "slv", "spa", "srp", "swe", "tha", "tur", "ukr",
        "vie", "zho",
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchMovieAsync(string title, int? year, string language, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MetadataResult>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<string>>("series name search", async () =>
        {
            if (!EnsureConfigured() || string.IsNullOrWhiteSpace(partial))
                return [];
            var lang = ApiLanguage(language);
            var items = await SearchSeriesAsync(partial, cancellationToken).ConfigureAwait(false);
            return ProviderText.Names(items.Select(i => Translated(i.Translations, lang) ?? i.Name));
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("TV search", async () =>
        {
            if (!EnsureConfigured() || string.IsNullOrWhiteSpace(seriesName))
                return [];
            var lang = ApiLanguage(language);
            var items = (await SearchSeriesAsync(seriesName, cancellationToken).ConfigureAwait(false)).Where(i => i.SeriesId is not null).ToList();
            var exact = items.Where(i => ProviderText.SameTitle(i.Name, seriesName)
                                         || (i.Translations?.Values.Any(t => ProviderText.SameTitle(t, seriesName)) ?? false)
                                         || (i.Aliases?.Any(a => ProviderText.SameTitle(a, seriesName)) ?? false)).Take(3).ToList();
            var chosen = exact.Count > 0 ? exact : items.Take(1).ToList();

            var results = new List<MetadataResult>();
            foreach (var item in chosen)
            {
                var series = await GetSeriesAsync(item.SeriesId!, cancellationToken).ConfigureAwait(false);
                var episodes = await GetEpisodesAsync(item.SeriesId!, season, episode, lang, cancellationToken).ConfigureAwait(false);
                var counts = episodes.GroupBy(e => e.SeasonNumber ?? 0).ToDictionary(g => g.Key, g => g.Count());
                foreach (var ep in episodes)
                {
                    var r = new MetadataResult(ProviderName, MediaSearchKind.TvEpisode)
                    {
                        ProviderId = ep.Id?.ToString(CultureInfo.InvariantCulture),
                        ProviderParentId = item.SeriesId,
                    };
                    ApplySeries(r, series, item, lang);
                    ApplyEpisode(r, ep, lang, episode is null ? counts.GetValueOrDefault(ep.SeasonNumber ?? 0) : 0);
                    AddSeasonPoster(r, series, ep.SeasonNumber);
                    AddArtwork(r, series?.Image ?? item.ImageUrl, null, ArtworkKind.Poster, null, null, null, null);
                    results.Add(r);
                    if (results.Count >= _settings.MaxEpisodeResults)
                        return results;
                }
            }

            return results;
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<MetadataResult> LoadDetailsAsync(MetadataResult result, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _http.GuardAsync("load details", async () =>
        {
            if (!EnsureConfigured() || result.ProviderId is null || result.Kind != MediaSearchKind.TvEpisode)
                return result;
            var lang = ApiLanguage(language);
            var episode = (await GetAsync($"episodes/{result.ProviderId}/extended", new QueryString().Add("meta", "translations"),
                TvdbJsonContext.Default.TvdbEnvelopeTvdbEpisode, cancellationToken).ConfigureAwait(false))?.Data;
            var seriesId = result.ProviderParentId ?? episode?.SeriesId?.ToString(CultureInfo.InvariantCulture);
            var series = seriesId is null ? null : await GetSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false);

            if (series is not null)
                ApplySeries(result, series, null, lang);
            if (episode is not null)
            {
                ApplyEpisode(result, episode, lang, 0, keepTrackTotal: true);
                ApplyPeople(result, series, episode);
                if (episode.ContentRatings is { Count: > 0 })
                    ApplyRating(result, episode.ContentRatings);
                if (episode.Networks?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Name)) is { } network && !result.Values.ContainsKey(MetadataTokens.Network))
                    result.Set(MetadataTokens.Network, network.Name);
            }

            result.Artworks.Clear();
            if (episode?.Image is { } still)
                AddArtwork(result, still, null, ArtworkKind.Episode, null, null, null, null);
            AddSeasonPoster(result, series, result.GetInt(MetadataTokens.Season));

            var artworks = seriesId is null ? null : (await GetAsync($"series/{seriesId}/artworks", new QueryString(),
                TvdbJsonContext.Default.TvdbEnvelopeTvdbArtworkPage, cancellationToken).ConfigureAwait(false))?.Data?.Artworks;
            var all = (artworks ?? series?.Artworks ?? [])
                .OrderByDescending(a => LanguageRank(a.Language, lang))
                .ThenByDescending(a => a.Score ?? 0);
            foreach (var art in all)
            {
                if (KindOf(art.Type) is { } kind)
                    AddArtwork(result, art.Image, art.Thumbnail, kind, art.Width, art.Height, art.Language, null);
            }

            if (!result.Artworks.Any(a => a.Kind == ArtworkKind.Poster))
                AddArtwork(result, series?.Image, null, ArtworkKind.Poster, null, null, null, null);
            result.IsDetailed = true;
            return result;
        }, result, cancellationToken);
    }

    private async Task<List<TvdbSearchItem>> SearchSeriesAsync(string query, CancellationToken ct)
    {
        var response = await GetAsync("search", new QueryString().Add("query", query.Trim()).Add("type", "series"),
            TvdbJsonContext.Default.TvdbEnvelopeListTvdbSearchItem, ct).ConfigureAwait(false);
        return response?.Data ?? [];
    }

    private async Task<TvdbSeries?> GetSeriesAsync(string seriesId, CancellationToken ct) =>
        (await GetAsync($"series/{seriesId}/extended", new QueryString().Add("meta", "translations"),
            TvdbJsonContext.Default.TvdbEnvelopeTvdbSeries, ct).ConfigureAwait(false))?.Data;

    private async Task<List<TvdbEpisode>> GetEpisodesAsync(string seriesId, int? season, int? episode, string lang, CancellationToken ct)
    {
        var episodes = new List<TvdbEpisode>();
        for (var page = 0; page < MaxEpisodePages; page++)
        {
            var query = new QueryString().Add("page", page).Add("season", season).Add("episodeNumber", episode);
            TvdbEnvelope<TvdbEpisodePage>? response;
            try
            {
                response = await GetAsync($"series/{seriesId}/episodes/default/{lang}", query, TvdbJsonContext.Default.TvdbEnvelopeTvdbEpisodePage, ct).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound && lang != "eng")
            {
                // No translation in this language: fall back to English.
                response = await GetAsync($"series/{seriesId}/episodes/default/eng", query, TvdbJsonContext.Default.TvdbEnvelopeTvdbEpisodePage, ct).ConfigureAwait(false);
            }

            var batch = response?.Data?.Episodes ?? [];
            episodes.AddRange(batch.Where(e => (season is null || e.SeasonNumber == season) && (episode is null || e.Number == episode)));
            if (batch.Count == 0 || string.IsNullOrEmpty(response?.Links?.Next) || episode is not null)
                break;
        }

        return episodes
            .Where(e => season is not null || (e.SeasonNumber ?? 0) > 0)
            .OrderBy(e => e.SeasonNumber ?? 0).ThenBy(e => e.Number ?? 0)
            .ToList();
    }

    private void ApplySeries(MetadataResult result, TvdbSeries? series, TvdbSearchItem? item, string lang)
    {
        var name = Translated(series?.Translations?.NameTranslations, lang, t => t.Name)
                   ?? Translated(item?.Translations, lang)
                   ?? series?.Name ?? item?.Name;
        var overview = Translated(series?.Translations?.OverviewTranslations, lang, t => t.Overview)
                       ?? Translated(item?.Overviews, lang)
                       ?? series?.Overview ?? item?.Overview;
        result.Set(MetadataTokens.SeriesName, name);
        result.Set(MetadataTokens.SeriesDescription, overview);
        result.Set(MetadataTokens.ServiceSeriesId, series?.Id?.ToString(CultureInfo.InvariantCulture) ?? item?.SeriesId);
        result.Set(MetadataTokens.Network, series?.OriginalNetwork?.Name ?? series?.LatestNetwork?.Name ?? item?.Network);
        if (series is null)
            return;
        result.Set(MetadataTokens.Genre, ProviderText.Join(series.Genres?.Select(g => g.Name)));
        result.Set(MetadataTokens.Studio, ProviderText.Join(series.Companies?
            .Where(c => c.CompanyType?.Name is { } t && (t.Contains("Studio", StringComparison.OrdinalIgnoreCase) || t.Contains("Production", StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)));
        ApplyRating(result, series.ContentRatings);
        result.Set(MetadataTokens.Cast, ProviderText.Names(Actors(series.Characters)).Take(MaxCast));
    }

    private static void ApplyEpisode(MetadataResult result, TvdbEpisode ep, string lang, int seasonCount, bool keepTrackTotal = false)
    {
        var season = ep.SeasonNumber ?? 0;
        var number = ep.Number ?? 0;
        var name = Translated(ep.Translations?.NameTranslations, lang, t => t.Name) ?? ep.Name;
        var overview = Translated(ep.Translations?.OverviewTranslations, lang, t => t.Overview) ?? ep.Overview;
        result.Set(MetadataTokens.Name, name);
        result.Set(MetadataTokens.Season, season);
        result.Set(MetadataTokens.EpisodeNumber, number);
        if (!(keepTrackTotal && result.Values.ContainsKey(MetadataTokens.TrackNumber)))
            result.Set(MetadataTokens.TrackNumber, seasonCount > 0 ? $"{number}/{seasonCount}" : number.ToString(CultureInfo.InvariantCulture));
        result.SetIfMissing(MetadataTokens.DiskNumber, "1/1");
        result.Set(MetadataTokens.EpisodeId, !string.IsNullOrWhiteSpace(ep.ProductionCode)
            ? ep.ProductionCode
            : string.Create(CultureInfo.InvariantCulture, $"{season}{number:00}"));
        result.Set(MetadataTokens.ServiceEpisodeId, ep.Id?.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(ep.Aired));
        result.Set(MetadataTokens.Description, overview);
        result.Set(MetadataTokens.LongDescription, overview);
        if (ep.Image is { } still)
            AddArtwork(result, still, null, ArtworkKind.Episode, null, null, null, null);
    }

    private static void ApplyPeople(MetadataResult result, TvdbSeries? series, TvdbEpisode episode)
    {
        var people = episode.Characters ?? [];
        string[] Of(params string[] types) => ProviderText.Names(people
            .Where(c => c.PeopleType is { } t && types.Any(x => t.Equals(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Sort ?? int.MaxValue).Select(c => c.Person));

        var cast = Actors(series?.Characters).Concat(Of("Actor", "Guest Star"));
        result.Set(MetadataTokens.Cast, ProviderText.Names(cast).Take(MaxCast));
        result.Set(MetadataTokens.Director, Of("Director"));
        result.Set(MetadataTokens.Screenwriters, Of("Writer"));
        result.Set(MetadataTokens.Producers, Of("Producer"));
        result.Set(MetadataTokens.ExecutiveProducer, ProviderText.Join(Of("Executive Producer")));
        result.Set(MetadataTokens.Composer, ProviderText.Join(Of("Composer", "Musical Guest")));
        if (episode.Companies is { Count: > 0 } companies)
        {
            var studio = ProviderText.Join(companies
                .Where(c => c.CompanyType?.Name is { } t && t.Contains("Studio", StringComparison.OrdinalIgnoreCase)).Select(c => c.Name));
            if (studio is not null)
                result.Set(MetadataTokens.Studio, studio);
        }
    }

    private static IEnumerable<string?> Actors(List<TvdbCharacter>? characters) =>
        (characters ?? [])
            .Where(c => c.Type == 3 || string.Equals(c.PeopleType, "Actor", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Sort ?? int.MaxValue)
            .Select(c => c.Person);

    private void ApplyRating(MetadataResult result, List<TvdbContentRating>? ratings)
    {
        if (ratings is null || ratings.Count == 0)
            return;
        var iso2 = _settings.RatingCountry;
        var iso3 = ThreeLetterRegion(iso2);
        var match = ratings.FirstOrDefault(r => r.Country is { } c && (c.Equals(iso3, StringComparison.OrdinalIgnoreCase) || c.Equals(iso2, StringComparison.OrdinalIgnoreCase))
                                                && !string.IsNullOrWhiteSpace(r.Name)
                                                && (r.ContentType is null || !r.ContentType.Contains("movie", StringComparison.OrdinalIgnoreCase)));
        if (RatingMapper.Encode(match?.Name, iso2, MediaSearchKind.TvEpisode) is { } encoded)
            result.Set(MetadataTokens.Rating, encoded);
    }

    private static void AddSeasonPoster(MetadataResult result, TvdbSeries? series, int? season)
    {
        if (series?.Seasons is null || season is null)
            return;
        var match = series.Seasons
            .Where(s => s.Number == season && !string.IsNullOrWhiteSpace(s.Image))
            .OrderBy(s => string.Equals(s.Type?.Type, "official", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (match is not null)
            AddArtwork(result, match.Image, null, ArtworkKind.Season, null, null, null, season);
    }

    private static void AddArtwork(MetadataResult result, string? image, string? thumbnail, ArtworkKind kind, int? width, int? height, string? language, int? season)
    {
        if (ToUri(image) is not { } full)
            return;
        if (result.Artworks.Any(a => a.FullUrl == full))
            return;
        var thumb = ToUri(thumbnail) ?? full;
        result.Artworks.Add(new RemoteArtwork(thumb, full, kind, ProviderName, width, height) { Language = language, Season = season });
    }

    private static Uri? ToUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (path.StartsWith("//", StringComparison.Ordinal))
            path = "https:" + path;
        else if (path.StartsWith('/'))
            path = "https://artworks.thetvdb.com" + path;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>TheTVDB artwork type ids (see /artwork/types).</summary>
    internal static ArtworkKind? KindOf(int? type) => type switch
    {
        2 or 14 => ArtworkKind.Poster,
        7 => ArtworkKind.Season,
        11 or 12 => ArtworkKind.Episode,
        3 or 8 or 15 => ArtworkKind.Backdrop,
        1 or 6 or 16 => ArtworkKind.Rectangle,
        5 or 10 => ArtworkKind.Square,
        _ => null,
    };

    private static int LanguageRank(string? artLanguage, string lang) =>
        artLanguage is null ? 1 : artLanguage.Equals(lang, StringComparison.OrdinalIgnoreCase) ? 3 : artLanguage == "eng" ? 2 : 0;

    private static string? Translated(Dictionary<string, string>? translations, string lang) =>
        translations is not null && translations.TryGetValue(lang, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? Translated(List<TvdbNameTranslation>? translations, string lang, Func<TvdbNameTranslation, string?> selector) =>
        translations?.Where(t => string.Equals(t.Language, lang, StringComparison.OrdinalIgnoreCase)).Select(selector).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static string ThreeLetterRegion(string iso2)
    {
        try
        {
            return new RegionInfo(iso2).ThreeLetterISORegionName.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "usa";
        }
    }

    /// <summary>TheTVDB language code (ISO 639-2, e.g. "eng") from any ISO 639 code or BCP-47 tag.</summary>
    internal static string ApiLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "eng";
        var trimmed = language.Trim();
        if (trimmed.Equals("pt-BR", StringComparison.OrdinalIgnoreCase))
            return "pt";
        if (trimmed.Equals("pt", StringComparison.Ordinal))
            return "pt";
        var code = LanguageTable.ToIso639_2T(trimmed);
        return code == LanguageTable.Undetermined ? "eng" : code;
    }

    private bool EnsureConfigured()
    {
        if (IsConfigured)
            return true;
        AppLog.Warn($"{ProviderName}: no API key configured (set it in appsettings.json, {ApiKeys.TvdbEnvironmentVariable} or Preferences).");
        return false;
    }

    private string TokenKey => _baseUri + "|" + _settings.ApiKeys.TheTvDb + "|" + _settings.TvdbPin;

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var key = TokenKey;
        if (TvdbTokenCache.Get(key, _time.GetUtcNow()) is { } cached)
            return cached;

        var body = new TvdbLoginRequest
        {
            ApiKey = _settings.ApiKeys.TheTvDb,
            Pin = string.IsNullOrWhiteSpace(_settings.TvdbPin) ? null : _settings.TvdbPin,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "login"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, TvdbJsonContext.Default.TvdbLoginRequest), Encoding.UTF8, "application/json"),
        };
        var text = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var token = JsonSerializer.Deserialize(text, TvdbJsonContext.Default.TvdbEnvelopeTvdbLogin)?.Data?.Token;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("TheTVDB login returned no token.");
        TvdbTokenCache.Set(key, token, _time.GetUtcNow());
        return token;
    }

    private async Task<T?> GetAsync<T>(string path, QueryString query, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, path + query);
        var cacheKey = path + query;
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(ct).ConfigureAwait(false);
            try
            {
                return await _http.GetJsonAsync(uri, type, cacheKey,
                    request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token), ct).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                TvdbTokenCache.Invalidate(TokenKey);
            }
        }
    }
}
