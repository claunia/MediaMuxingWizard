using System.Net.Http.Headers;
using MMW.Core.Model;
using MMW.Metadata;
using MMW.Metadata.Artwork;
using MMW.Metadata.Caching;
using MMW.Metadata.Mapping;
using MMW.Metadata.Search;

namespace MMW.App.Services;

/// <summary>Shared metadata search services: providers, maps, cache, artwork downloads and recent searches.</summary>
public sealed class MetadataService : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;
    private readonly string _mapsPath;

    public MetadataService(ISettingsService settings, HttpClient? http = null, string? dataDirectory = null, MetadataProviderRegistry? registry = null)
    {
        _settings = settings;
        var dir = dataDirectory ?? SettingsService.AppDataDirectory;
        _http = http ?? CreateHttpClient();
        _mapsPath = Path.Combine(dir, "metadata-maps.json");
        ProviderSettings = new ProviderSettings { Cache = new SearchCache(Path.Combine(dir, "cache")) };
        ApplyKeyOverrides();
        Registry = registry ?? MetadataProviderRegistry.CreateDefault(_http, ProviderSettings);
        ApplyDefaults();
        Maps = MetadataMaps.Load(_mapsPath);
        Downloader = new ArtworkDownloader(_http);
        RecentSearches = new RecentSearches(Path.Combine(dir, "recent-searches.json"));
    }

    public ProviderSettings ProviderSettings { get; }

    public MetadataProviderRegistry Registry { get; }

    public MetadataMaps Maps { get; }

    public ArtworkDownloader Downloader { get; }

    public RecentSearches RecentSearches { get; }

    public HttpClient Http => _http;

    public AppSettings Settings => _settings.Settings;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(MetadataService).Assembly.GetName().Version?.ToString(3) ?? "0.1";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaMuxingWizard", version));
        return http;
    }

    /// <summary>Re-reads user settings (API key overrides, default providers) after Preferences changed.</summary>
    public void SettingsChanged()
    {
        ApplyKeyOverrides();
        ApplyDefaults();
    }

    public void SaveMaps() => Maps.Save(_mapsPath);

    public ApplyOptions ApplyOptionsFor(MediaSearchKind kind, bool replaceArtworks) => new()
    {
        Overwrite = Settings.MetadataOverwrite,
        KeepEmpty = Settings.MetadataKeepEmpty,
        Autodetect4K = Settings.Autodetect4K,
        ReplaceArtworks = replaceArtworks,
        MappedTags = Maps.For(kind).MappedTags.ToList(),
    };

    public string LanguageFor(IMetadataProvider provider) =>
        Settings.ProviderLanguages.GetValueOrDefault(provider.Name) is { Length: > 0 } lang && provider.Languages.Contains(lang)
            ? lang
            : provider.DefaultLanguage;

    public void RememberLanguage(IMetadataProvider provider, string language)
    {
        Settings.ProviderLanguages[provider.Name] = language;
        _settings.Save();
    }

    /// <summary>Kind to search for a document: TV when its tags or file name look like an episode.</summary>
    public static MediaSearchKind GuessKind(MediaDocument document) =>
        SearchPrefill.From(document).Kind;

    private void ApplyKeyOverrides()
    {
        var s = Settings;
        // Empty values fall through to the environment and appsettings.json.
        ProviderSettings.UserApiKeys = new ApiKeys(s.TmdbApiKey?.Trim() ?? string.Empty, s.TvdbApiKey?.Trim() ?? string.Empty);
        ProviderSettings.RatingCountry = Core.Metadata.Ratings.All.FirstOrDefault(r => r.Country == s.RatingsCountry) is null ? "US" : CountryIso(s.RatingsCountry);
    }

    private static string CountryIso(string ratingsCountry) => ratingsCountry switch
    {
        "USA" => "US",
        "UK" => "GB",
        "Australia" => "AU",
        "Canada" => "CA",
        "Deutschland" => "DE",
        "France" => "FR",
        "Italia" => "IT",
        "Ireland" => "IE",
        "日本" => "JP",
        "México" => "MX",
        "Nederland" => "NL",
        "New Zealand" => "NZ",
        "Sverige" => "SE",
        "Schweiz" or "Suisse" => "CH",
        "Россия" => "RU",
        "Brasil" => "BR",
        "Hong Kong" => "HK",
        "India" => "IN",
        "Česká republika" => "CZ",
        "Pilipinas" => "PH",
        "Paraguay" => "PY",
        _ => "US",
    };

    private void ApplyDefaults()
    {
        if (Settings.DefaultMovieProvider is { Length: > 0 } movie && Registry.Find(movie) is not null)
            Registry.DefaultMovieProviderName = movie;
        if (Settings.DefaultTvProvider is { Length: > 0 } tv && Registry.Find(tv) is not null)
            Registry.DefaultTvProviderName = tv;
    }

    public void Dispose() => _http.Dispose();
}
