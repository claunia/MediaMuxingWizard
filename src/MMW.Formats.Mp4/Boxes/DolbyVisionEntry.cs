using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>
/// Dolby Vision signalling of a visual sample entry, per "Dolby Vision Streams Within the ISO Base Media File
/// Format": the configuration box (dvcC/dvvC/dvwC by profile), the sample entry type (dvh1/dvhe/dva1/dvav/dav1 or the
/// base codec's) and, for single-track dual-layer streams, the enhancement layer configuration box (hvcE/avcE).
/// </summary>
public static class DolbyVisionEntry
{
    /// <summary>Write 'av01' instead of 'dav1' for AV1 profile 10 without a compatible base layer (FFmpeg cannot read 'dav1').</summary>
    public static bool Av1UsesAv01 { get; set; }

    /// <summary>
    /// Brings <paramref name="entry"/> in line with <paramref name="record"/> (or, when null, with the entry's own
    /// configuration box). Returns the configuration that applies, or null when the entry has no Dolby Vision.
    /// </summary>
    public static DolbyVisionInfo? Apply(Box entry, byte[]? record = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Children is null)
            return null;
        var existing = entry.Children.FirstOrDefault(c => c.Type is "dvcC" or "dvvC" or "dvwC");
        record ??= existing?.Payload;
        if (record is not { Length: >= 5 })
            return null;

        var info = DolbyVision.ParseConfigurationRecord(record);
        var boxType = DolbyVision.Mp4BoxType(info.Profile);
        if (existing is null || existing.Type != boxType || !existing.Payload.AsSpan().SequenceEqual(record))
        {
            entry.Children.RemoveAll(c => c.Type is "dvcC" or "dvvC" or "dvwC");
            var after = entry.Children.FindLastIndex(c => c.Type is "avcC" or "hvcC" or "av1C");
            entry.Children.Insert(after + 1, new Box(boxType, record));
        }

        entry.Type = DolbyVision.Mp4SampleEntryType(entry.Type, info, Av1UsesAv01);

        // hvcE/avcE: the spec leaves their content open; like GPAC, they repeat the base configuration record.
        entry.Children.RemoveAll(c => c.Type is "hvcE" or "avcE");
        if (DolbyVision.NeedsEnhancementLayerConfig(info) &&
            entry.Children.FirstOrDefault(c => c.Type is "hvcC" or "avcC") is { } config)
        {
            var at = entry.Children.FindIndex(c => c.Type is "dvcC" or "dvvC" or "dvwC");
            entry.Children.Insert(at + 1, new Box(config.Type == "hvcC" ? "hvcE" : "avcE", config.Payload.ToArray()));
        }

        return info;
    }
}
