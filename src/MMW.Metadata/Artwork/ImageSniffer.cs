using MMW.Core.Metadata;

namespace MMW.Metadata.Artwork;

/// <summary>Detects image formats from their signature bytes.</summary>
/// <remarks>
/// Kept separate from <see cref="MMW.Core.Metadata.Artwork.Detect"/> because that implementation compares against the
/// UTF-8 literal <c>"\x89PNG…"u8</c>, which encodes 0x89 as two bytes and therefore never matches a PNG file.
/// </remarks>
public static class ImageSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns the format of <paramref name="data"/>, or <see cref="ArtworkFormat.Unknown"/>.</summary>
    public static ArtworkFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ArtworkFormat.Jpeg;
        if (data.StartsWith(PngSignature))
            return ArtworkFormat.Png;
        if (data.Length >= 4 && data[..4].SequenceEqual("GIF8"u8))
            return ArtworkFormat.Gif;
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
            return ArtworkFormat.Bmp;
        return ArtworkFormat.Unknown;
    }
}
