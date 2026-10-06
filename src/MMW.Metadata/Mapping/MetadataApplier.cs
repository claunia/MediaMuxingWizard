using MMW.Core.Metadata;
using MMW.Core.Model;

namespace MMW.Metadata.Mapping;

/// <summary>Options for <see cref="MetadataApplier.Apply"/>.</summary>
public sealed record ApplyOptions
{
    /// <summary>Replace existing tag values (otherwise only missing tags are filled). Default true.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// When true (default) tags of <see cref="MappedTags"/> for which the result has no value keep their current
    /// value. When false and <see cref="Overwrite"/> is set, such tags are removed so stale values from an earlier
    /// match do not survive.
    /// </summary>
    public bool KeepEmpty { get; init; } = true;

    /// <summary>Allow the "4K" HD Video value; otherwise videos larger than 1920x1088 are flagged 1080p.</summary>
    public bool Autodetect4K { get; init; }

    /// <summary>Replace the existing artwork with the result's artwork (when it has any) instead of appending.</summary>
    public bool ReplaceArtworks { get; init; }

    /// <summary>Tags the metadata map writes (see <see cref="MetadataMap.MappedTags"/>); used with <see cref="KeepEmpty"/>.</summary>
    public IReadOnlyCollection<TagId>? MappedTags { get; init; }
}

/// <summary>Applies a search result (already mapped to tags) to a document, Subler style.</summary>
public static class MetadataApplier
{
    /// <summary>Merges <paramref name="fromResult"/> into the document and sets HD Video from the main video track.</summary>
    public static void Apply(MediaDocument doc, MetadataSet fromResult, ApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(fromResult);
        options ??= new ApplyOptions();

        var incoming = fromResult.Clone();
        if (doc.MainVideo is { PixelWidth: > 0, PixelHeight: > 0 } video)
        {
            var hd = HdVideoKind(video.PixelWidth, video.PixelHeight, options.Autodetect4K);
            if (hd > 0)
                incoming.Set(TagId.HdVideo, hd);
        }

        if (!options.KeepEmpty && options.Overwrite && options.MappedTags is { } mapped)
        {
            foreach (var tag in mapped)
            {
                if (!incoming.Contains(tag))
                    doc.Metadata.Remove(tag);
            }
        }

        doc.Metadata.Merge(incoming, options.Overwrite, options.ReplaceArtworks);
    }

    /// <summary>
    /// iTunes HD Video value for a resolution (Subler's rules): 0 = SD; 1 = 720p when width ≥ 1280 or height ≥ 720;
    /// 2 = 1080p above 1280x720 up to 1920x1088; 3 = 4K above 1920x1088 when <paramref name="autodetect4K"/> (else 2).
    /// </summary>
    public static int HdVideoKind(int width, int height, bool autodetect4K)
    {
        if (width > 1920 || height > 1088)
            return autodetect4K ? 3 : 2;
        if (width > 1280 || height > 720)
            return 2;
        if (width >= 1280 || height >= 720)
            return 1;
        return 0;
    }
}
