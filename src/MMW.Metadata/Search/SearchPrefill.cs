using System.Globalization;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Metadata.Parsing;

namespace MMW.Metadata.Search;

/// <summary>Builds the initial search query for a document, like Subler's search window does.</summary>
public static class SearchPrefill
{
    /// <summary>
    /// Uses the existing tags when they identify the title (TV Show + TV Season + TV Episode # for TV, or Name +
    /// Release Date year for a movie), otherwise parses the file name.
    /// </summary>
    public static SearchQuery From(MediaDocument doc, string language = "")
    {
        ArgumentNullException.ThrowIfNull(doc);
        var tags = doc.Metadata;
        var mediaKind = tags.GetInt(TagId.MediaKind);
        var show = tags.GetString(TagId.TvShow);
        var season = tags.GetInt(TagId.TvSeason);
        var episode = tags.GetInt(TagId.TvEpisodeNumber);

        if (!string.IsNullOrWhiteSpace(show) && (mediaKind == TagCatalog.MediaKindTvShow || season is not null || episode is not null))
            return new SearchQuery(MediaSearchKind.TvEpisode, show.Trim(), null, season, episode, language);

        var name = tags.GetString(TagId.Name);
        if (!string.IsNullOrWhiteSpace(name) && mediaKind == TagCatalog.MediaKindMovie)
            return new SearchQuery(MediaSearchKind.Movie, name.Trim(), YearOf(tags.GetString(TagId.ReleaseDate)), null, null, language);

        if (!string.IsNullOrWhiteSpace(doc.Path))
        {
            var parsed = FileNameParser.Parse(doc.Path);
            if (parsed.Title.Length > 0)
                return parsed.ToQuery(language);
        }

        return !string.IsNullOrWhiteSpace(name)
            ? new SearchQuery(mediaKind == TagCatalog.MediaKindTvShow ? MediaSearchKind.TvEpisode : MediaSearchKind.Movie, name.Trim(), YearOf(tags.GetString(TagId.ReleaseDate)), null, null, language)
            : new SearchQuery(MediaSearchKind.Movie, string.Empty, null, null, null, language);
    }

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
}
