using System.Globalization;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Elementary;
using MMW.Formats.MpegTs;
using MMW.Formats.Matroska.Media;
using MMW.Formats.Mp4.Media;
using MMW.Media.Conversion;
using MMW.Ocr;

namespace MMW.Media.Remux;

/// <summary>Registers every demuxer and muxer of the application with <see cref="MediaFormatRegistry"/>.</summary>
public static class MediaRemux
{
    /// <summary>
    /// Registers the MP4, Matroska and elementary stream formats, the FFmpeg audio converter and the bitmap subtitle
    /// OCR converter (idempotent). The MP4 and Matroska handlers register their own formats; call this at start-up so
    /// files imported from elementary streams can be remuxed and conversions are offered. FFmpeg and Tesseract are
    /// loaded lazily (a missing library only disables the corresponding conversion actions). To use another tessdata
    /// directory, call <see cref="SubtitleOcr.Register"/> with a <see cref="TessdataManager"/> afterwards.
    /// </summary>
    public static void EnsureRegistered()
    {
        Mp4MediaFormat.Register();
        MatroskaMediaFormat.Register();
        ElementaryFormat.Register();
        TsFormat.Register();
        MediaConversion.Register();
        SubtitleOcr.Register();
    }

    /// <summary>Every file extension that can be inspected for tracks to import.</summary>
    public static IReadOnlyList<string> ImportExtensions { get; } =
        [.. ContainerKinds.Mp4Extensions, .. ContainerKinds.MatroskaExtensions, .. ElementaryFormat.Extensions, .. TsFormat.Extensions];
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

    /// <summary>True when the container cannot store the track as is: it needs one of the conversion actions.</summary>
    public bool ConversionRequired => Support.Level == TrackSupportLevel.NeedsConversion;

    /// <summary>True when the audio converter (FFmpeg) can convert this track (the conversion actions are offered).</summary>
    public bool CanConvert { get; init; }

    /// <summary>True when this bitmap subtitle track can be converted to text by OCR (Tesseract and FFmpeg are available).</summary>
    public bool CanOcr { get; init; }

    /// <summary>
    /// The actions to offer, with Subler's labels ("Passthru", "AAC - Dolby Pro Logic II", …, "AAC + Passthru",
    /// "AAC + AC3", "Skip"/"Not available"); the last entry is always <see cref="ImportAction.Skip"/>.
    /// </summary>
    public IReadOnlyList<ImportChoice> Choices { get; init; } = [];

    /// <summary>
    /// The chosen entry of <see cref="Choices"/>. Setting it updates <see cref="Action"/> and the mixdown of
    /// <see cref="Conversion"/>.
    /// </summary>
    public ImportChoice? Choice
    {
        get => field;
        set
        {
            field = value;
            if (value is null)
                return;
            Action = value.Action;
            Conversion = value.SettingsFrom(Conversion ?? ConversionDefaults.Settings);
            Ocr = value.OcrFrom(Ocr);
        }
    }

    /// <summary>The action to take (initially the suggested one).</summary>
    public ImportAction Action { get; set; }

    /// <summary>Conversion settings for the conversion actions (bitrate, DRC, mixdown); null for other actions.</summary>
    public AudioConversionSettings? Conversion { get; set; }

