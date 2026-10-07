using System.Collections.Frozen;
using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Metadata;

/// <summary>Describes one metadata field: name, group, value kind and, for enums, its choices.</summary>
/// <param name="Name">
/// Stable English name: used as the {Token} of file name templates, by the command line and in exports, so it is never
/// translated. Show <see cref="DisplayName"/> instead.
/// </param>
public sealed record TagDefinition(TagId Id, string Name, TagGroup Group, TagValueKind Kind, IReadOnlyList<EnumChoice>? Choices = null)
{
    /// <summary>The name in the user interface language (resource Tag_&lt;TagId&gt;; <see cref="Name"/> when missing).</summary>
    public string DisplayName => Strings.ResourceManager.GetString("Tag_" + Id, CultureInfo.CurrentUICulture) ?? Name;
}

/// <summary>One choice of an enumerated tag such as Media Kind.</summary>
/// <param name="Name">Stable English name, also stored in text-based containers (Matroska CONTENT_TYPE); never translated.</param>
public sealed record EnumChoice(int Value, string Name)
{
    /// <summary>
    /// The name in the user interface language (resource TagChoice_&lt;Name without spaces&gt;; <see cref="Name"/> when
    /// missing, as for names that are the same in every language).
    /// </summary>
    public string DisplayName =>
        Strings.ResourceManager.GetString("TagChoice_" + string.Concat(Name.Where(char.IsLetterOrDigit)), CultureInfo.CurrentUICulture) ?? Name;

    public override string ToString() => DisplayName;
}

/// <summary>Static catalog of all known tags, in the order the editor shows them.</summary>
public static class TagCatalog
{
    public static readonly IReadOnlyList<EnumChoice> MediaKinds =
    [
        new(0, "Home Video"),
        new(1, "Music"),
        new(2, "Audiobook"),
        new(6, "Music Video"),
        new(9, "Movie"),
        new(10, "TV Show"),
        new(11, "Booklet"),
        new(14, "Ringtone"),
        new(21, "Podcast"),
        new(23, "iTunes U"),
        new(27, "Alert Tone"),
    ];

    public static readonly IReadOnlyList<EnumChoice> ContentRatings =
    [
        new(0, "None"),
        new(2, "Clean"),
        new(4, "Explicit"),
    ];

    public static readonly IReadOnlyList<EnumChoice> HdVideoKinds =
    [
        new(0, "No"),
        new(1, "720p"),
        new(2, "1080p"),
        new(3, "4K"),
    ];

    public static readonly IReadOnlyList<EnumChoice> ITunesAccountTypes =
    [
        new(0, "iTunes"),
        new(1, "AOL"),
    ];

    /// <summary>Video genres offered by the genre combo box (free text is also allowed).</summary>
    public static readonly IReadOnlyList<string> VideoGenres =
    [
        "Action & Adventure", "Animation", "Anime", "Classic TV", "Classics", "Comedy", "Documentary", "Drama",
        "Fitness & Workout", "Horror", "Independent", "Kids", "Kids & Family", "Music", "Non-Fiction", "Reality TV",
        "Romance", "Sci-Fi & Fantasy", "Sports", "Teens", "Thriller", "Western",
    ];

    public const int MediaKindMovie = 9;
    public const int MediaKindTvShow = 10;
    public const int MediaKindITunesU = 23;

