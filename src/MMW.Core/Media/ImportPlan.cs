using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>What to do with a track when it is muxed into the document's container.</summary>
public enum ImportAction
{
    /// <summary>Copy the samples unchanged (codec configuration is translated between containers as needed).</summary>
    Passthrough,

    /// <summary>Convert text subtitles to 3GPP timed text (lossless for plain text; styling is simplified).</summary>
    ConvertToTx3g,

    /// <summary>Convert text subtitles to SubRip text (Matroska S_TEXT/UTF8).</summary>
    ConvertToSrt,

    /// <summary>
    /// Transcode audio to AAC with the mixdown of <see cref="TrackImportOptions.Conversion"/> (requires the registered
    /// <see cref="IAudioConverterFactory"/>, i.e. FFmpeg).
    /// </summary>
    ConvertToAac,

    /// <summary>Transcode audio to AC-3 (multichannel up to 5.1; requires FFmpeg).</summary>
    ConvertToAc3,

    /// <summary>Do not import the track.</summary>
    Skip,

    /// <summary>
    /// Subler's "AAC + Passthru": the track is kept unchanged (disabled) and an AAC conversion of it is added before it
    /// (enabled, same alternate group); the original's fallback (MP4 <c>tref/fall</c>) points at the AAC track. The
    /// document is expanded into the two tracks by <see cref="TrackConversions.Expand"/>.
    /// </summary>
    AacPlusPassthrough,

    /// <summary>
    /// Subler's "AAC + AC3": like <see cref="AacPlusPassthrough"/>, but the original is converted to AC-3 instead of
    /// being copied (for DTS and other codecs Apple devices cannot play).
    /// </summary>
    AacPlusAc3,
}

/// <summary>How well a container can store a codec.</summary>
public enum TrackSupportLevel
{
    /// <summary>Stored as is.</summary>
    Passthrough,

    /// <summary>Stored after an automatic lossless conversion (e.g. SubRip → tx3g, tx3g → SubRip).</summary>
    Converted,

    /// <summary>Needs transcoding, which is not available: the track cannot be muxed until a conversion is chosen.</summary>
    NeedsConversion,

    /// <summary>The container cannot carry this codec at all.</summary>
    Unsupported,
}

/// <summary>Answer of <see cref="IMuxerFactory.CheckSupport"/>.</summary>
/// <param name="Level">Support level.</param>
/// <param name="SuggestedAction">The action to offer in the import UI.</param>
/// <param name="Reason">Explanation for <see cref="TrackSupportLevel.NeedsConversion"/>/<see cref="TrackSupportLevel.Unsupported"/>.</param>
public sealed record TrackSupport(TrackSupportLevel Level, ImportAction SuggestedAction, string? Reason = null)
{
    public static TrackSupport Passthrough { get; } = new(TrackSupportLevel.Passthrough, ImportAction.Passthrough);

    public bool CanMux => Level is TrackSupportLevel.Passthrough or TrackSupportLevel.Converted;
}

/// <summary>Import settings stored on a pending track's <see cref="TrackSource"/>.</summary>
public sealed record TrackImportOptions
{
    public ImportAction Action { get; init; } = ImportAction.Passthrough;

    /// <summary>Frame rate for raw video streams without timing (H.264/HEVC Annex B).</summary>
    public double? FrameRate { get; init; }

    /// <summary>Audio conversion settings for the conversion actions (null = <see cref="AudioConversionSettings.Default"/>).</summary>
    public AudioConversionSettings? Conversion { get; init; }
}

/// <summary>One track to take from a source file.</summary>
/// <param name="SourcePath">File the track is read from.</param>
/// <param name="TrackId">ID of the track in that file.</param>
/// <param name="Action">What to do with it.</param>
public sealed record TrackImport(string SourcePath, uint TrackId, ImportAction Action = ImportAction.Passthrough)
{
    /// <summary>Frame rate for raw H.264/HEVC streams.</summary>
    public double? FrameRate { get; init; }

    /// <summary>Audio conversion settings for the conversion actions.</summary>
    public AudioConversionSettings? Conversion { get; init; }
}

/// <summary>The tracks a save will read, grouped by source file.</summary>
public sealed class ImportPlan
{
    private readonly List<TrackImport> _imports = [];

    public IReadOnlyList<TrackImport> Imports => _imports;

    public void Add(TrackImport import) => _imports.Add(import);

    /// <summary>Distinct source files, in first-use order.</summary>
    public IEnumerable<string> SourceFiles => _imports.Select(i => i.SourcePath).Distinct(StringComparer.Ordinal);

    /// <summary>The plan of every non-chapter track of <paramref name="document"/>.</summary>
    /// <exception cref="InvalidOperationException">A track has no source.</exception>
    public static ImportPlan FromDocument(MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var plan = new ImportPlan();
        foreach (var track in document.Tracks.Where(t => t is not ChapterTrack))
        {
            var source = track.Source ?? throw new InvalidOperationException($"Track '{track.Name}' ({track.Format}) has no source file.");
            plan.Add(new TrackImport(source.Path, source.TrackId, source.Import?.Action ?? ImportAction.Passthrough)
            {
                FrameRate = source.Import?.FrameRate,
                Conversion = source.Import?.Conversion,
            });
        }

        return plan;
    }
}
