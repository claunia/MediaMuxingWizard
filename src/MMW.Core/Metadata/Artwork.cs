namespace MMW.Core.Metadata;

public enum ArtworkFormat
{
    Unknown,
    Jpeg,
    Png,
    Bmp,
    Gif,
}

/// <summary>A cover image stored in the file (MP4 <c>covr</c> item or Matroska attachment).</summary>
public sealed class Artwork
{
    public Artwork(byte[] data, ArtworkFormat format = ArtworkFormat.Unknown)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Format = format == ArtworkFormat.Unknown ? Detect(data) : format;
    }

    public byte[] Data { get; }

    public ArtworkFormat Format { get; }

    /// <summary>Matroska attachment file name, when the artwork came from one.</summary>
    public string? FileName { get; init; }

    public string MimeType => Format switch
    {
        ArtworkFormat.Png => "image/png",
        ArtworkFormat.Bmp => "image/bmp",
        ArtworkFormat.Gif => "image/gif",
        _ => "image/jpeg",
    };

    public string Extension => Format switch
    {
        ArtworkFormat.Png => ".png",
        ArtworkFormat.Bmp => ".bmp",
        ArtworkFormat.Gif => ".gif",
        _ => ".jpg",
    };

    public static ArtworkFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ArtworkFormat.Jpeg;
        if (data.Length >= 8 && data[..8].SequenceEqual("\x89PNG\r\n\x1a\n"u8))
            return ArtworkFormat.Png;
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
            return ArtworkFormat.Bmp;
        if (data.Length >= 4 && data[..4].SequenceEqual("GIF8"u8))
            return ArtworkFormat.Gif;
        return ArtworkFormat.Unknown;
    }
}