    public static IReadOnlyList<TagDefinition> All { get; } =
    [
        // General
        new(TagId.Name, "Name", TagGroup.General, TagValueKind.String),
        new(TagId.TrackSubtitle, "Track Sub-Title", TagGroup.General, TagValueKind.String),
        new(TagId.Artist, "Artist", TagGroup.General, TagValueKind.String),
        new(TagId.AlbumArtist, "Album Artist", TagGroup.General, TagValueKind.String),
        new(TagId.Album, "Album", TagGroup.General, TagValueKind.String),
        new(TagId.Grouping, "Grouping", TagGroup.General, TagValueKind.String),
        new(TagId.Composer, "Composer", TagGroup.General, TagValueKind.String),
        new(TagId.Comments, "Comments", TagGroup.General, TagValueKind.Text),
        new(TagId.Genre, "Genre", TagGroup.General, TagValueKind.String),
        new(TagId.ReleaseDate, "Release Date", TagGroup.General, TagValueKind.Date),
        new(TagId.TrackNumber, "Track #", TagGroup.General, TagValueKind.IntegerPair),
        new(TagId.DiskNumber, "Disk #", TagGroup.General, TagValueKind.IntegerPair),
        new(TagId.Tempo, "Tempo", TagGroup.General, TagValueKind.Integer),
        new(TagId.Compilation, "Compilation", TagGroup.General, TagValueKind.Bool),
        new(TagId.Keywords, "Keywords", TagGroup.General, TagValueKind.String),
        new(TagId.Category, "Category", TagGroup.General, TagValueKind.String),

        // Video
        new(TagId.MediaKind, "Media Kind", TagGroup.Video, TagValueKind.Enum, MediaKinds),
        new(TagId.HdVideo, "HD Video", TagGroup.Video, TagValueKind.Enum, HdVideoKinds),
        new(TagId.Gapless, "Gapless", TagGroup.Video, TagValueKind.Bool),
        new(TagId.Podcast, "Podcast", TagGroup.Video, TagValueKind.Bool),
        new(TagId.ITunesU, "iTunes U", TagGroup.Video, TagValueKind.Bool),

        // TV
        new(TagId.TvShow, "TV Show", TagGroup.TvShow, TagValueKind.String),
        new(TagId.TvNetwork, "TV Network", TagGroup.TvShow, TagValueKind.String),
        new(TagId.TvEpisodeId, "TV Episode ID", TagGroup.TvShow, TagValueKind.String),
        new(TagId.TvSeason, "TV Season", TagGroup.TvShow, TagValueKind.Integer),
        new(TagId.TvEpisodeNumber, "TV Episode #", TagGroup.TvShow, TagValueKind.Integer),

        // People
        new(TagId.Cast, "Cast", TagGroup.People, TagValueKind.StringList),
        new(TagId.Director, "Director", TagGroup.People, TagValueKind.StringList),
        new(TagId.Codirector, "Codirector", TagGroup.People, TagValueKind.StringList),
        new(TagId.Producers, "Producers", TagGroup.People, TagValueKind.StringList),
        new(TagId.Screenwriters, "Screenwriters", TagGroup.People, TagValueKind.StringList),
        new(TagId.Studio, "Studio", TagGroup.People, TagValueKind.String),

        // Descriptions
        new(TagId.Description, "Description", TagGroup.Descriptions, TagValueKind.Text),
        new(TagId.LongDescription, "Long Description", TagGroup.Descriptions, TagValueKind.Text),
        new(TagId.SeriesDescription, "Series Description", TagGroup.Descriptions, TagValueKind.Text),
        new(TagId.Lyrics, "Lyrics", TagGroup.Descriptions, TagValueKind.Text),

        // Sorting
        new(TagId.SortName, "Sort Name", TagGroup.Sorting, TagValueKind.String),
        new(TagId.SortArtist, "Sort Artist", TagGroup.Sorting, TagValueKind.String),
        new(TagId.SortAlbumArtist, "Sort Album Artist", TagGroup.Sorting, TagValueKind.String),
        new(TagId.SortAlbum, "Sort Album", TagGroup.Sorting, TagValueKind.String),
        new(TagId.SortComposer, "Sort Composer", TagGroup.Sorting, TagValueKind.String),
        new(TagId.SortTvShow, "Sort TV Show", TagGroup.Sorting, TagValueKind.String),

        // Classical
        new(TagId.WorkName, "Work Name", TagGroup.Classical, TagValueKind.String),
        new(TagId.MovementName, "Movement Name", TagGroup.Classical, TagValueKind.String),
        new(TagId.MovementNumber, "Movement Number", TagGroup.Classical, TagValueKind.Integer),
        new(TagId.MovementCount, "Movement Count", TagGroup.Classical, TagValueKind.Integer),
        new(TagId.ShowWorkAndMovement, "Show Work And Movement", TagGroup.Classical, TagValueKind.Bool),

        // Song credits
        new(TagId.SongDescription, "Song Description", TagGroup.Credits, TagValueKind.Text),
        new(TagId.ArtDirector, "Art Director", TagGroup.Credits, TagValueKind.String),
        new(TagId.Arranger, "Arranger", TagGroup.Credits, TagValueKind.String),
        new(TagId.Lyricist, "Lyricist", TagGroup.Credits, TagValueKind.String),
        new(TagId.Acknowledgement, "Acknowledgement", TagGroup.Credits, TagValueKind.String),
        new(TagId.Conductor, "Conductor", TagGroup.Credits, TagValueKind.String),
        new(TagId.LinerNotes, "Liner Notes", TagGroup.Credits, TagValueKind.Text),
        new(TagId.RecordCompany, "Record Company", TagGroup.Credits, TagValueKind.String),
        new(TagId.OriginalArtist, "Original Artist", TagGroup.Credits, TagValueKind.String),
        new(TagId.PhonogramRights, "Phonogram Rights", TagGroup.Credits, TagValueKind.String),
        new(TagId.SongProducer, "Song Producer", TagGroup.Credits, TagValueKind.String),
        new(TagId.Performer, "Performer", TagGroup.Credits, TagValueKind.String),
        new(TagId.Publisher, "Publisher", TagGroup.Credits, TagValueKind.String),
        new(TagId.SoundEngineer, "Sound Engineer", TagGroup.Credits, TagValueKind.String),
        new(TagId.Soloist, "Soloist", TagGroup.Credits, TagValueKind.String),
        new(TagId.Credits, "Credits", TagGroup.Credits, TagValueKind.String),
        new(TagId.Thanks, "Thanks", TagGroup.Credits, TagValueKind.String),
        new(TagId.OnlineExtras, "Online Extras", TagGroup.Credits, TagValueKind.String),
        new(TagId.ExecutiveProducer, "Executive Producer", TagGroup.Credits, TagValueKind.String),

        // Rating
        new(TagId.Rating, "Rating", TagGroup.Rating, TagValueKind.Rating),
        new(TagId.RatingAnnotation, "Rating Annotation", TagGroup.Rating, TagValueKind.String),
        new(TagId.ContentRating, "Content Rating", TagGroup.Rating, TagValueKind.Enum, ContentRatings),

        // Encoding
        new(TagId.Copyright, "Copyright", TagGroup.Encoding, TagValueKind.String),
        new(TagId.EncodingTool, "Encoding Tool", TagGroup.Encoding, TagValueKind.String),
        new(TagId.EncodedBy, "Encoded By", TagGroup.Encoding, TagValueKind.String),
        new(TagId.PurchaseDate, "Purchase Date", TagGroup.Encoding, TagValueKind.Date),

        // Store
        new(TagId.ITunesAccount, "iTunes Account", TagGroup.Store, TagValueKind.String),
        new(TagId.ITunesAccountType, "iTunes Account Type", TagGroup.Store, TagValueKind.Enum, ITunesAccountTypes),
        new(TagId.ITunesCountry, "iTunes Country", TagGroup.Store, TagValueKind.Integer),
        new(TagId.ContentId, "Content ID", TagGroup.Store, TagValueKind.Integer),
        new(TagId.ArtistId, "Artist ID", TagGroup.Store, TagValueKind.Integer),
        new(TagId.PlaylistId, "Playlist ID", TagGroup.Store, TagValueKind.Integer),
        new(TagId.GenreId, "Genre ID", TagGroup.Store, TagValueKind.Integer),
        new(TagId.ComposerId, "Composer ID", TagGroup.Store, TagValueKind.Integer),
        new(TagId.Xid, "XID", TagGroup.Store, TagValueKind.String),

        // Audiobook
        new(TagId.AudiobookSubtitle, "Subtitle", TagGroup.Audiobook, TagValueKind.String),
        new(TagId.AudiobookLanguage, "Language", TagGroup.Audiobook, TagValueKind.String),
        new(TagId.Asin, "ASIN", TagGroup.Audiobook, TagValueKind.String),
        new(TagId.Abridged, "Abridged", TagGroup.Audiobook, TagValueKind.Bool),
    ];

