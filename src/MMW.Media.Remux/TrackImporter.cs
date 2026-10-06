using System.Globalization;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Elementary;
using MMW.Formats.Matroska.Media;
using MMW.Formats.Mp4.Media;

namespace MMW.Media.Remux;

/// <summary>Registers every demuxer and muxer of the application with <see cref="MediaFormatRegistry"/>.</summary>
public static class MediaRemux
{
    /// <summary>
    /// Registers the MP4, Matroska and elementary stream formats (idempotent). The MP4 and Matroska handlers register
    /// their own formats; call this at start-up so files imported from elementary streams can be remuxed too.
    /// </summary>
    public static void EnsureRegistered()
    {
        Mp4MediaFormat.Register();
        MatroskaMediaFormat.Register();
        ElementaryFormat.Register();
    }

    /// <summary>Every file extension that can be inspected for tracks to import.</summary>
    public static IReadOnlyList<string> ImportExtensions { get; } =
        [.. ContainerKinds.Mp4Extensions, .. ContainerKinds.MatroskaExtensions, .. ElementaryFormat.Extensions];
}

/// <summary>A track of a file that can be added to a document.</summary>
public sealed class ImportableTrack
{
    public required string SourcePath { get; init; }

    /// <summary>Format of the source file ("MP4", "Matroska", "SubRip", …).</summary>
    public required string SourceFormat { get; init; }

    public required ContainerKind SourceContainer { get; init; }

    /// <summary>ID of the track in the source file.</summary>
    public required uint TrackId { get; init; }

    public required CodecConfig Config { get; init; }

    public TrackKind Kind => Config.Kind;

    /// <summary>Short codec name ("H.264", "AC-3", "SRT", …).</summary>
    public string Format => Config.FormatName;

    /// <summary>Details such as "1920×1080, 23.976 fps" or "5.1, 48 kHz".</summary>
    public required string Details { get; init; }

    public required TimeSpan Duration { get; init; }

    public string Language { get; set; } = "und";

    public string Name { get; set; } = string.Empty;

    /// <summary>How the document's container can store the track.</summary>
    public required TrackSupport Support { get; init; }

    /// <summary>True when the track can only be imported after a conversion that is not available yet.</summary>
    public bool ConversionRequired => Support.Level == TrackSupportLevel.NeedsConversion;

    /// <summary>The action to take (initially the suggested one).</summary>
    public ImportAction Action { get; set; }

    /// <summary>Raw H.264/HEVC without timing: the frame rate the UI should ask for.</summary>
    public bool RequiresFrameRate { get; init; }

    /// <summary>Frame rate for raw video streams (null = the stream's own, or 25 fps when it has none).</summary>
    public double? FrameRate { get; set; }

    /// <summary>Whether the track is ticked in the import dialog (initially: when it can be muxed as is).</summary>
    public bool Selected { get; set; }

    public override string ToString() => $"{TrackId}: {Kind} {Format} {Details} [{Support.Level}]";
}

/// <summary>Lists the tracks of files and adds them to documents as pending tracks (written on the next save).</summary>
public static class TrackImporter
{
    static TrackImporter() => MediaRemux.EnsureRegistered();

    /// <summary>
    /// Lists the tracks of <paramref name="path"/> (MP4/MOV/M4A, MKV/MKA/MKS/WebM, raw H.264/HEVC, ADTS AAC,
    /// AC-3/E-AC-3, SRT/ASS/SSA/WebVTT) with the action suggested for a document of kind <paramref name="target"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is not in a supported format.</exception>
    public static Task<IReadOnlyList<ImportableTrack>> InspectAsync(string path, ContainerKind target = ContainerKind.Mp4, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run<IReadOnlyList<ImportableTrack>>(() => Inspect(path, target), cancellationToken);
    }

    private static List<ImportableTrack> Inspect(string path, ContainerKind target)
    {
        var full = Path.GetFullPath(path);
        var muxer = MediaFormatRegistry.GetMuxer(target == ContainerKind.Unknown ? ContainerKind.Mp4 : target)
                    ?? throw new NotSupportedException($"No muxer is registered for {target}.");
        using var demuxer = MediaFormatRegistry.OpenDemuxer(full);
        var needsRate = ElementaryFormat.RequiresFrameRate(demuxer);
        var result = new List<ImportableTrack>();
        foreach (var source in demuxer.Tracks)
        {
            var config = source.Config;
            if (config.Kind is TrackKind.Chapters)
                continue;
            var support = muxer.CheckSupport(config);
            result.Add(new ImportableTrack
            {
                SourcePath = full,
                SourceFormat = demuxer.FormatName,
                SourceContainer = demuxer.Container,
                TrackId = source.TrackId,
                Config = config,
                Details = Describe(config),
                Duration = source.Duration > TimeSpan.Zero ? source.Duration : demuxer.Duration,
                Language = string.IsNullOrWhiteSpace(config.Language) ? "und" : config.Language,
                Name = config.Name,
                Support = support,
                Action = support.SuggestedAction,
                RequiresFrameRate = needsRate && config.Kind == TrackKind.Video,
                Selected = support.CanMux,
            });
        }

        return result;
    }

