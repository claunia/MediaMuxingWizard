using System.Globalization;

namespace MMW.Metadata.Search;

/// <summary>A metadata search request as entered in the search window.</summary>
/// <param name="Kind">Movie or TV episode.</param>
/// <param name="Title">Movie title or series name.</param>
/// <param name="Year">Optional release year (movies).</param>
/// <param name="Season">Optional season number (TV).</param>
/// <param name="Episode">Optional episode number (TV).</param>
/// <param name="Language">Provider language code or storefront name; empty for the provider default.</param>
public sealed record SearchQuery(MediaSearchKind Kind, string Title, int? Year, int? Season, int? Episode, string Language)
{
    /// <summary>Human readable summary used by the recent-searches list.</summary>
    public override string ToString()
    {
        if (Kind == MediaSearchKind.Movie)
            return Year is { } y ? string.Create(CultureInfo.InvariantCulture, $"{Title} ({y})") : Title;

        var text = Title;
        if (Season is { } s)
            text += string.Create(CultureInfo.InvariantCulture, $" S{s:00}");
        if (Episode is { } e)
            text += string.Create(CultureInfo.InvariantCulture, $"{(Season is null ? " " : string.Empty)}E{e:00}");
        return text;
    }
}
