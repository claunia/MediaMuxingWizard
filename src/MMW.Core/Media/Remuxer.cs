using System.Globalization;
using MMW.Core.Diagnostics;
using MMW.Core.Media.Subtitles;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>Decides when a save must rebuild the file instead of editing it in place.</summary>
public static class RemuxPolicy
{
    /// <summary>Container family of the save destination (falls back to the document's).</summary>
    public static ContainerKind TargetKind(MediaDocument document, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        var path = options.OutputPath ?? document.Path;
        var kind = path is null ? ContainerKind.Unknown : ContainerKinds.FromPath(path);
        return kind == ContainerKind.Unknown ? document.Container : kind;
    }

    /// <summary>
    /// True when a track is pending (not yet written), its samples live in a file other than the document's, or it
    /// is to be converted (<see cref="ConversionDefaults.IsConversion"/>).
    /// </summary>
    public static bool HasImportedTracks(MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tracks.Any(t => t is not ChapterTrack &&
                                        (t.IsPending || (t.Source is { } s && (document.Path is null || !SamePath(s.Path, document.Path)))))
               || TrackConversions.HasConversions(document);
    }

    /// <summary>
    /// True when the document's file is of another container family than <paramref name="target"/> (a Matroska file
    /// whose output was switched to MP4): only a remux can write it.
    /// </summary>
    public static bool ChangesContainer(MediaDocument document, ContainerKind target)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Path is { } path && ContainerKinds.FromPath(path) is var kind && kind != ContainerKind.Unknown && kind != target;
    }

    /// <summary>True when a track has a start offset to apply (only possible by rewriting the timeline).</summary>
    public static bool HasOffsets(MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tracks.Any(t => t is not ChapterTrack && t.StartOffset != TimeSpan.Zero);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// Rebuilds a document into a new file: every track is demuxed from its <see cref="TrackSource"/> (the document's
/// own file or an imported one), muxed into the destination container in a temporary file next to the destination,
/// and the temporary file then replaces the destination.
/// </summary>
public static class Remuxer
{
    /// <summary>Remuxes <paramref name="document"/> into <paramref name="target"/> and points the document at the result.</summary>
    /// <exception cref="NotSupportedException">No muxer is registered for the target, or a track cannot be stored in it.</exception>
    public static async Task SaveAsync(MediaDocument document, SaveOptions options, ContainerKind target, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        var output = Path.GetFullPath(options.OutputPath ?? document.Path ?? throw new InvalidOperationException(Strings.Error_NoDestinationPath));
        var factory = MediaFormatRegistry.GetMuxer(target) ??
                      throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_RemuxComponentMissing, target));

        // "AAC + Passthru" / "AAC + AC3" become two document tracks (on the caller's context: the document is changed).
        TrackConversions.Expand(document);
        var tracks = document.Tracks.Where(t => t is not ChapterTrack && t.Source?.Import?.Action != ImportAction.Skip).ToList();

        // Bitmap subtitles converted by OCR keep their forced flags: found by a decoding pass (no OCR) before muxing,
        // since the track header is written first. The model is updated here, on the caller's context.
        await DetectForcedModesAsync(tracks, cancellationToken);

        // The heavy lifting runs off the caller's thread; the document is only read there.
        await Task.Run(() => Write(document, tracks, options, output, factory, progress, cancellationToken), cancellationToken);

        // Back on the caller's (UI) context: refresh the document from the new file. The file has been replaced at
        // this point, so the document must follow it even if cancellation is requested now.
        await factory.AdoptAsync(document, output, tracks, CancellationToken.None);
        progress?.Report(1.0);
    }

    /// <summary>
    /// Checks every track of <paramref name="document"/> against <paramref name="target"/> without writing anything,
    /// so the UI can list tracks that need an action (conversion or skipping) before a structural save.
    /// </summary>
    /// <exception cref="NotSupportedException">No muxer is registered for the target.</exception>
    public static Task<IReadOnlyList<(Track Track, TrackSupport Support)>> CheckAsync(MediaDocument document, ContainerKind target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var factory = MediaFormatRegistry.GetMuxer(target) ??
                      throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_RemuxComponentMissing, target));
        var tracks = document.Tracks.Where(t => t is not ChapterTrack).ToList();
        return Task.Run<IReadOnlyList<(Track, TrackSupport)>>(() =>
        {
            var result = new List<(Track, TrackSupport)>();
            var demuxers = new Dictionary<string, IDemuxer>(StringComparer.Ordinal);
            try
            {
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (track.Source is not { } source)
                    {
                        result.Add((track, new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, Strings.Reason_NoSourceFile)));
                        continue;
                    }

                    var path = Path.GetFullPath(source.Path);
                    if (!demuxers.TryGetValue(path, out var demuxer))
                    {
                        demuxer = MediaFormatRegistry.OpenDemuxer(path, new DemuxOptions { FrameRate = source.Import?.FrameRate });
                        demuxers.Add(path, demuxer);
                    }

                    var sample = demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId);
                    result.Add((track, sample is null
                        ? new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, Strings.Reason_TrackNotFoundInSource)
                        : CheckConverted(factory, sample.Config, source.Import)));
                }
            }
            finally
            {
                foreach (var d in demuxers.Values)
                    d.Dispose();
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Sets <see cref="SubtitleTrack.ForcedMode"/> of the subtitle tracks converted by OCR whose mode is not set, from
    /// the forced flags of their bitmaps (all forced: <see cref="ForcedSubtitleMode.AllSamplesForced"/>, which also
    /// sets Matroska's FlagForced; some: <see cref="ForcedSubtitleMode.SomeSamplesForced"/>).
    /// </summary>
    private static async Task DetectForcedModesAsync(List<Track> tracks, CancellationToken cancellationToken)
    {
        if (MediaFormatRegistry.AvailableSubtitleConverter is not { } converter)
            return;
        foreach (var track in tracks)
        {
            if (track is not SubtitleTrack { ForcedMode: ForcedSubtitleMode.None, IsForced: false } sub || sub.Source is not { } source ||
                !SubtitleConversions.IsOcr(sub))
                continue;

            var mode = await Task.Run(() =>
            {
                using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions { FrameRate = source.Import?.FrameRate });
                var sampleSource = demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId);
                return sampleSource is not null && converter.CanDecode(sampleSource.Config)
                    ? converter.DetectForcedMode(sampleSource, cancellationToken)
                    : ForcedSubtitleMode.None;
            }, cancellationToken);
            if (mode != ForcedSubtitleMode.None)
            {
                AppLog.Info(string.Format(CultureInfo.CurrentCulture, mode == ForcedSubtitleMode.AllSamplesForced ? Strings.Log_AllSubtitlesForced : Strings.Log_SomeSubtitlesForced, track.Name));
                sub.ForcedMode = mode;
            }
        }
    }

    /// <summary>Support of a track after its conversion action (if any) is applied.</summary>
    private static TrackSupport CheckConverted(IMuxerFactory factory, CodecConfig config, TrackImportOptions? import)
    {
        var action = import?.Action ?? ImportAction.Passthrough;
        if (SubtitleConversions.IsOcr(import, config.Codec))
            return CheckOcr(factory, config, import!);
        if (TextSubtitleConverter.Converts(config, action))
        {
            var converted = factory.CheckSupport(TextSubtitleConverter.PredictOutput(config, TextSubtitleConverter.Target(action)!.Value));
            return converted.CanMux ? new TrackSupport(TrackSupportLevel.Converted, action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConvertedTo, ConversionDefaults.DisplayName(action))) : converted;
        }

        if (ConversionTarget(action) is not { } target)
            return factory.CheckSupport(config);

        var label = ConversionDefaults.DisplayName(action, import?.Conversion?.Mixdown);
        if (MediaFormatRegistry.AvailableAudioConverter is not { } converter)
        {
            var reason = MediaFormatRegistry.AudioConverter?.UnavailableReason ?? Strings.Reason_NoAudioConverter;
            return new TrackSupport(TrackSupportLevel.NeedsConversion, action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConversionUnavailable, label, reason));
        }

        if (!converter.CanDecode(config))
            return new TrackSupport(TrackSupportLevel.NeedsConversion, action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_AudioCannotBeDecoded, config.FormatName, converter.Name));

        var output = factory.CheckSupport((import?.Conversion ?? ConversionDefaults.Settings).PredictOutput(config, target));
        return output.CanMux ? new TrackSupport(TrackSupportLevel.Converted, action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConvertedTo, label)) : output;
    }

    /// <summary>Support of a bitmap subtitle track converted to text by OCR.</summary>
    private static TrackSupport CheckOcr(IMuxerFactory factory, CodecConfig config, TrackImportOptions import)
    {
        var target = SubtitleConversions.Target(import.Action)!.Value;
        var label = SubtitleConversions.DisplayName(target);
        if (MediaFormatRegistry.AvailableSubtitleConverter is not { } converter)
        {
            var reason = MediaFormatRegistry.SubtitleConverter?.UnavailableReason ?? Strings.Reason_NoOcrEngine;
            return new TrackSupport(TrackSupportLevel.NeedsConversion, import.Action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConversionUnavailable, label, reason));
        }

        if (!converter.CanDecode(config))
            return new TrackSupport(TrackSupportLevel.NeedsConversion, import.Action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_SubtitlesCannotBeDecodedForOcr, config.FormatName));

        var language = converter.ResolveLanguage(config.Language, import.Ocr ?? OcrOptions.Default);
        if (converter.CheckLanguage(language) is { } missing)
            return new TrackSupport(TrackSupportLevel.NeedsConversion, import.Action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConversionUnavailable, label, missing));

        var output = factory.CheckSupport(SubtitleConversions.PredictOutput(config, target));
        return output.CanMux ? new TrackSupport(TrackSupportLevel.Converted, import.Action, string.Format(CultureInfo.CurrentCulture, Strings.Reason_ConvertedToWith, label, converter.Name, language)) : output;
    }

    private static AudioConversionTarget? ConversionTarget(ImportAction action) => ConversionDefaults.Target(action);

    /// <summary>
    /// The text conversion to apply: the chosen one, or the container's own when it cannot store the track as it is
    /// (tx3g into Matroska); null when the track is copied.
    /// </summary>
    private static ImportAction? TextAction(CodecConfig config, ImportAction action, TrackSupport support)
    {
        if (TextSubtitleConverter.Converts(config, action))
            return action;
        return action == ImportAction.Passthrough && support.Level == TrackSupportLevel.Converted && TextSubtitleConverter.Converts(config, support.SuggestedAction)
            ? support.SuggestedAction
            : null;
    }

    /// <summary>The picture subtitles are laid out on: the first video track's display size (0 × 0 without video).</summary>
    private static (int Width, int Height) Canvas(IEnumerable<Track> tracks)
    {
        if (tracks.OfType<VideoTrack>().FirstOrDefault() is not { } video)
            return (0, 0);
        return video.DisplayWidth > 0 && video.DisplayHeight > 0 ? ((int)video.DisplayWidth, (int)video.DisplayHeight) : (video.PixelWidth, video.PixelHeight);
    }

    /// <summary>
    /// The bitstream's colour and static HDR10 metadata when the container lacks some of it (the document scan's
    /// result when available, otherwise a scan of the first frames); null when nothing is missing.
    /// </summary>
    private static VideoStreamInfo? StreamInfoFor(Track track, ISampleSource source, CancellationToken ct)
    {
        var config = source.Config;
        if (!VideoStreamInfoScanner.CanScan(config))
            return null;
        var hdr = config.Hdr;
        if (config.Color.IsSpecified && hdr is { HasMasteringDisplay: true, HasLightLevel: true, HasAmbient: true })
            return null;
        var info = (track as VideoTrack)?.StreamInfo ?? VideoStreamInfoScanner.Scan(source, ct);
        if (info.IsEmpty)
            return null;
        if (!config.Color.IsSpecified && info.Color.IsSpecified || !ReferenceEquals(HdrInfo.Merge(hdr, info.Hdr), hdr))
            AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_StreamMetadataAdded, track.Name));
        return info;
    }

    private sealed class Output
    {
        public required Track Model { get; init; }

        public required ISampleSource Source { get; init; }

        public int MuxIndex { get; set; }

        /// <summary>Added to source DTS to place samples on the output timeline (source timescale).</summary>
        public long Offset { get; set; }

        /// <summary>Output time (track timescale) before which the source's samples are not presented.</summary>
        public long VisibleFrom { get; set; }

        public double Timescale { get; init; }

        public MediaSample? Head { get; set; }

        /// <summary>The track carries HDR10+ (known from the model or found in the bitstream).</summary>
        public bool Hdr10Plus { get; init; }

        /// <summary>Colour and static HDR10 metadata of the bitstream, written when the container has none.</summary>
        public VideoStreamInfo? StreamInfo { get; init; }

        public double HeadTime => Head is null ? double.MaxValue : (Head.Dts + Offset) / Timescale;
    }

    private static void Write(MediaDocument document, List<Track> tracks, SaveOptions options, string output, IMuxerFactory factory,
        IProgress<double>? progress, CancellationToken ct)
    {
        var demuxers = new Dictionary<(string Path, double? FrameRate), IDemuxer>();
        var extraDemuxers = new List<IDemuxer>();
        var converters = new List<IDisposable>();
        var used = new HashSet<(string Path, double? FrameRate, uint TrackId)>();
        void Release()
        {
            foreach (var c in converters)
                c.Dispose();
            converters.Clear();
            foreach (var d in demuxers.Values.Concat(extraDemuxers))
                d.Dispose();
            demuxers.Clear();
            extraDemuxers.Clear();
        }

        var directory = Path.GetDirectoryName(output)!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            var outputs = new List<Output>();
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                var source = track.Source ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Error_TrackHasNoSourceFile, track.Name, track.Format));
                var action = source.Import?.Action ?? ImportAction.Passthrough;
                var key = (Path.GetFullPath(source.Path), source.Import?.FrameRate);
                IDemuxer? demuxer;
                if (!used.Add((key.Item1, key.FrameRate, source.TrackId)))
                {
                    // The same source track feeds two outputs ("AAC + Passthru"): each needs its own read position.
                    demuxer = MediaFormatRegistry.OpenDemuxer(key.Item1, new DemuxOptions { FrameRate = key.FrameRate });
                    extraDemuxers.Add(demuxer);
                }
                else if (!demuxers.TryGetValue(key, out demuxer))
                {
                    demuxer = MediaFormatRegistry.OpenDemuxer(key.Item1, new DemuxOptions { FrameRate = key.FrameRate });
                    demuxers.Add(key, demuxer);
                }

                var sampleSource = demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) ??
                                   throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_TrackNotFoundInFile, source.TrackId, Path.GetFileName(source.Path)));
                var support = CheckConverted(factory, sampleSource.Config, source.Import);
                if (!support.CanMux)
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_TrackCannotBeWritten, sampleSource.Config.FormatName, track.Name, factory.Kind, support.Reason));
                if (support.Level == TrackSupportLevel.Passthrough && support.Reason is { } warning)
                    AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_TrackWarning, sampleSource.Config.FormatName, track.Name, warning));

                // HDR10+ in AV1 is signalled in MP4 by the cdm4 brand ("HDR10+ Metadata in AV1", §3), so the muxer
                // must know before it starts; only AV1 bitstreams are scanned for it.
                var hdr10Plus = track is VideoTrack { Hdr10Plus: true } || sampleSource.Config.Hdr10Plus ||
                                (factory.Kind == ContainerKind.Mp4 && sampleSource.Config.Codec == CodecType.Av1 && Hdr10PlusDetector.Detect(sampleSource, ct));

                var streamInfo = StreamInfoFor(track, sampleSource, ct);

                sampleSource.Reset();
                if (SubtitleConversions.IsOcr(source.Import, sampleSource.Config.Codec))
                {
                    var target = SubtitleConversions.Target(action)!.Value;
                    var converter = MediaFormatRegistry.AvailableSubtitleConverter!.Create(sampleSource, target, source.Import!.Ocr ?? OcrOptions.Default, ct);
                    if (converter is IDisposable disposable)
                        converters.Add(disposable);
                    AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_RecognisingOcr, sampleSource.Config.FormatName, track.Name, converter.Config.FormatName));
                    sampleSource = converter;
                }
                else if (ConversionTarget(action) is { } target)
                {
                    var settings = source.Import?.Conversion ?? ConversionDefaults.Settings;
                    var converter = MediaFormatRegistry.AvailableAudioConverter!.Create(sampleSource, target, settings);
                    if (converter is IDisposable disposable)
                        converters.Add(disposable);
                    AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_ConvertingAudio, sampleSource.Config.FormatName, track.Name, converter.Config.FormatName,
                                converter.Config.Channels, converter.Config.SampleRate));
                    sampleSource = converter;
                }

                else if (TextAction(sampleSource.Config, action, support) is { } textAction)
                {
                    // Text subtitles in another text format: styles, positions and karaoke kept as the target allows.
                    var (width, height) = Canvas(tracks);
                    var converter = new TextSubtitleConverter(sampleSource, TextSubtitleConverter.Target(textAction)!.Value, width, height);
                    AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_ConvertingTrack, sampleSource.Config.FormatName, track.Name, converter.Config.FormatName));
                    sampleSource = converter;
                }
                else if (factory.Kind == ContainerKind.Mp4 && VfwNativeSource.TryCreate(sampleSource) is { } native)
                {
                    // MPEG-4 Part 2, VC-1 and H.263 stored the Video for Windows way go to MP4 in their own form.
                    AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_StoredNativelyInMp4, sampleSource.Config.FormatName, track.Name, native.Config.FormatName));
                    sampleSource = native;
                }
                else if (factory.Kind == ContainerKind.Mp4 && sampleSource.Config.Codec == CodecType.TrueHd)
                {
                    // MP4 stores one access unit per sample with exact timing and a dmlp box (Dolby's ISOBMFF spec).
                    sampleSource = new TrueHdAccessUnitSource(sampleSource);
                }

                outputs.Add(new Output
                {
                    Model = track, Source = sampleSource, Timescale = Math.Max(1u, sampleSource.Config.Timescale), Hdr10Plus = hdr10Plus,
                    StreamInfo = streamInfo,
                });
            }

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                using var muxer = factory.Create(stream, new MuxerSettings { Document = document, OutputPath = output, Options = options });
                double total = 0;
                foreach (var o in outputs)
                {
                    var startTicks = (long)Math.Round((o.Source.StartOffset + o.Model.StartOffset).TotalSeconds * o.Timescale);
                    o.Offset = startTicks - o.Source.MediaStart;
                    o.VisibleFrom = startTicks;
                    o.Head = o.Source.ReadNext();
                }

                // A track without samples has nothing to store (and an empty track makes some readers fail).
                var withMedia = outputs.Count;
                foreach (var empty in outputs.Where(o => o.Head is null).ToList())
                {
                    AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_TrackHasNoSamples, empty.Source.Config.FormatName, empty.Model.Name));
                    outputs.Remove(empty);
                }

                if (withMedia > 0 && outputs.Count == 0)
                    throw new InvalidDataException(Strings.Error_NoTrackHasSamples);

                // Video decoded before time zero (e.g. an MP4 edit list skipping leading frames) cannot be hidden in
                // every container: then everything moves so the earliest video frame is shown at zero.
                if (!muxer.SupportsVideoPreRoll)
                {
                    var shift = outputs.Where(o => o.Source.Config.Kind == TrackKind.Video && o.Head is not null)
                        .Select(o => -(o.Head!.Pts + o.Offset) / o.Timescale)
                        .DefaultIfEmpty(0)
                        .Max();
                    if (shift > 0)
                    {
                        AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_VideoStartsEarly, shift));
                        foreach (var o in outputs)
                        {
                            var ticks = (long)Math.Round(shift * o.Timescale);
                            o.Offset += ticks;
                            o.VisibleFrom += ticks;
                        }
                    }
                }

                foreach (var o in outputs)
                {
                    var cfg = o.Source.Config;
                    if (o.Model is VideoTrack { DolbyVisionRecord: { Length: >= 5 } dvRecord })
                        cfg = cfg with { DolbyVisionConfig = dvRecord }; // repaired or edited Dolby Vision configuration
                    if (o.Hdr10Plus)
                        cfg = cfg with { Hdr10Plus = true };
                    if (o.StreamInfo is { IsEmpty: false } bitstream)
                        cfg = cfg with { StreamColor = bitstream.Color, StreamHdr = bitstream.Hdr };
                    var preRoll = o.Head is { } first && first.Pts + o.Offset < 0 ? TimeSpan.FromSeconds(-(first.Pts + o.Offset) / o.Timescale) : TimeSpan.Zero;
                    o.MuxIndex = muxer.AddTrack(cfg, new MuxTrackSettings
                    {
                        Model = o.Model,
                        EstimatedSampleCount = o.Source.SampleCountHint,
                        EstimatedDuration = o.Source.Duration,
                        PreRoll = preRoll,
                        // Frames the source decodes before its edit starts stay hidden (not only those before zero).
                        VisibleFrom = TimeSpan.FromSeconds(Math.Max(0, o.VisibleFrom) / (double)o.Timescale),
                    });
                    total = Math.Max(total, o.Source.Duration.TotalSeconds + o.Model.StartOffset.TotalSeconds);
                }

                Pump(outputs, muxer, total, progress, ct);
                muxer.Finish(ct);
                stream.Flush(flushToDisk: true);
            }

            // Release the sources before replacing the destination (it may be one of them).
            Release();
            File.Move(temp, output, overwrite: true);
            AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_Remuxed, tracks.Count, Path.GetFileName(output)));
        }
        catch
        {
            Release();
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Feeds samples to the muxer in decoding-time order, in runs of the muxer's interleave duration.</summary>
    private static void Pump(List<Output> outputs, IMuxer muxer, double totalSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var interleave = muxer.InterleaveDuration.TotalSeconds;
        var lastReported = -1.0;
        var count = 0;
        while (true)
        {
            Output? next = null;
            foreach (var o in outputs)
            {
                if (o.Head is not null && (next is null || o.HeadTime < next.HeadTime))
                    next = o;
            }

            if (next is null)
                break;

            var runEnd = next.HeadTime + interleave;
            do
            {
                if ((++count & 0xFF) == 0)
                    ct.ThrowIfCancellationRequested();
                var sample = next.Head!;
                var time = next.HeadTime;
                sample.Dts += next.Offset;
                muxer.WriteSample(next.MuxIndex, sample);
                next.Head = next.Source.ReadNext();

                if (progress is not null && totalSeconds > 0)
                {
                    var p = Math.Clamp(time / totalSeconds, 0, 0.99);
                    if (p - lastReported >= 0.005)
                    {
                        lastReported = p;
                        progress.Report(p);
                    }
                }
            }
            while (interleave > 0 && next.Head is not null && next.HeadTime < runEnd);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_CouldNotDeleteTemp, path, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_CouldNotDeleteTemp, path, ex.Message));
        }
    }
}

