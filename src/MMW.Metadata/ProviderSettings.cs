using MMW.Metadata.Caching;

namespace MMW.Metadata;

/// <summary>User-adjustable provider configuration (usually built from the Preferences window).</summary>
public sealed class ProviderSettings
{
    private ApiKeys? _resolved;
    private ApiKeys? _userApiKeys;
    private ApiKeys? _fileApiKeys;

    /// <summary>User overrides from Preferences; non-empty keys take precedence over environment and file.</summary>
    public ApiKeys? UserApiKeys
    {
        get => _userApiKeys;
        set
        {
            _userApiKeys = value;
            _resolved = null;
        }
    }

    /// <summary>Keys from appsettings.json; null loads them with <see cref="ApiKeys.Load"/> on first use.</summary>
    public ApiKeys? FileApiKeys
    {
        get => _fileApiKeys;
        set
        {
            _fileApiKeys = value;
            _resolved = null;
        }
    }

    /// <summary>Effective keys: user override, then environment variables, then appsettings.json.</summary>
    public ApiKeys ApiKeys => _resolved ??= ApiKeys.Resolve(UserApiKeys, FileApiKeys ??= ApiKeys.Load());

    /// <summary>Optional TheTVDB subscriber PIN. Never required and not exposed in the UI.</summary>
    public string? TvdbPin { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country whose certifications are used for the Rating token (default "US").</summary>
    public string RatingCountry { get; set; } = "US";

    /// <summary>Optional on-disk response cache shared by the providers.</summary>
    public SearchCache? Cache { get; set; }

    /// <summary>How long cached responses stay valid.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromDays(2);

    /// <summary>Maximum number of episode results returned by a single TV search.</summary>
    public int MaxEpisodeResults { get; set; } = 500;

    /// <summary>Forgets the resolved keys so environment changes are picked up again.</summary>
    public void InvalidateApiKeys() => _resolved = null;
}
