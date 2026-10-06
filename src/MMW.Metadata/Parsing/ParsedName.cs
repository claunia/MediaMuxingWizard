using MMW.Metadata.Search;

namespace MMW.Metadata.Parsing;

/// <summary>What <see cref="FileNameParser"/> recognised in a file name.</summary>
/// <param name="Kind">Movie or TV episode.</param>
/// <param name="Title">Movie title or series name, cleaned of release tags.</param>
/// <param name="Year">Release year (movies, date-based episodes, or a year in a series name).</param>
/// <param name="Season">Season number (TV).</param>
/// <param name="Episode">First episode number (TV).</param>
public sealed record ParsedName(MediaSearchKind Kind, string Title, int? Year, int? Season, int? Episode)
{
    /// <summary>Last episode of a multi-episode file (S01E01E02 → 2), when different from <see cref="Episode"/>.</summary>
    public int? LastEpisode { get; init; }

    /// <summary>Air date of a date-based episode ("Show.2024.03.05").</summary>
    public DateOnly? AirDate { get; init; }

    /// <summary>Release group, when one was found ("[Group] …" or "…-GROUP").</summary>
    public string? Group { get; init; }

    /// <summary>Converts to a search query.</summary>
    public SearchQuery ToQuery(string language = "") => new(Kind, Title, Year, Season, Episode, language);
}
