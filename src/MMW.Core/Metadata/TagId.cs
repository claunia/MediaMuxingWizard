namespace MMW.Core.Metadata;

/// <summary>Container-neutral identifier for every metadata field the application understands.</summary>
public enum TagId
{
    // General
    Name,
    TrackSubtitle,
    Artist,
    AlbumArtist,
    Album,
    Grouping,
    Composer,
    Comments,
    Genre,
    ReleaseDate,
    TrackNumber,
    DiskNumber,
    Tempo,
    Compilation,
    Keywords,
    Category,

    // Video / media flags
    MediaKind,
    HdVideo,
    Gapless,
    Podcast,
    ITunesU,

    // TV
    TvShow,
    TvNetwork,
    TvEpisodeId,
    TvSeason,
    TvEpisodeNumber,

    // People (iTunMOVI)
    Cast,
    Director,
    Codirector,
    Producers,
    Screenwriters,
    Studio,

    // Descriptions
    Description,
    LongDescription,
    SeriesDescription,
    Lyrics,

    // Sorting
    SortName,
    SortArtist,
    SortAlbumArtist,
    SortAlbum,
    SortComposer,
    SortTvShow,

    // Classical
    WorkName,
    MovementName,
    MovementNumber,
    MovementCount,
    ShowWorkAndMovement,

    // Song credits
    SongDescription,
    ArtDirector,
    Arranger,
    Lyricist,
    Acknowledgement,
    Conductor,
    LinerNotes,
    RecordCompany,
    OriginalArtist,
    PhonogramRights,
    SongProducer,
    Performer,
    Publisher,
    SoundEngineer,
    Soloist,
    Credits,
    Thanks,
    OnlineExtras,
    ExecutiveProducer,

    // Rating
    Rating,
    RatingAnnotation,
    ContentRating,

    // Encoding
    Copyright,
    EncodingTool,
    EncodedBy,
    PurchaseDate,

    // iTunes store
    ITunesAccount,
    ITunesAccountType,
    ITunesCountry,
    ContentId,
    ArtistId,
    PlaylistId,
    GenreId,
    ComposerId,
    Xid,

    // Audiobook (unofficial freeform)
    AudiobookSubtitle,
    AudiobookLanguage,
    Asin,
    Abridged,
}
