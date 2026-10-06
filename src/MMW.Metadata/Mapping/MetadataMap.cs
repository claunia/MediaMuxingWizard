using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;
using MMW.Metadata.Search;

namespace MMW.Metadata.Mapping;

/// <summary>One row of a <see cref="MetadataMap"/>: a tag and the template producing its value.</summary>
/// <param name="Tag">Destination tag.</param>
/// <param name="Template">Text with <c>{Token}</c> placeholders, e.g. <c>"{Series Name}, Season {Season}"</c>.</param>
public sealed record MetadataMapEntry(TagId Tag, string Template);

/// <summary>
/// Subler-style "metadata result map": how the tokens of a <see cref="MetadataResult"/> become tags. One map exists
/// per search kind; maps are JSON serialisable so users can edit them and restore the defaults.
/// </summary>
public sealed partial class MetadataMap
{
    /// <summary>Movie or TV.</summary>
    public MediaSearchKind Kind { get; set; }

    /// <summary>Rows, in the order they are applied.</summary>
    public List<MetadataMapEntry> Entries { get; set; } = [];

    /// <summary>Tags written by this map.</summary>
    [JsonIgnore]
    public IEnumerable<TagId> MappedTags => Entries.Select(e => e.Tag).Distinct();

    /// <summary>Default movie map (matches Subler's defaults).</summary>
    public static MetadataMap DefaultMovie() => new()
    {
        Kind = MediaSearchKind.Movie,
        Entries =
        [
            new(TagId.Name, "{Name}"),
            new(TagId.Artist, "{Director}"),
            new(TagId.Composer, "{Composer}"),
            new(TagId.Genre, "{Genre}"),
            new(TagId.ReleaseDate, "{Release Date}"),
            new(TagId.Description, "{Description}"),
            new(TagId.LongDescription, "{Long Description}"),
            new(TagId.Rating, "{Rating}"),
            new(TagId.Studio, "{Studio}"),
            new(TagId.Cast, "{Cast}"),
            new(TagId.Director, "{Director}"),
            new(TagId.Producers, "{Producers}"),
            new(TagId.Screenwriters, "{Screenwriters}"),
            new(TagId.ExecutiveProducer, "{Executive Producer}"),
            new(TagId.Copyright, "{Copyright}"),
            new(TagId.ContentId, "{contentID}"),
            new(TagId.ArtistId, "{artistID}"),
            new(TagId.PlaylistId, "{playlistID}"),
            new(TagId.ITunesCountry, "{iTunes Country}"),
        ],
    };

    /// <summary>Default TV episode map (matches Subler's defaults).</summary>
    public static MetadataMap DefaultTv() => new()
    {
        Kind = MediaSearchKind.TvEpisode,
        Entries =
        [
            new(TagId.Name, "{Name}"),
            new(TagId.Artist, "{Series Name}"),
            new(TagId.AlbumArtist, "{Series Name}"),
            new(TagId.Album, "{Series Name}, Season {Season}"),
            new(TagId.Composer, "{Composer}"),
            new(TagId.Genre, "{Genre}"),
            new(TagId.ReleaseDate, "{Release Date}"),
            new(TagId.TrackNumber, "{Episode #}"),
            new(TagId.DiskNumber, "{Season}"),
            new(TagId.TvShow, "{Series Name}"),
            new(TagId.TvEpisodeId, "{Episode ID}"),
            new(TagId.TvSeason, "{Season}"),
            new(TagId.TvEpisodeNumber, "{Episode #}"),
            new(TagId.TvNetwork, "{Network}"),
            new(TagId.Description, "{Description}"),
            new(TagId.LongDescription, "{Long Description}"),
            new(TagId.SeriesDescription, "{Series Description}"),
            new(TagId.Rating, "{Rating}"),
            new(TagId.Studio, "{Studio}"),
            new(TagId.Cast, "{Cast}"),
            new(TagId.Director, "{Director}"),
            new(TagId.Producers, "{Producers}"),
            new(TagId.Screenwriters, "{Screenwriters}"),
            new(TagId.ExecutiveProducer, "{Executive Producer}"),
            new(TagId.Copyright, "{Copyright}"),
            new(TagId.ContentId, "{contentID}"),
            new(TagId.ArtistId, "{artistID}"),
            new(TagId.PlaylistId, "{playlistID}"),
            new(TagId.ITunesCountry, "{iTunes Country}"),
        ],
    };

    /// <summary>Default map for <paramref name="kind"/>.</summary>
    public static MetadataMap CreateDefault(MediaSearchKind kind) => kind == MediaSearchKind.Movie ? DefaultMovie() : DefaultTv();

