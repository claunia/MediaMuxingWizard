namespace MMW.Metadata.Search;

/// <summary>
/// Subler-compatible token names used as keys of <see cref="MetadataResult"/> and as
/// <c>{Token}</c> placeholders in <see cref="Mapping.MetadataMap"/> templates.
/// </summary>
public static class MetadataTokens
{
    public const string Name = "Name";
    public const string Composer = "Composer";
    public const string Genre = "Genre";
    public const string ReleaseDate = "Release Date";
    public const string Description = "Description";
    public const string LongDescription = "Long Description";
    public const string Rating = "Rating";
    public const string Studio = "Studio";
    public const string Cast = "Cast";
    public const string Director = "Director";
    public const string Producers = "Producers";
    public const string Screenwriters = "Screenwriters";
    public const string ExecutiveProducer = "Executive Producer";
    public const string Copyright = "Copyright";
    public const string ContentId = "contentID";
    public const string ArtistId = "artistID";
    public const string PlaylistId = "playlistID";
    public const string ITunesCountry = "iTunes Country";
    public const string ITunesUrl = "iTunes URL";
    public const string SeriesName = "Series Name";
    public const string SeriesDescription = "Series Description";
    public const string TrackNumber = "Track #";
    public const string DiskNumber = "Disk #";
    public const string EpisodeNumber = "Episode #";
    public const string EpisodeId = "Episode ID";
    public const string Season = "Season";
    public const string Network = "Network";
    public const string ServiceSeriesId = "ServiceSeriesID";
    public const string ServiceEpisodeId = "ServiceEpisodeID";

    /// <summary>Extra (non-Subler) tokens filled by some providers.</summary>
    public const string OriginalTitle = "Original Title";

    /// <summary>IMDb identifier (tt…), when known.</summary>
    public const string ImdbId = "IMDb ID";

    /// <summary>All tokens, in display order (used by the map editor's token picker).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Name, SeriesName, Season, EpisodeNumber, EpisodeId, TrackNumber, DiskNumber, Network, Genre, ReleaseDate,
        Description, LongDescription, SeriesDescription, Rating, Studio, Cast, Director, Producers, Screenwriters,
        ExecutiveProducer, Composer, Copyright, ContentId, ArtistId, PlaylistId, ITunesCountry, ITunesUrl,
        ServiceSeriesId, ServiceEpisodeId, OriginalTitle, ImdbId,
    ];
}
