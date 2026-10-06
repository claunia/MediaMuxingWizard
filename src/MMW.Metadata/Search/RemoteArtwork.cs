namespace MMW.Metadata.Search;

/// <summary>Kind of a remote artwork image offered by a provider.</summary>
public enum ArtworkKind
{
    /// <summary>Movie or series poster.</summary>
    Poster,

    /// <summary>Season poster.</summary>
    Season,

    /// <summary>Episode still / screen capture.</summary>
    Episode,

    /// <summary>Background / fan art.</summary>
    Backdrop,

    /// <summary>Square artwork (iTunes style).</summary>
    Square,

    /// <summary>Wide rectangular artwork (banners, 16:9 cover art).</summary>
    Rectangle,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>An artwork image that can be downloaded from a provider.</summary>
/// <param name="ThumbnailUrl">Small preview image for the artwork picker.</param>
/// <param name="FullUrl">Full resolution image to embed.</param>
/// <param name="Kind">What the image shows.</param>
/// <param name="Provider">Name of the provider that offered the image.</param>
/// <param name="Width">Full image width, when known.</param>
/// <param name="Height">Full image height, when known.</param>
public sealed record RemoteArtwork(Uri ThumbnailUrl, Uri FullUrl, ArtworkKind Kind, string Provider, int? Width = null, int? Height = null)
{
    /// <summary>Language of the artwork text, when the provider reports it (ISO code).</summary>
    public string? Language { get; init; }

    /// <summary>Season the artwork belongs to (season posters).</summary>
    public int? Season { get; init; }
}