    /// <summary>
    /// OCR settings (recognition language) when an OCR choice is selected; null otherwise. Set
    /// <see cref="OcrOptions.Language"/> to override the language derived from <see cref="Language"/>.
    /// </summary>
    public OcrOptions? Ocr { get; set; }

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
            config = WithDetectedDolbyVision(source, config);
            config = WithDetectedHdr10Plus(source, config);
            config = WithStreamInfo(source, config);
            config = WithDtsDescription(source, config);
            var support = muxer.CheckSupport(config);
            var canConvert = ConversionDefaults.CanConvert(config);
            var canOcr = ConversionDefaults.CanOcr(config);
            var choices = ConversionDefaults.Choices(config, support, target, canConvert, canOcr);
            var choice = ConversionDefaults.Suggest(config, support, target, choices);
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
                CanConvert = canConvert,
                CanOcr = canOcr,
                Choices = choices,
                Choice = choice,
                RequiresFrameRate = needsRate && config.Kind == TrackKind.Video,
                Selected = support.CanMux || ConversionDefaults.IsConversion(choice.Action) || choice.Ocr,
            });
        }

        return result;
    }

    /// <summary>
    /// Dolby Vision RPUs in an HEVC/AV1 track that has no configuration record (raw elementary streams, or files
    /// muxed without it): the record is rebuilt from the bitstream so the imported track is signalled correctly.
    /// </summary>
    private static CodecConfig WithDetectedDolbyVision(ISampleSource source, CodecConfig config)
    {
        if (config.Kind != TrackKind.Video || config.Codec is not (CodecType.Hevc or CodecType.Av1) || config.DolbyVisionConfig is not null)
            return config;
        try
        {
            if (DolbyVisionDetector.Detect(source, config.Color) is not { } detection)
                return config;
            AppLog.Info($"Dolby Vision {detection.ProfileName} (level {detection.Level}) found in the bitstream of track {source.TrackId}; its configuration was rebuilt.");
            return config with { DolbyVisionConfig = detection.ConfigurationRecord };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            AppLog.Debug($"Dolby Vision check of track {source.TrackId} failed: {ex.Message}");
            return config;
        }
    }

    /// <summary>DTS product (DTS-HD MA, DTS:X …), channels and sample rate from the first access units.</summary>
    private static CodecConfig WithDtsDescription(ISampleSource source, CodecConfig config)
    {
        if (!DtsDetector.CanScan(config) || config.AudioProfile.Length > 0)
            return config;
        try
        {
            return DtsDetector.Detect(source) is { } header ? DtsDetector.Apply(config, header) : config;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            AppLog.Debug($"DTS check of track {source.TrackId} failed: {ex.Message}");
            return config;
        }
    }

    /// <summary>Colour and static HDR10 metadata of the bitstream (used when the source container lacks them).</summary>
    private static CodecConfig WithStreamInfo(ISampleSource source, CodecConfig config)
    {
        if (!VideoStreamInfoScanner.CanScan(config))
            return config;
        try
        {
            var info = VideoStreamInfoScanner.Scan(source);
            if (info.IsEmpty)
                return config;
            config = config with { StreamColor = info.Color, StreamHdr = info.Hdr };
            // A raw stream has no container: its colour is the bitstream's, including the alternative transfer
            // characteristics SEI (HLG signalled as BT.2020 SDR) that the demuxer's VUI reading does not see.
            return source.Config.Native is null && info.Color.IsSpecified ? config with { Color = info.Color } : config;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            AppLog.Debug($"Colour scan of track {source.TrackId} failed: {ex.Message}");
            return config;
        }
    }

    /// <summary>HDR10+ dynamic metadata in the first frames of a video track (bitstream or Matroska block additions).</summary>
    private static CodecConfig WithDetectedHdr10Plus(ISampleSource source, CodecConfig config)
    {
        if (config.Kind != TrackKind.Video || config.Hdr10Plus)
            return config;
        try
        {
            return Hdr10PlusDetector.Detect(source) ? config with { Hdr10Plus = true } : config;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            AppLog.Debug($"HDR10+ check of track {source.TrackId} failed: {ex.Message}");
            return config;
        }
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

            // "AAC + Passthru" / "AAC + AC3": the AAC track is inserted before the original.
            if (item.Action is ImportAction.AacPlusPassthrough or ImportAction.AacPlusAc3)
                added.AddRange(TrackConversions.SetAction(document, track, item.Action, item.Conversion));
            added.Add(track);
        }

        return added;
    }

    /// <summary>
    /// The track's Dolby Vision record; for raw streams imported with a chosen frame rate the level is recomputed with
    /// it (the bitstream scan used the stream's timing, or assumed one).
    /// </summary>
    private static byte[]? DolbyVisionRecordFor(ImportableTrack item)
    {
        if (item.Config.DolbyVisionConfig is not { Length: >= 5 } record)
            return null;
        if (item.SourceContainer != ContainerKind.Unknown || item.FrameRate is not > 0 || item.Config.Width <= 0 || item.Config.Height <= 0)
            return record;
        return DolbyVision.WithLevel(record, DolbyVision.Level(item.Config.Width, item.Config.Height, DolbyVision.NominalFrameRate(item.FrameRate.Value)));
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
                    ProfileLevel = ProfileLevel(c),
                    DisplayWidth = Math.Round(c.Width * par),
                    DisplayHeight = c.Height,
                    ParNumerator = c.ParNumerator,
                    ParDenominator = c.ParDenominator,
                    Color = c.Color,
                    Hdr = c.Hdr,
                    FrameRate = item.FrameRate ?? c.FrameRate,
                    DolbyVisionRecord = DolbyVisionRecordFor(item), // also carries detected records to the muxer
                    Hdr10Plus = c.Hdr10Plus,
                    StreamInfo = new VideoStreamInfo(c.StreamColor, c.StreamHdr),
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
                    Profile = c.AudioProfile,
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
        var conversion = ConversionDefaults.IsConversion(item.Action) ? item.Conversion ?? ConversionDefaults.Settings : null;
        var ocr = SubtitleConversions.IsBitmap(c.Codec) && SubtitleConversions.Target(item.Action) is not null ? item.Ocr ?? OcrOptions.Default : null;
        track.Source = new TrackSource(item.SourcePath, item.SourceContainer, item.TrackId)
        {
            Import = new TrackImportOptions { Action = item.Action, FrameRate = item.FrameRate, Conversion = conversion, Ocr = ocr },
        };

        // A track converted by OCR is shown with the text format it will have once saved.
        if (ocr is not null && SubtitleConversions.Target(item.Action) is { } ocrTarget)
        {
            var output = SubtitleConversions.PredictOutput(c, ocrTarget);
            track.Format = output.FormatName;
            track.CodecId = output.SourceCodecId;
            track.FormatDetails = "Text (OCR)";
            track.Timescale = output.Timescale;
        }

        // A converted track is shown with the codec, channels and rate it will have once saved.
        if (conversion is not null && track is AudioTrack audio && item.Action is ImportAction.ConvertToAac or ImportAction.ConvertToAc3)
        {
            var output = conversion.PredictOutput(c, item.Action == ImportAction.ConvertToAac ? AudioConversionTarget.Aac : AudioConversionTarget.Ac3);
            audio.Channels = output.Channels;
            audio.SampleRate = output.SampleRate;
            audio.ChannelLayout = TrackConversions.ChannelName(output.Channels);
            audio.IsAtmos = false;
            track.Format = output.FormatName;
            track.CodecId = output.SourceCodecId;
            track.FormatDetails = TrackConversions.Details(output.Channels, output.SampleRate);
            track.Timescale = output.Timescale;
        }

        return track;
    }

    /// <summary>"Profile@Level" of an H.264, HEVC, VVC or EVC configuration record; empty for other codecs.</summary>
    public static string ProfileLevel(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Extradata is not { Length: > 0 } record ? string.Empty
            : config.Codec switch
            {
                CodecType.H264 => H264.ProfileLevel(record),
                CodecType.Hevc => Hevc.ProfileLevel(record),
                CodecType.Vvc => Vvc.ProfileLevel(record),
                CodecType.Evc => Evc.ProfileLevel(record),
                _ => string.Empty,
            };
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
                if (ProfileLevel(config) is { Length: > 0 } profile)
                    parts.Add(profile);
                if (config.DolbyVisionConfig is not null)
                    parts.Add("Dolby Vision");
                var color = config.EffectiveColor;
                if (config.Hdr10Plus)
                    parts.Add("HDR10+");
                else if (config.DolbyVisionConfig is null && (config.EffectiveHdr is not null || color.Transfer is 16 or 18))
                    parts.Add(color.Transfer == 18 ? "HLG" : "HDR10");
                break;
            case TrackKind.Audio:
                if (config.AudioProfile.Length > 0)
                    parts.Add(config.AudioProfile);
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