/// <summary>Helpers shared by <see cref="IMuxerFactory.AdoptAsync"/> implementations.</summary>
public static class RemuxAdoption
{
    /// <summary>
    /// Points <paramref name="document"/> at the re-read file <paramref name="saved"/>: path, container, state and
    /// the IDs/sources of <paramref name="written"/> (matched in order with the saved file's tracks).
    /// </summary>
    public static void Apply(MediaDocument document, MediaDocument saved, IReadOnlyList<Track> written)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(written);
        document.Path = saved.Path;
        document.Container = saved.Container;
        document.ContainerState = saved.ContainerState;
        document.FileSize = saved.FileSize;
        if (saved.Duration > TimeSpan.Zero)
            document.Duration = saved.Duration;

        var savedTracks = saved.Tracks.Where(t => t is not ChapterTrack).ToList();
        for (var i = 0; i < written.Count && i < savedTracks.Count; i++)
        {
            var track = written[i];
            var s = savedTracks[i];
            track.Id = s.Id;
            track.Source = s.Source;
            track.CodecId = s.CodecId;
            track.Format = s.Format;
            if (s.FormatDetails.Length > 0)
                track.FormatDetails = s.FormatDetails;
            track.Timescale = s.Timescale;
            if (s.Duration > TimeSpan.Zero)
                track.Duration = s.Duration;
            if (s.Bitrate > 0)
                track.Bitrate = s.Bitrate;
            if (s.DataLength > 0)
                track.DataLength = s.DataLength;
            track.StartOffset = TimeSpan.Zero;
        }

        // Tracks that were not written (skipped) are no longer part of the document.
        foreach (var dropped in document.Tracks.Where(t => t is not ChapterTrack && !written.Contains(t)).ToList())
            document.Tracks.Remove(dropped);

        var savedChapters = saved.Tracks.OfType<ChapterTrack>().FirstOrDefault();
        foreach (var chapterTrack in document.Tracks.OfType<ChapterTrack>())
            chapterTrack.Id = savedChapters?.Id ?? 0;
        document.IsDirty = false;
    }
}
