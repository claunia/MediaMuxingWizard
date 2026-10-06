using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMW.Core.Metadata;

/// <summary>
/// Builds file names from tag tokens, e.g. <c>{TV Show} s{TV Season:00}e{TV Episode #:00}</c> or
/// <c>{Name} ({Release Date:yyyy})</c>.
/// </summary>
/// <remarks>
/// Token syntax: <c>{Tag Name[:format][|transform]}</c>. The format is a .NET number format for numbers (e.g. "00"
/// for a leading zero) or "yyyy"/"MM"/"dd" for date parts. Transforms: upper, lower, capitalize, camel, snake,
/// train, dot. Text outside braces is copied verbatim.
/// </remarks>
public static partial class FileNameFormatter
{
    public const string DefaultMovieFormat = "{Name}";
    public const string DefaultTvFormat = "{TV Show} s{TV Season:00}e{TV Episode #:00}";

    public static IReadOnlyList<string> Transforms { get; } = ["upper", "lower", "capitalize", "camel", "snake", "train", "dot"];

    [GeneratedRegex(@"\{(?<name>[^{}:|]+)(?::(?<format>[^{}|]*))?(?:\|(?<transform>[a-z]+))?\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Formats <paramref name="pattern"/>; returns null when every token is empty.</summary>
    public static string? Format(string pattern, MetadataSet metadata)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(metadata);
        var anyValue = false;
        var result = Token().Replace(pattern, m =>
        {
            var value = Resolve(m.Groups["name"].Value.Trim(), m.Groups["format"].Value, metadata);
            if (value.Length > 0)
                anyValue = true;
            return Transform(value, m.Groups["transform"].Value);
        });

        return anyValue ? Sanitize(result) : null;
    }

    /// <summary>Picks the TV or movie pattern from the media kind and formats it.</summary>
    public static string? FormatFor(MetadataSet metadata, string moviePattern = DefaultMovieFormat, string tvPattern = DefaultTvFormat)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var isTv = metadata.GetInt(TagId.MediaKind) == TagCatalog.MediaKindTvShow || (metadata.Contains(TagId.TvShow) && metadata.Contains(TagId.TvEpisodeNumber));
        return Format(isTv ? tvPattern : moviePattern, metadata);
    }

    private static string Resolve(string name, string format, MetadataSet metadata)
    {
        var def = TagCatalog.All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (def is null || metadata[def.Id] is not { } value)
            return string.Empty;

        switch (value)
        {
            case int i when format.Length > 0:
                return i.ToString(format, CultureInfo.InvariantCulture);
            case IntPair p when format.Length > 0:
                return p.Number.ToString(format, CultureInfo.InvariantCulture);
            case IntPair p:
                return p.Number.ToString(CultureInfo.InvariantCulture);
            case string s when def.Kind == TagValueKind.Date && format.Length > 0:
                return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date.ToString(format, CultureInfo.InvariantCulture)
                    : s.Length >= 4 && format == "yyyy" ? s[..4] : s;
            default:
                return MetadataSet.FormatValue(def.Id, value);
        }
    }

    private static string Transform(string value, string transform)
    {
        if (value.Length == 0)
            return value;
        var words = Spaces().Split(value.Trim());
        return transform switch
        {
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "capitalize" => string.Join(' ', words.Select(Capitalize)),
            "camel" => string.Concat(words.Select((w, i) => i == 0 ? w.ToLowerInvariant() : Capitalize(w))),
            "snake" => string.Join('_', words).ToLowerInvariant(),
            "train" => string.Join('-', words.Select(Capitalize)),
            "dot" => string.Join('.', words),
            _ => value,
        };
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..].ToLower(CultureInfo.InvariantCulture);

    /// <summary>Removes characters that are invalid in file names on any supported OS.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            switch (c)
            {
                case ':' or '*' or '?' or '"' or '<' or '>' or '|':
                    continue;
                case '/' or '\\':
                    sb.Append('-');
                    break;
                default:
                    if (!char.IsControl(c))
                        sb.Append(c);
                    break;
            }
        }

        return Spaces().Replace(sb.ToString(), " ").Trim().TrimEnd('.');
    }
}