    /// <summary>
    /// Builds the tags for <paramref name="result"/>. Entries whose template references a missing token are skipped;
    /// single-token templates keep lists as lists for list tags (Cast, Director …); Media Kind is set to 9 or 10
    /// unless the map has its own Media Kind row.
    /// </summary>
    public MetadataSet Apply(MetadataResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var set = new MetadataSet();
        foreach (var entry in Entries)
        {
            var value = Evaluate(entry, result);
            if (value is null)
                continue;
            try
            {
                set.Set(entry.Tag, value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                AppLog.Warn($"Metadata map: cannot store '{value}' in {TagCatalog.Get(entry.Tag).Name}: {ex.Message}");
            }
        }

        if (!set.Contains(TagId.MediaKind))
            set.Set(TagId.MediaKind, result.MediaKind);
        return set;
    }

    /// <summary>Tokens referenced by a template.</summary>
    public static IReadOnlyList<string> TokensOf(string template) =>
        TokenPattern().Matches(template ?? string.Empty).Select(m => m.Groups[1].Value).ToList();

    private static object? Evaluate(MetadataMapEntry entry, MetadataResult result)
    {
        var template = entry.Template ?? string.Empty;
        var matches = TokenPattern().Matches(template);
        if (matches.Count == 0)
            return string.IsNullOrWhiteSpace(template) ? null : template; // constant text

        var kind = TagCatalog.Get(entry.Tag).Kind;
        if (matches.Count == 1 && matches[0].Length == template.Length)
        {
            var raw = result[matches[0].Groups[1].Value];
            return raw switch
            {
                null => null,
                IReadOnlyList<string> list when kind == TagValueKind.StringList => list,
                IReadOnlyList<string> list => string.Join(", ", list),
                int i when kind == TagValueKind.IntegerPair => new IntPair(i, 0),
                int i when kind is TagValueKind.Integer or TagValueKind.Enum => i,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => raw.ToString(),
            };
        }

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in matches)
        {
            var text = result.GetString(m.Groups[1].Value);
            if (string.IsNullOrEmpty(text))
                return null;
            sb.Append(template, last, m.Index - last).Append(text);
            last = m.Index + m.Length;
        }

        sb.Append(template, last, template.Length - last);
        var composed = sb.ToString().Trim();
        return composed.Length == 0 ? null : composed;
    }

    /// <summary>Serialises the map to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MetadataMapJsonContext.Default.MetadataMap);

    /// <summary>Reads a map from JSON (unknown tags make the read fail with <see cref="JsonException"/>).</summary>
    public static MetadataMap FromJson(string json) =>
        JsonSerializer.Deserialize(json, MetadataMapJsonContext.Default.MetadataMap) ?? throw new JsonException("Empty metadata map.");

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenPattern();
}

/// <summary>The movie and TV maps, persisted together (e.g. in the preferences folder).</summary>
public sealed class MetadataMaps
{
    /// <summary>Movie map.</summary>
    public MetadataMap Movie { get; set; } = MetadataMap.DefaultMovie();

    /// <summary>TV map.</summary>
    public MetadataMap Tv { get; set; } = MetadataMap.DefaultTv();

    /// <summary>Map for <paramref name="kind"/>.</summary>
    public MetadataMap For(MediaSearchKind kind) => kind == MediaSearchKind.Movie ? Movie : Tv;

    /// <summary>Restores the default map for <paramref name="kind"/>.</summary>
    public void RestoreDefault(MediaSearchKind kind)
    {
        if (kind == MediaSearchKind.Movie)
            Movie = MetadataMap.DefaultMovie();
        else
            Tv = MetadataMap.DefaultTv();
    }

    /// <summary>Loads maps from <paramref name="path"/>; defaults when the file is missing or unreadable.</summary>
    public static MetadataMaps Load(string path)
    {
        if (!File.Exists(path))
            return new MetadataMaps();
        try
        {
            var maps = JsonSerializer.Deserialize(File.ReadAllText(path), MetadataMapJsonContext.Default.MetadataMaps) ?? new MetadataMaps();
            maps.Movie.Kind = MediaSearchKind.Movie;
            maps.Tv.Kind = MediaSearchKind.TvEpisode;
            return maps;
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"Metadata maps file {path} is invalid, using defaults: {ex.Message}");
            return new MetadataMaps();
        }
    }

    /// <summary>Saves the maps as JSON.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, MetadataMapJsonContext.Default.MetadataMaps));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MetadataMap))]
[JsonSerializable(typeof(MetadataMaps))]
internal sealed partial class MetadataMapJsonContext : JsonSerializerContext;
