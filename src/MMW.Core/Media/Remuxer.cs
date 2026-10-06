using MMW.Core.Diagnostics;
using MMW.Core.Model;

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
        var output = Path.GetFullPath(options.OutputPath ?? document.Path ?? throw new InvalidOperationException("The document has no destination path."));
        var factory = MediaFormatRegistry.GetMuxer(target) ??
                      throw new NotSupportedException($"Writing {target} files requires the remux component, which is not registered.");

        // "AAC + Passthru" / "AAC + AC3" become two document tracks (on the caller's context: the document is changed).
        TrackConversions.Expand(document);
        var tracks = document.Tracks.Where(t => t is not ChapterTrack && t.Source?.Import?.Action != ImportAction.Skip).ToList();

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
                      throw new NotSupportedException($"Writing {target} files requires the remux component, which is not registered.");
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
                        result.Add((track, new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, "the track has no source file")));
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
                        ? new TrackSupport(TrackSupportLevel.Unsupported, ImportAction.Skip, "the track was not found in its source file")
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

    /// <summary>Support of a track after its conversion action (if any) is applied.</summary>
    private static TrackSupport CheckConverted(IMuxerFactory factory, CodecConfig config, TrackImportOptions? import)
    {
        var action = import?.Action ?? ImportAction.Passthrough;
        if (ConversionTarget(action) is not { } target)
            return factory.CheckSupport(config);

        var label = ConversionDefaults.DisplayName(action, import?.Conversion?.Mixdown);
        if (MediaFormatRegistry.AvailableAudioConverter is not { } converter)
        {
            var reason = MediaFormatRegistry.AudioConverter?.UnavailableReason ?? "no audio converter is installed";
            return new TrackSupport(TrackSupportLevel.NeedsConversion, action, $"converting to {label} is not available: {reason}");
        }

        if (!converter.CanDecode(config))
            return new TrackSupport(TrackSupportLevel.NeedsConversion, action, $"{config.FormatName} audio cannot be decoded by {converter.Name}");

        var output = factory.CheckSupport((import?.Conversion ?? ConversionDefaults.Settings).PredictOutput(config, target));
        return output.CanMux ? new TrackSupport(TrackSupportLevel.Converted, action, $"converted to {label}") : output;
    }

    /// <summary>The codec a single-track conversion action produces, or null for other actions.</summary>
    private static AudioConversionTarget? ConversionTarget(ImportAction action) => action switch
    {
        ImportAction.ConvertToAac => AudioConversionTarget.Aac,
        ImportAction.ConvertToAc3 => AudioConversionTarget.Ac3,
        _ => null,
    };

    private sealed class Output
    {
        public required Track Model { get; init; }

        public required ISampleSource Source { get; init; }

        public int MuxIndex { get; set; }

        /// <summary>Added to source DTS to place samples on the output timeline (source timescale).</summary>
        public long Offset { get; set; }

        public double Timescale { get; init; }

        public MediaSample? Head { get; set; }

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
                var source = track.Source ?? throw new InvalidOperationException($"Track '{track.Name}' ({track.Format}) has no source file.");
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
                                   throw new InvalidDataException($"Track {source.TrackId} was not found in '{Path.GetFileName(source.Path)}'.");
                var support = CheckConverted(factory, sampleSource.Config, source.Import);
                if (!support.CanMux)
                    throw new NotSupportedException($"{sampleSource.Config.FormatName} track '{track.Name}' cannot be written to {factory.Kind}: {support.Reason}");

                sampleSource.Reset();
                if (ConversionTarget(action) is { } target)
                {
                    var settings = source.Import?.Conversion ?? ConversionDefaults.Settings;
                    var converter = MediaFormatRegistry.AvailableAudioConverter!.Create(sampleSource, target, settings);
                    if (converter is IDisposable disposable)
                        converters.Add(disposable);
                    AppLog.Info($"Converting {sampleSource.Config.FormatName} track '{track.Name}' to {converter.Config.FormatName} " +
                                $"({converter.Config.Channels} ch, {converter.Config.SampleRate} Hz).");
                    sampleSource = converter;
                }

                outputs.Add(new Output { Model = track, Source = sampleSource, Timescale = Math.Max(1u, sampleSource.Config.Timescale) });
            }

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                using var muxer = factory.Create(stream, new MuxerSettings { Document = document, OutputPath = output, Options = options });
                double total = 0;
                foreach (var o in outputs)
                {
                    var startTicks = (long)Math.Round((o.Source.StartOffset + o.Model.StartOffset).TotalSeconds * o.Timescale);
                    o.Offset = startTicks - o.Source.MediaStart;
                    o.Head = o.Source.ReadNext();
                }

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
                        AppLog.Info($"Video starts {shift:0.###} s before the presentation; shifting all tracks.");
                        foreach (var o in outputs)
                            o.Offset += (long)Math.Round(shift * o.Timescale);
                    }
                }

                foreach (var o in outputs)
                {
                    var cfg = o.Source.Config;
                    var preRoll = o.Head is { } first && first.Pts + o.Offset < 0 ? TimeSpan.FromSeconds(-(first.Pts + o.Offset) / o.Timescale) : TimeSpan.Zero;
                    o.MuxIndex = muxer.AddTrack(cfg, new MuxTrackSettings
                    {
                        Model = o.Model,
                        EstimatedSampleCount = o.Source.SampleCountHint,
                        EstimatedDuration = o.Source.Duration,
                        PreRoll = preRoll,
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
            AppLog.Info($"Remuxed {tracks.Count} track(s) into '{Path.GetFileName(output)}'.");
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
            AppLog.Warn($"Could not delete temporary file '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Warn($"Could not delete temporary file '{path}': {ex.Message}");
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
