// ─────────────────────────────────────────────────────────────────────────────────────────────
//  Setting the online provider API keys
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  API keys are NEVER committed. They are resolved with this precedence (highest first):
//
//   1. User override from Preferences, passed in through ProviderSettings.UserApiKeys.
//   2. Environment variables MMW_TMDB_API_KEY, MMW_TVDB_API_KEY (and MMW_FANARTTV_API_KEY).
//   3. A git-ignored appsettings.json next to the application binaries (AppContext.BaseDirectory):
//
//        {
//          "ApiKeys": {
//            "TheMovieDb": "<v3 api key or v4 read access token>",
//            "TheTvDb": "<project api key>",
//            "FanartTv": ""
//          }
//        }
//
//  TMDb accepts either a v3 API key (sent as ?api_key=) or a v4 "API Read Access Token" (a JWT, sent as a
//  bearer token); the kind is detected automatically. TheTVDB uses the project key only: a subscriber PIN
//  is optional (ProviderSettings.TvdbPin) and never required.
// ─────────────────────────────────────────────────────────────────────────────────────────────

using System.Text.Json;
using MMW.Core.Diagnostics;

namespace MMW.Metadata;

/// <summary>API keys of the online metadata providers (see the comment at the top of this file).</summary>
/// <param name="TheMovieDb">TMDb v3 API key or v4 read access token.</param>
/// <param name="TheTvDb">TheTVDB v4 project API key.</param>
/// <param name="FanartTv">fanart.tv project key.</param>
public sealed record ApiKeys(string TheMovieDb = "", string TheTvDb = "", string FanartTv = "")
{
    /// <summary>Environment variable holding the TMDb key or v4 read token.</summary>
    public const string TmdbEnvironmentVariable = "MMW_TMDB_API_KEY";

    /// <summary>Environment variable holding the TheTVDB v4 project key.</summary>
    public const string TvdbEnvironmentVariable = "MMW_TVDB_API_KEY";

    /// <summary>Environment variable holding the fanart.tv project key.</summary>
    public const string FanartTvEnvironmentVariable = "MMW_FANARTTV_API_KEY";

    /// <summary>Name of the settings file read by <see cref="Load"/>.</summary>
    public const string SettingsFileName = "appsettings.json";

    /// <summary>No keys at all.</summary>
    public static ApiKeys Empty { get; } = new();

    /// <summary>True when the TMDb credential is set.</summary>
    public bool HasTheMovieDb => !string.IsNullOrWhiteSpace(TheMovieDb);

    /// <summary>True when the TheTVDB key is set.</summary>
    public bool HasTheTvDb => !string.IsNullOrWhiteSpace(TheTvDb);

    /// <summary>True when the fanart.tv key is set.</summary>
    public bool HasFanartTv => !string.IsNullOrWhiteSpace(FanartTv);

    /// <summary>
    /// Reads the <c>ApiKeys</c> section of <paramref name="path"/> (default:
    /// <c>appsettings.json</c> in <see cref="AppContext.BaseDirectory"/>). A missing file or section yields
    /// <see cref="Empty"/>; a malformed file logs a warning and also yields <see cref="Empty"/>.
    /// </summary>
    public static ApiKeys Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        string text;
        try
        {
            if (!File.Exists(path))
                return Empty;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not read API keys from {path}: {ex.Message}");
            return Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(doc.RootElement, "ApiKeys", out var section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            return new ApiKeys(Read(section, "TheMovieDb"), Read(section, "TheTvDb"), Read(section, "FanartTv"));
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"Ignoring malformed API key file {path}: {ex.Message}");
            return Empty;
        }
    }

    /// <summary>Keys taken from the MMW_*_API_KEY environment variables (unset variables give empty keys).</summary>
    public static ApiKeys FromEnvironment() => new(
        Environment.GetEnvironmentVariable(TmdbEnvironmentVariable)?.Trim() ?? string.Empty,
        Environment.GetEnvironmentVariable(TvdbEnvironmentVariable)?.Trim() ?? string.Empty,
        Environment.GetEnvironmentVariable(FanartTvEnvironmentVariable)?.Trim() ?? string.Empty);

    /// <summary>
    /// Returns a copy where every non-empty key of <paramref name="overrides"/> replaces this instance's key
    /// (empty override keys leave the current value alone).
    /// </summary>
    public ApiKeys WithOverrides(ApiKeys? overrides) => overrides is null
        ? this
        : new ApiKeys(Pick(overrides.TheMovieDb, TheMovieDb), Pick(overrides.TheTvDb, TheTvDb), Pick(overrides.FanartTv, FanartTv));

    /// <summary>
    /// Effective keys with the documented precedence: <paramref name="userOverride"/>, then the environment, then
    /// <paramref name="fileKeys"/> (default: <see cref="Load"/>).
    /// </summary>
    public static ApiKeys Resolve(ApiKeys? userOverride, ApiKeys? fileKeys = null) =>
        (fileKeys ?? Load()).WithOverrides(FromEnvironment()).WithOverrides(userOverride);

    /// <summary>True when <paramref name="credential"/> looks like a TMDb v4 read access token (a JWT).</summary>
    public static bool IsTmdbBearerToken(string? credential) =>
        credential is { Length: > 64 } && credential.StartsWith("eyJ", StringComparison.Ordinal) && credential.Count(c => c == '.') == 2;

    /// <inheritdoc />
    public override string ToString() =>
        $"ApiKeys {{ TheMovieDb = {Mask(TheMovieDb)}, TheTvDb = {Mask(TheTvDb)}, FanartTv = {Mask(FanartTv)} }}";

    private static string Pick(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    private static string Mask(string key) => string.IsNullOrEmpty(key) ? "<none>" : "***";

    private static string Read(JsonElement section, string name) =>
        TryGetProperty(section, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