    /// <summary>
    /// Appends the selected tracks to <paramref name="document"/> as pending tracks (ID 0, <see cref="Track.Source"/>
    /// pointing at the imported file). They are muxed when the document is saved.
    /// </summary>
    /// <returns>The tracks that were added.</returns>
    public static IReadOnlyList<Track> AddToDocument(MediaDocument document, IEnumerable<ImportableTrack> selected)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        var added = new List<Track>();
        foreach (var item in selected)
        {
            if (item.Action == ImportAction.Skip)
                continue;
            var track = CreateTrack(item);
            var existingOfKind = document.Tracks.Where(t => t.Kind == track.Kind && t is not ChapterTrack).ToList();
            track.Enabled = existingOfKind.Count == 0 || track.Kind == TrackKind.Video && !existingOfKind.Any(t => t.Enabled);
            track.IsDefault = track.Enabled;
            if (document.Container == ContainerKind.Mp4 && track.Kind is TrackKind.Audio or TrackKind.Subtitle)
            {
                var group = existingOfKind.Select(t => t.AlternateGroup).FirstOrDefault(g => g > 0);
                track.AlternateGroup = group > 0 ? group : track.Kind == TrackKind.Audio ? 1 : 2;
            }

            var chapterIndex = document.Tracks.ToList().FindIndex(t => t is ChapterTrack);
            if (chapterIndex >= 0)
                document.Tracks.Insert(chapterIndex, track);
            else
                document.Tracks.Add(track);
            added.Add(track);
        }

        return added;
    }

    private static Track CreateTrack(ImportableTrack item)
    {
        var c = item.Config;
        Track track;
        switch (c.Kind)
        {
            case TrackKind.Video:
            {
                var par = c.ParDenominator > 0 ? (double)c.ParNumerator / c.ParDenominator : 1;
                track = new VideoTrack
                {
                    PixelWidth = c.Width,
                    PixelHeight = c.Height,
                    DisplayWidth = Math.Round(c.Width * par),
                    DisplayHeight = c.Height,
                    ParNumerator = c.ParNumerator,
                    ParDenominator = c.ParDenominator,
                    Color = c.Color,
                    Hdr = c.Hdr,
                    FrameRate = item.FrameRate ?? c.FrameRate,
                    DolbyVision = c.DolbyVisionConfig is { Length: >= 5 } dv ? Formats.Mp4.Boxes.CodecInfo.ParseDolbyVisionRecord(dv) : null,
                };
                break;
            }

            case TrackKind.Audio:
                track = new AudioTrack
                {
                    Channels = c.Channels,
                    SampleRate = c.SampleRate,
                    ChannelLayout = Formats.Mp4.Boxes.CodecInfo.ChannelDescription(c.Channels),
                    IsAtmos = c.IsAtmos,
                };
                break;
            case TrackKind.Subtitle:
                track = new SubtitleTrack { Width = c.SubtitleWidth, Height = c.SubtitleHeight };
                break;
            case TrackKind.ClosedCaption:
                track = new ClosedCaptionTrack();
                break;
            default:
                track = new OtherTrack();
                break;
        }

        track.Id = 0;
        track.Name = item.Name;
        track.Language = item.Language;
        track.Format = c.FormatName;
        track.CodecId = c.SourceCodecId;
        track.FormatDetails = item.Details;
        track.Duration = item.Duration;
        track.Timescale = c.Timescale;
        track.Source = new TrackSource(item.SourcePath, item.SourceContainer, item.TrackId)
        {
            Import = new TrackImportOptions { Action = item.Action, FrameRate = item.FrameRate },
        };
        return track;
    }

    /// <summary>Human-readable details of a codec configuration.</summary>
    public static string Describe(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var parts = new List<string>();
        switch (config.Kind)
        {
            case TrackKind.Video:
                if (config.Width > 0)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{config.Width}×{config.Height}"));
                if (config.FrameRate > 0)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{config.FrameRate:0.###} fps"));
                if (config.DolbyVisionConfig is not null)
                    parts.Add("Dolby Vision");
                else if (config.Hdr is not null || config.Color.Transfer is 16 or 18)
                    parts.Add(config.Color.Transfer == 18 ? "HLG" : "HDR10");
                break;
            case TrackKind.Audio:
                if (config.Channels > 0)
                    parts.Add(Formats.Mp4.Boxes.CodecInfo.ChannelDescription(config.Channels));
                if (config.SampleRate > 0)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{config.SampleRate / 1000.0:0.###} kHz"));
                if (config.IsAtmos)
                    parts.Add("Atmos");
                break;
            case TrackKind.Subtitle when CodecNames.IsText(config.Codec):
                parts.Add("Text");
                break;
        }

        return string.Join(", ", parts);
    }
}
