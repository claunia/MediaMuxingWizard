using System.Globalization;
using MMW.Core.Metadata;

namespace MMW.Metadata.Search;

/// <summary>
/// One search hit from a metadata provider: a bag of <c>{Token}</c> values (see <see cref="MetadataTokens"/>)
/// plus the artwork the provider offers. Values are <see cref="string"/>, <see cref="int"/> or
/// <see cref="IReadOnlyList{T}"/> of string (people lists).
/// </summary>
public sealed class MetadataResult
{
    /// <summary>Creates an empty result.</summary>
    /// <param name="provider">Name of the provider that produced the result.</param>
    /// <param name="kind">Movie or TV episode.</param>
    public MetadataResult(string provider, MediaSearchKind kind)
    {
        Provider = provider;
        Kind = kind;
    }

    /// <summary>Provider name (as in <see cref="IMetadataProvider.Name"/>).</summary>
    public string Provider { get; }

    /// <summary>Movie or TV episode.</summary>
    public MediaSearchKind Kind { get; }

    /// <summary>iTunes media kind written to the file: 9 (Movie) or 10 (TV Show).</summary>
    public int MediaKind => Kind == MediaSearchKind.Movie ? TagCatalog.MediaKindMovie : TagCatalog.MediaKindTvShow;

    /// <summary>Token values keyed by token name (case-insensitive).</summary>
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Artwork offered by the provider; usually completed by <see cref="IMetadataProvider.LoadDetailsAsync"/>.</summary>
    public List<RemoteArtwork> Artworks { get; } = [];

    /// <summary>Provider-specific identifier used to load details (movie id, episode id …).</summary>
    public string? ProviderId { get; set; }

    /// <summary>Provider-specific secondary identifier (e.g. series id for an episode).</summary>
    public string? ProviderParentId { get; set; }

    /// <summary>True once <see cref="IMetadataProvider.LoadDetailsAsync"/> completed for this result.</summary>
    public bool IsDetailed { get; set; }

    /// <summary>Title shown in the result list: the movie title, or "S1E05 - Title" for episodes.</summary>
    public string DisplayTitle
    {
        get
        {
            var name = GetString(MetadataTokens.Name) ?? string.Empty;
            if (Kind == MediaSearchKind.Movie)
                return name;
            var season = GetInt(MetadataTokens.Season);
            var episode = GetInt(MetadataTokens.EpisodeNumber);
            if (season is null && episode is null)
                return name;
            var prefix = string.Create(CultureInfo.InvariantCulture, $"S{season ?? 0}E{episode ?? 0:00}");
            return name.Length == 0 ? prefix : $"{prefix} - {name}";
        }
    }

    /// <summary>Secondary line in the result list: the year for movies, the series name for episodes.</summary>
    public string Subtitle
    {
        get
        {
            var year = Year;
            if (Kind == MediaSearchKind.Movie)
                return year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var series = GetString(MetadataTokens.SeriesName) ?? string.Empty;
            return year is null ? series : string.Create(CultureInfo.InvariantCulture, $"{series} ({year})");
        }
    }

    /// <summary>Release year parsed from the Release Date token.</summary>
    public int? Year
    {
        get
        {
            var date = GetString(MetadataTokens.ReleaseDate);
            return date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        }
    }

    /// <summary>Gets or sets a token value; setting null or an empty string/list removes it.</summary>
    public object? this[string token]
    {
        get => Values.GetValueOrDefault(token);
        set => Set(token, value);
    }

    /// <summary>Sets a token, ignoring null, blank strings and empty lists.</summary>
    public void Set(string token, object? value)
    {
        switch (value)
        {
            case null:
            case string s when string.IsNullOrWhiteSpace(s):
                Values.Remove(token);
                return;
            case string s:
                Values[token] = s.Trim();
                return;
            case IEnumerable<string> list:
                var items = list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                if (items.Length == 0)
                    Values.Remove(token);
                else
                    Values[token] = items;
                return;
            default:
                Values[token] = value;
                return;
        }
    }

    /// <summary>Sets a token only when it has no value yet.</summary>
    public void SetIfMissing(string token, object? value)
    {
        if (!Values.ContainsKey(token))
            Set(token, value);
    }

    /// <summary>Returns a token as text (lists joined with ", ").</summary>
    public string? GetString(string token) => Values.GetValueOrDefault(token) switch
    {
        null => null,
        string s => s,
        IReadOnlyList<string> list => string.Join(", ", list),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>Returns a token as an integer when it is one (or is numeric text).</summary>
    public int? GetInt(string token) => Values.GetValueOrDefault(token) switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    /// <summary>Returns a token as a list (single strings become a one-element list).</summary>
    public IReadOnlyList<string> GetList(string token) => Values.GetValueOrDefault(token) switch
    {
        IReadOnlyList<string> list => list,
        string s => [s],
        _ => [],
    };

    /// <inheritdoc />
    public override string ToString() => $"{DisplayTitle} [{Provider}]";
}
