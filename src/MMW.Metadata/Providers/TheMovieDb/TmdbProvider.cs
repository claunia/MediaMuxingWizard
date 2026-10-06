using System.Globalization;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Metadata.Certifications;
using MMW.Metadata.Http;
using MMW.Metadata.Search;

namespace MMW.Metadata.Providers.TheMovieDb;

/// <summary>TheMovieDB (TMDb) v3 API provider for movies and TV episodes.</summary>
public sealed class TmdbProvider : IMetadataProvider
{
    /// <summary>Provider display name.</summary>
    public const string ProviderName = "TheMovieDB";

    private static readonly Uri s_defaultBase = new("https://api.themoviedb.org/3/");
    private const string ImageBase = "https://image.tmdb.org/t/p/";
    private const int MaxCast = 30;

    private readonly ProviderHttp _http;
    private readonly ProviderSettings _settings;
    private readonly Uri _baseUri;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">Shared HTTP client.</param>
    /// <param name="settings">Provider settings (API key override, rating country, cache).</param>
    /// <param name="baseUri">API root (tests); defaults to https://api.themoviedb.org/3/.</param>
    public TmdbProvider(HttpClient httpClient, ProviderSettings? settings = null, Uri? baseUri = null)
    {
        _settings = settings ?? new ProviderSettings();
        _http = new ProviderHttp(httpClient, _settings, ProviderName);
        _baseUri = baseUri ?? s_defaultBase;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public bool SupportsMovies => true;

    /// <inheritdoc />
    public bool SupportsTv => true;

    /// <inheritdoc />
    public bool IsConfigured => Credential is not null;

    /// <inheritdoc />
    public ProviderLanguageType LanguageType => ProviderLanguageType.Language;

    /// <inheritdoc />
    public string DefaultLanguage => "en";

    /// <inheritdoc />
    public IReadOnlyList<string> Languages { get; } =
    [
        "ar", "bg", "ca", "cs", "da", "de", "el", "en", "es", "et", "fa", "fi", "fr", "he", "hi", "hr", "hu", "id", "it",
        "ja", "ko", "lt", "lv", "nl", "no", "pl", "pt", "ro", "ru", "sk", "sl", "sr", "sv", "th", "tr", "uk", "vi", "zh",
    ];

    private string? Credential => _settings.ApiKeys.HasTheMovieDb ? _settings.ApiKeys.TheMovieDb : null;

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchMovieAsync(string title, int? year, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("movie search", async () =>
        {
            if (!EnsureConfigured() || string.IsNullOrWhiteSpace(title))
                return [];
            var lang = ApiLanguage(language);
            var page = await GetAsync("search/movie", new QueryString().Add("query", title.Trim()).Add("year", year).Add("language", lang).Add("include_adult", "false"),
                TmdbJsonContext.Default.TmdbPageTmdbMovie, cancellationToken).ConfigureAwait(false);
            var results = (page?.Results ?? []).Where(m => m.Id is not null).Select(MovieSummary).ToList();
            if (results.Count == 0 && year is not null)
            {
                // TMDb's year filter is strict (primary release year only); retry without it.
                page = await GetAsync("search/movie", new QueryString().Add("query", title.Trim()).Add("language", lang).Add("include_adult", "false"),
                    TmdbJsonContext.Default.TmdbPageTmdbMovie, cancellationToken).ConfigureAwait(false);
                results = (page?.Results ?? []).Where(m => m.Id is not null).Select(MovieSummary).ToList();
            }

            return results;
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SearchSeriesNamesAsync(string partial, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<string>>("series name search", async () =>
        {
            if (!EnsureConfigured() || string.IsNullOrWhiteSpace(partial))
                return [];
            var page = await GetAsync("search/tv", new QueryString().Add("query", partial.Trim()).Add("language", ApiLanguage(language)),
                TmdbJsonContext.Default.TmdbPageTmdbTvShow, cancellationToken).ConfigureAwait(false);
            return ProviderText.Names((page?.Results ?? []).Select(s => s.Name));
        }, [], cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataResult>> SearchTvAsync(string seriesName, int? season, int? episode, string language, CancellationToken cancellationToken = default) =>
        _http.GuardAsync<IReadOnlyList<MetadataResult>>("TV search", async () =>
        {
            if (!EnsureConfigured() || string.IsNullOrWhiteSpace(seriesName))
                return [];
            var lang = ApiLanguage(language);
            var page = await GetAsync("search/tv", new QueryString().Add("query", seriesName.Trim()).Add("language", lang),
                TmdbJsonContext.Default.TmdbPageTmdbTvShow, cancellationToken).ConfigureAwait(false);
            var shows = (page?.Results ?? []).Where(s => s.Id is not null).ToList();
            var exact = shows.Where(s => ProviderText.SameTitle(s.Name, seriesName) || ProviderText.SameTitle(s.OriginalName, seriesName)).Take(3).ToList();
            var chosen = exact.Count > 0 ? exact : shows.Take(1).ToList();

            var results = new List<MetadataResult>();
            foreach (var summary in chosen)
            {
                var show = await GetAsync($"tv/{summary.Id}", new QueryString().Add("language", lang).Add("append_to_response", "content_ratings"),
                    TmdbJsonContext.Default.TmdbTvShow, cancellationToken).ConfigureAwait(false);
                if (show is null)
                    continue;

                var seasons = season is { } s
                    ? [s]
                    : (show.Seasons ?? []).Select(x => x.SeasonNumber ?? -1).Where(x => x > 0).Order().ToList();
                foreach (var seasonNumber in seasons)
                {
                    TmdbSeason? seasonData;
                    try
                    {
                        seasonData = await GetAsync($"tv/{summary.Id}/season/{seasonNumber}", new QueryString().Add("language", lang),
                            TmdbJsonContext.Default.TmdbSeason, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProviderHttpException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        AppLog.Debug($"{ProviderName}: season {seasonNumber} of {show.Name} not found.");
                        continue;
                    }

                    if (seasonData is null)
                        continue;
                    var episodes = (seasonData.Episodes ?? []).Where(e => episode is null || e.EpisodeNumber == episode);
                    foreach (var ep in episodes)
                    {
                        results.Add(EpisodeResult(show, seasonData, ep));
                        if (results.Count >= _settings.MaxEpisodeResults)
                            return results;
                    }
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
            if (!EnsureConfigured() || result.ProviderId is null)
                return result;
            var lang = ApiLanguage(language);
            if (result.Kind == MediaSearchKind.Movie)
                await LoadMovieDetailsAsync(result, lang, cancellationToken).ConfigureAwait(false);
            else
                await LoadEpisodeDetailsAsync(result, lang, cancellationToken).ConfigureAwait(false);
            result.IsDetailed = true;
            return result;
        }, result, cancellationToken);
    }

    private async Task LoadMovieDetailsAsync(MetadataResult result, string lang, CancellationToken ct)
    {
        var movie = await GetAsync($"movie/{result.ProviderId}",
            new QueryString().Add("language", lang).Add("append_to_response", "credits,release_dates,images").Add("include_image_language", ImageLanguages(lang)),
            TmdbJsonContext.Default.TmdbMovie, ct).ConfigureAwait(false);
        if (movie is null)
            return;

        result.Set(MetadataTokens.Name, movie.Title);
        result.Set(MetadataTokens.OriginalTitle, movie.OriginalTitle);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(movie.ReleaseDate));
        result.Set(MetadataTokens.Description, movie.Overview);
        result.Set(MetadataTokens.LongDescription, movie.Overview);
        result.Set(MetadataTokens.Genre, ProviderText.Join(movie.Genres?.Select(g => g.Name)));
        result.Set(MetadataTokens.Studio, ProviderText.Join(movie.ProductionCompanies?.Select(c => c.Name)));
        result.Set(MetadataTokens.ImdbId, movie.ImdbId);
        ApplyCrew(result, movie.Credits?.Cast, movie.Credits?.Crew);

        var country = _settings.RatingCountry;
        var cert = (movie.ReleaseDates?.Results ?? [])
            .Where(r => string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.ReleaseDates ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d.Certification))
            .OrderBy(d => d.Type == 3 ? 0 : d.Type == 4 ? 1 : 2)
            .Select(d => d.Certification)
            .FirstOrDefault();
        result.Set(MetadataTokens.Rating, RatingMapper.Encode(cert, country, MediaSearchKind.Movie));

        result.Artworks.Clear();
        AddImages(result, movie.Images?.Posters, ArtworkKind.Poster, null);
        if (result.Artworks.Count == 0)
            AddImage(result, movie.PosterPath, ArtworkKind.Poster, null);
        AddImages(result, movie.Images?.Backdrops, ArtworkKind.Backdrop, null);
    }

    private async Task LoadEpisodeDetailsAsync(MetadataResult result, string lang, CancellationToken ct)
    {
        var seriesId = result.ProviderParentId;
        var season = result.GetInt(MetadataTokens.Season);
        var number = result.GetInt(MetadataTokens.EpisodeNumber);
        if (seriesId is null || season is null || number is null)
            return;

        var show = await GetAsync($"tv/{seriesId}",
            new QueryString().Add("language", lang).Add("append_to_response", "content_ratings,credits,images").Add("include_image_language", ImageLanguages(lang)),
            TmdbJsonContext.Default.TmdbTvShow, ct).ConfigureAwait(false);
        var episode = await GetAsync($"tv/{seriesId}/season/{season}/episode/{number}",
            new QueryString().Add("language", lang).Add("append_to_response", "credits,images"),
            TmdbJsonContext.Default.TmdbEpisode, ct).ConfigureAwait(false);
        var seasonData = await GetAsync($"tv/{seriesId}/season/{season}", new QueryString().Add("language", lang),
            TmdbJsonContext.Default.TmdbSeason, ct).ConfigureAwait(false);

        if (show is not null)
            ApplyShow(result, show);
        if (episode is not null)
        {
            result.Set(MetadataTokens.Name, episode.Name);
            result.Set(MetadataTokens.Description, episode.Overview);
            result.Set(MetadataTokens.LongDescription, episode.Overview);
            result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(episode.AirDate));

            var cast = new List<TmdbPerson>();
            cast.AddRange((show?.Credits?.Cast ?? []).OrderBy(c => c.Order ?? int.MaxValue));
            cast.AddRange(episode.Credits?.GuestStars ?? episode.GuestStars ?? []);
            var crew = episode.Credits?.Crew ?? episode.Crew ?? [];
            ApplyCrew(result, cast, crew, sortCast: false);
            if (show?.CreatedBy is { Count: > 0 } creators && !result.Values.ContainsKey(MetadataTokens.ExecutiveProducer))
                result.Set(MetadataTokens.ExecutiveProducer, ProviderText.Join(creators.Select(c => c.Name)));
        }

        result.Artworks.Clear();
        if (episode is not null)
        {
            AddImages(result, episode.Images?.Stills, ArtworkKind.Episode, null);
            if (!result.Artworks.Any(a => a.Kind == ArtworkKind.Episode))
                AddImage(result, episode.StillPath, ArtworkKind.Episode, null);
        }

        AddImage(result, seasonData?.PosterPath, ArtworkKind.Season, season);
        if (show is not null)
        {
            AddImages(result, show.Images?.Posters, ArtworkKind.Poster, null);
            if (!result.Artworks.Any(a => a.Kind == ArtworkKind.Poster))
                AddImage(result, show.PosterPath, ArtworkKind.Poster, null);
            AddImages(result, show.Images?.Backdrops, ArtworkKind.Backdrop, null);
        }
    }

    private MetadataResult MovieSummary(TmdbMovie movie)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.Movie) { ProviderId = movie.Id!.Value.ToString(CultureInfo.InvariantCulture) };
        result.Set(MetadataTokens.Name, movie.Title);
        result.Set(MetadataTokens.OriginalTitle, movie.OriginalTitle);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(movie.ReleaseDate));
        result.Set(MetadataTokens.Description, movie.Overview);
        result.Set(MetadataTokens.LongDescription, movie.Overview);
        AddImage(result, movie.PosterPath, ArtworkKind.Poster, null);
        return result;
    }

    private MetadataResult EpisodeResult(TmdbTvShow show, TmdbSeason season, TmdbEpisode ep)
    {
        var result = new MetadataResult(ProviderName, MediaSearchKind.TvEpisode)
        {
            ProviderId = ep.Id?.ToString(CultureInfo.InvariantCulture),
            ProviderParentId = show.Id?.ToString(CultureInfo.InvariantCulture),
        };
        ApplyShow(result, show);
        var seasonNumber = ep.SeasonNumber ?? season.SeasonNumber ?? 0;
        var episodeNumber = ep.EpisodeNumber ?? 0;
        var count = season.Episodes?.Count ?? 0;
        result.Set(MetadataTokens.Name, ep.Name);
        result.Set(MetadataTokens.Season, seasonNumber);
        result.Set(MetadataTokens.EpisodeNumber, episodeNumber);
        result.Set(MetadataTokens.TrackNumber, count > 0 ? $"{episodeNumber}/{count}" : episodeNumber.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.DiskNumber, "1/1");
        result.Set(MetadataTokens.EpisodeId, !string.IsNullOrWhiteSpace(ep.ProductionCode)
            ? ep.ProductionCode
            : string.Create(CultureInfo.InvariantCulture, $"{seasonNumber}{episodeNumber:00}"));
        result.Set(MetadataTokens.ServiceEpisodeId, ep.Id?.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.Description, ep.Overview);
        result.Set(MetadataTokens.LongDescription, ep.Overview);
        result.Set(MetadataTokens.ReleaseDate, ProviderText.Date(ep.AirDate));
        AddImage(result, ep.StillPath, ArtworkKind.Episode, null);
        AddImage(result, season.PosterPath, ArtworkKind.Season, seasonNumber);
        AddImage(result, show.PosterPath, ArtworkKind.Poster, null);
        return result;
    }

    private void ApplyShow(MetadataResult result, TmdbTvShow show)
    {
        result.Set(MetadataTokens.SeriesName, show.Name);
        result.Set(MetadataTokens.SeriesDescription, show.Overview);
        result.Set(MetadataTokens.ServiceSeriesId, show.Id?.ToString(CultureInfo.InvariantCulture));
        result.Set(MetadataTokens.Genre, ProviderText.Join(show.Genres?.Select(g => g.Name)));
        result.Set(MetadataTokens.Network, show.Networks?.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
        result.Set(MetadataTokens.Studio, ProviderText.Join(show.ProductionCompanies?.Select(c => c.Name)));
        var country = _settings.RatingCountry;
        var cert = show.ContentRatings?.Results?
            .FirstOrDefault(r => string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Rating))?.Rating;
        result.Set(MetadataTokens.Rating, RatingMapper.Encode(cert, country, MediaSearchKind.TvEpisode));
    }

    private static void ApplyCrew(MetadataResult result, IEnumerable<TmdbPerson>? cast, IEnumerable<TmdbPerson>? crew, bool sortCast = true)
    {
        var castList = cast ?? [];
        if (sortCast)
            castList = castList.OrderBy(c => c.Order ?? int.MaxValue);
        result.Set(MetadataTokens.Cast, ProviderText.Names(castList.Select(c => c.Name)).Take(MaxCast));

        var crewList = (crew ?? []).ToList();
        string[] ByJob(params string[] jobs) => ProviderText.Names(crewList.Where(c => jobs.Contains(c.Job, StringComparer.OrdinalIgnoreCase)).Select(c => c.Name));

        result.Set(MetadataTokens.Director, ByJob("Director"));
        result.Set(MetadataTokens.Producers, ByJob("Producer"));
        result.Set(MetadataTokens.ExecutiveProducer, ProviderText.Join(ByJob("Executive Producer")));
        result.Set(MetadataTokens.Composer, ProviderText.Join(ByJob("Original Music Composer", "Music", "Composer")));
        result.Set(MetadataTokens.Screenwriters, ProviderText.Names(crewList
            .Where(c => string.Equals(c.Department, "Writing", StringComparison.OrdinalIgnoreCase) || c.Job is "Screenplay" or "Writer" or "Teleplay")
            .Select(c => c.Name)));
    }

    private static void AddImages(MetadataResult result, List<TmdbImage>? images, ArtworkKind kind, int? season)
    {
        foreach (var image in images ?? [])
            AddImage(result, image.FilePath, kind, season, image.Width, image.Height, image.Language);
    }

    private static void AddImage(MetadataResult result, string? path, ArtworkKind kind, int? season, int? width = null, int? height = null, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var thumbSize = kind is ArtworkKind.Backdrop or ArtworkKind.Episode ? "w300" : "w342";
        var full = new Uri(ImageBase + "original" + path);
        if (result.Artworks.Any(a => a.FullUrl == full))
            return;
        result.Artworks.Add(new RemoteArtwork(new Uri(ImageBase + thumbSize + path), full, kind, ProviderName, width, height)
        {
            Language = language,
            Season = season,
        });
    }

    private bool EnsureConfigured()
    {
        if (IsConfigured)
            return true;
        AppLog.Warn($"{ProviderName}: no API key configured (set it in appsettings.json, {ApiKeys.TmdbEnvironmentVariable} or Preferences).");
        return false;
    }

    /// <summary>TMDb language parameter ("en", "pt-BR") from an ISO 639-1/639-2 code or BCP-47 tag.</summary>
    internal static string ApiLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";
        var bcp = LanguageTable.ToBcp47(language.Trim());
        return bcp == LanguageTable.Undetermined ? "en" : bcp;
    }

    private static string ImageLanguages(string lang)
    {
        var primary = lang.Split('-')[0];
        return primary == "en" ? "en,null" : $"{primary},en,null";
    }

    private Task<T?> GetAsync<T>(string path, QueryString query, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        var credential = Credential!;
        var bearer = ApiKeys.IsTmdbBearerToken(credential);
        var cacheKey = path + query.ToStringWithout("api_key");
        if (!bearer)
            query.Add("api_key", credential);
        var uri = new Uri(_baseUri, path + query);
        return _http.GetJsonAsync(uri, type, cacheKey, request =>
        {
            if (bearer)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        }, ct);
    }
}