    private static readonly FrozenDictionary<TagId, TagDefinition> s_byId = All.ToFrozenDictionary(d => d.Id);

    private static readonly FrozenDictionary<TagId, int> s_order =
        All.Select((d, i) => (d.Id, i)).ToFrozenDictionary(x => x.Id, x => x.i);

    public static TagDefinition Get(TagId id) => s_byId[id];

    /// <summary>Display order of a tag in the editor.</summary>
    public static int OrderOf(TagId id) => s_order[id];

    public static string GroupDisplayName(TagGroup group) => group switch
    {
        TagGroup.General => Strings.TagGroup_General,
        TagGroup.Video => Strings.TagGroup_Video,
        TagGroup.TvShow => Strings.TagGroup_TvShow,
        TagGroup.People => Strings.TagGroup_People,
        TagGroup.Descriptions => Strings.TagGroup_Descriptions,
        TagGroup.Sorting => Strings.TagGroup_Sorting,
        TagGroup.Classical => Strings.TagGroup_Classical,
        TagGroup.Credits => Strings.TagGroup_Credits,
        TagGroup.Rating => Strings.TagGroup_Rating,
        TagGroup.Encoding => Strings.TagGroup_Encoding,
        TagGroup.Store => Strings.TagGroup_Store,
        TagGroup.Audiobook => Strings.TagGroup_Audiobook,
        _ => group.ToString(),
    };

    /// <summary>Tags added by the built-in "Movie" set.</summary>
    public static readonly IReadOnlyList<TagId> MovieSet =
    [
        TagId.Name, TagId.Artist, TagId.Genre, TagId.ReleaseDate, TagId.Description, TagId.LongDescription,
        TagId.Rating, TagId.Studio, TagId.Cast, TagId.Director, TagId.Producers, TagId.Screenwriters,
        TagId.MediaKind, TagId.HdVideo,
    ];

    /// <summary>Tags added by the built-in "TV Show" set.</summary>
    public static readonly IReadOnlyList<TagId> TvShowSet =
    [
        TagId.Name, TagId.Artist, TagId.AlbumArtist, TagId.Album, TagId.Genre, TagId.ReleaseDate,
        TagId.TrackNumber, TagId.DiskNumber, TagId.TvShow, TagId.TvEpisodeId, TagId.TvSeason,
        TagId.TvEpisodeNumber, TagId.TvNetwork, TagId.Description, TagId.LongDescription,
        TagId.SeriesDescription, TagId.Rating, TagId.Studio, TagId.Cast, TagId.Director, TagId.Producers,
        TagId.Screenwriters, TagId.MediaKind, TagId.HdVideo,
    ];
}
