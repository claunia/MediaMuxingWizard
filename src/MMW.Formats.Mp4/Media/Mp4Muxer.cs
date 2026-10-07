using System.Buffers.Binary;
using System.Text;
using MMW.Core.Chapters;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Metadata;

namespace MMW.Formats.Mp4.Media;

/// <summary>
/// Writes an MP4 file from interleaved samples.
/// </summary>
/// <remarks>
/// <para>
/// Layout: <c>ftyp</c>, a <c>free</c> box reserved for the movie header (sized from the expected sample counts),
/// then <c>mdat</c>. Samples are streamed into <c>mdat</c> in chunks (runs of one track, see
/// <see cref="InterleaveDuration"/>); only the per-sample tables are kept in memory. On <see cref="Finish"/> the
/// <c>moov</c> is written into the reserved space when it fits (fast start, no copy). Otherwise it is appended after
/// <c>mdat</c>, or — when <see cref="SaveOptions.Optimize"/> is set — the media data is shifted to make room so the
/// result is still fast-start.
/// </para>
/// <para>
/// Timing: composition offsets are normalised to be non-negative (version 0 <c>ctts</c>) and the shift goes into the
/// edit list's media_time, together with any pre-roll (AAC priming, Opus pre-skip); a positive start time becomes an
/// initial empty edit.
/// </para>
/// </remarks>
internal sealed class Mp4Muxer : IMuxer
{
    private const uint DefaultMovieTimescale = 1000;

    /// <summary>
    /// The movie timescale (edit list and movie durations): milliseconds, or the timescale of an audio track whose end
    /// is trimmed, so its edit ends on the exact sample.
    /// </summary>
    private uint MovieTimescale { get; set; } = DefaultMovieTimescale;
    private const long ChunkByteLimit = 4L * 1024 * 1024;

    private readonly Stream _out;
    private readonly MuxerSettings _settings;
    private readonly List<TrackState> _tracks = [];
    private long _reservedStart;
    private long _reservedSize;
    private long _mdatStart;
    private bool _headerWritten;
    private TrackState? _chunkTrack;
    private long _chunkStartDts;
    private long _chunkBytes;
    private byte[] _buffer = new byte[64 * 1024];
    private bool _finished;

    public Mp4Muxer(Stream output, MuxerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);
        if (!output.CanSeek || !output.CanWrite)
            throw new ArgumentException("The output stream must be seekable and writable.", nameof(output));
        _out = output;
        _settings = settings;
    }

    public TimeSpan InterleaveDuration => TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    public bool SupportsVideoPreRoll => true;

    private sealed class TrackState
    {
        public required CodecConfig Config { get; set; }

        public required MuxTrackSettings Settings { get; init; }

        public uint TrackId { get; init; }

        public uint Timescale { get; init; }

        /// <summary>Output is tx3g produced from another text format (or tx3g needing gap filling).</summary>
        public bool TextConversion { get; init; }

        public SubtitleTimeline? Timeline { get; init; }

        /// <summary>WebVTT cues written as ISO/IEC 14496-30 samples (one per interval of unchanged active cues).</summary>
        public Mp4WebVttTimeline? WebVttTimeline { get; init; }

        public List<int> Sizes { get; } = [];

        public List<long> Dts { get; } = [];

        public List<long> Cto { get; } = [];

        public List<long> Durations { get; } = [];

        public List<int> SyncSamples { get; } = [];

        public List<long> ChunkOffsets { get; } = [];

        public List<int> ChunkCounts { get; } = [];

        public byte[]? FirstSample { get; set; }

        /// <summary>Samples not presented at the end of the last sample (encoder padding), shortening the edit.</summary>
        public long TrimEnd { get; set; }

        /// <summary>Decoded length of the last sample from its bitstream (audio), or 0 when unknown.</summary>
        public long LastFrameSamples { get; set; }

        public bool InBandParameterSets { get; set; }

        public long TotalBytes { get; set; }

        public int MaxSampleSize { get; set; }

        public Dictionary<long, long> BytesPerSecond { get; } = [];

        public long LastDts { get; set; } = long.MinValue;

        public bool HasNegativeDts { get; set; }

        public byte[][]? HevcParameterSets { get; set; }
    }

    // ------------------------------------------------------------------ tracks

    public int AddTrack(CodecConfig config, MuxTrackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        if (_headerWritten)
            throw new InvalidOperationException("Tracks must be added before samples are written.");
        var support = Mp4SampleEntries.CheckSupport(config);
        if (!support.CanMux)
            throw new NotSupportedException($"{config.FormatName} cannot be written to MP4: {support.Reason}");

        config = ApplyModel(config, settings.Model);
        // QuickTime PCM entries ('sowt', 'twos', 'in24', 'lpcm' …) are rewritten as the ISO 'ipcm' / 'fpcm'.
        if (config.Codec == CodecType.Pcm && config.Native is Mp4NativeTrack { Entry.Type: not ("ipcm" or "fpcm") } && Mp4SampleEntries.PcmWritable(config))
            config = config with { Native = null };
        // WebVTT samples carry single cues (also when read from 'wvtt'); they are always rebuilt as 14496-30 samples.
        var webVtt = config.Codec == CodecType.WebVtt;
        // tx3g with its own sample description (from the subtitle converter) is written as it is: its samples already
        // follow each other and carry every style, karaoke and text box record.
        var tx3g = config.Codec == CodecType.Tx3g && config.Extradata is { Length: >= 30 } && config.SourceCodecId == "tx3g";
        var text = config.Native is not Mp4NativeTrack && CodecNames.IsText(config.Codec) && !webVtt && !tx3g;
        var timescale = config.Timescale == 0 ? 1000u : config.Timescale;
        var state = new TrackState
        {
            Config = config,
            Settings = settings,
            TrackId = (uint)_tracks.Count + 1,
            Timescale = timescale,
            TextConversion = text,
            Timeline = text ? new SubtitleTimeline() : null,
            WebVttTimeline = webVtt ? new Mp4WebVttTimeline() : null,
        };

        if (config.Codec == CodecType.Hevc && config.Extradata is { } hvcC && Mp4RemuxOptions.ForceHvc1)
        {
            try
            {
                state.HevcParameterSets = Hevc.ParseHvcC(hvcC).Nals.Where(n => n.Type is Hevc.NalVps or Hevc.NalSps or Hevc.NalPps).Select(n => n.Nal).ToArray();
            }
            catch (InvalidDataException ex)
            {
                AppLog.Warn($"Invalid hvcC record: {ex.Message}");
            }
        }

        _tracks.Add(state);
        return _tracks.Count - 1;
    }

    /// <summary>Applies the document track's edited values to the codec description.</summary>
    private static CodecConfig ApplyModel(CodecConfig config, Track? model)
    {
        if (model is null)
            return config;
        config = config with { Language = model.Language, Name = model.Name };
        if (model is VideoTrack v)
        {
            config = config with
            {
                Color = v.Color,
                Hdr = v.Hdr ?? config.Hdr,
                ParNumerator = v.ParNumerator > 0 ? v.ParNumerator : config.ParNumerator,
                ParDenominator = v.ParDenominator > 0 ? v.ParDenominator : config.ParDenominator,
            };
        }

        return config;
    }

    // ------------------------------------------------------------------ samples

    public void WriteSample(int track, MediaSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var state = _tracks[track];
        EnsureHeader();

        if (state.TextConversion)
        {
            WriteText(state, sample);
            return;
        }

        if (state.WebVttTimeline is { } timeline)
        {
            var start = sample.Pts;
            var end = start + (sample.Duration > 0 ? sample.Duration : state.Timescale * 2L);
            var cue = new Mp4WebVttCue(null, sample.CueSettings, Encoding.UTF8.GetString(sample.GetData().Span));
            foreach (var interval in timeline.Add(start, end, cue))
                AppendWebVtt(state, interval);
            return;
        }

        var size = sample.Size;
        if (_buffer.Length < size)
            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        sample.CopyTo(_buffer.AsSpan(0, size));
        var data = _buffer.AsSpan(0, size);

        if (state.Config.Codec == CodecType.Hevc && sample.IsSync)
            data = InspectHevc(state, data);
        else if (state.Config.Codec == CodecType.Vvc && sample.IsSync && !state.InBandParameterSets)
            state.InBandParameterSets = HasVvcParameterSets(state, data);

        var (dts, duration) = (sample.Dts, sample.Duration);
        if (PcmTiming(state, size) is { } pcm)
            (dts, duration) = (pcm.Expected is { } expected && Math.Abs(dts - expected) <= state.Timescale / 50 ? expected : dts, pcm.Frames);
        Append(state, data, dts, sample.CtsOffset, duration, sample.IsSync);
        state.TrimEnd = sample.TrimEnd; // only the last sample's counts
        state.LastFrameSamples = sample.TrimEnd > 0 && state.Config.Kind == TrackKind.Audio && state.Config.SampleRate == state.Timescale
            ? AudioFrames.Samples(state.Config, data)
            : 0;
    }

    private static bool IsKnown(byte[][] known, ReadOnlySpan<byte> nal)
    {
        foreach (var k in known)
        {
            if (nal.SequenceEqual(k))
                return true;
        }

        return false;
    }

    private static Span<byte> InspectHevc(TrackState state, Span<byte> data)
    {
        var lengthSize = state.Config.Extradata is { Length: >= 23 } c ? (c[21] & 3) + 1 : 4;
        List<Range> nals;
        try
        {
            nals = NalUnits.SplitLengthPrefixed(data, lengthSize);
        }
        catch (InvalidDataException)
        {
            return data;
        }

        var hasPs = false;
        var keep = new List<Range>();
        foreach (var r in nals)
        {
            var type = NalUnits.HevcType(data[r]);
            if (type is Hevc.NalVps or Hevc.NalSps or Hevc.NalPps)
            {
                if (state.HevcParameterSets is { } known && IsKnown(known, data[r]))
                    continue;
                hasPs = true;
            }

            keep.Add(r);
        }

        if (hasPs)
            state.InBandParameterSets = true;
        if (state.HevcParameterSets is null || keep.Count == nals.Count || lengthSize != 4)
            return data;
        return NalUnits.ToLengthPrefixed(data, keep);
    }

    /// <summary>True when a VVC sample repeats VPS/SPS/PPS: the sample entry is then 'vvi1'.</summary>
    private static bool HasVvcParameterSets(TrackState state, ReadOnlySpan<byte> data)
    {
        var lengthSize = state.Config.Extradata is { Length: > 0 } c ? ((c[0] >> 1) & 3) + 1 : 4;
        try
        {
            foreach (var r in NalUnits.SplitLengthPrefixed(data, lengthSize))
            {
                if (Vvc.IsParameterSet(Vvc.NalType(data[r])))
                    return true;
            }
        }
        catch (InvalidDataException)
        {
        }

        return false;
    }

    private void WriteText(TrackState state, MediaSample sample)
    {
        var bytes = sample.GetData().Span;
        var styled = state.Config.Codec switch
        {
            CodecType.Tx3g => SubtitleText.FromTx3g(bytes),
            CodecType.Ass => SubtitleText.FromAss(SubtitleText.AssBlockText(Encoding.UTF8.GetString(bytes), ssa: false)),
            CodecType.Ssa => SubtitleText.FromAss(SubtitleText.AssBlockText(Encoding.UTF8.GetString(bytes), ssa: true)),
            CodecType.WebVtt => SubtitleText.FromWebVtt(Encoding.UTF8.GetString(bytes)),
            _ => SubtitleText.FromSrt(Encoding.UTF8.GetString(bytes)),
        };
        var start = sample.Pts;
        var duration = sample.Duration > 0 ? sample.Duration : state.Timescale * 2L;
        foreach (var cue in state.Timeline!.Add(new SubtitleCue(start, start + duration, styled)))
            AppendCue(state, cue);
    }

    private void AppendWebVtt(TrackState state, (long Start, long End, List<Mp4WebVttCue> Cues) interval) =>
        Append(state, Mp4WebVtt.Build(interval.Cues), interval.Start, 0, interval.End - interval.Start, true);

    private void AppendCue(TrackState state, SubtitleCue cue)
    {
        var bytes = SubtitleText.ToTx3g(cue.Text, SubtitleText.DefaultFontSize(TextCanvas(state).Height));
        Append(state, bytes, cue.Start, 0, cue.End - cue.Start, true);
    }

    /// <summary>
    /// PCM blocks last exactly their frame count, and follow each other without gaps: the container timestamps of the
    /// source (Matroska's are rounded to milliseconds) are only kept for jumps beyond 20 ms. Null for other tracks.
    /// </summary>
    private static (long? Expected, long Frames)? PcmTiming(TrackState state, int size)
    {
        var frameBytes = Mp4SampleEntries.PcmFrameBytes(state.Config);
        if (frameBytes <= 0 || size % frameBytes != 0 || state.Timescale != state.Config.SampleRate)
            return null;
        long? expected = state.Sizes.Count == 0 ? null : state.Dts[^1] + state.Durations[^1];
        return (expected, size / frameBytes);
    }

    private void Append(TrackState state, ReadOnlySpan<byte> data, long dts, long cto, long duration, bool sync)
    {
        // Decoding times must increase. Video keeps its presentation time when they are fixed; for other tracks
        // (overlapping timestamps in the source) presentation follows decoding, so no composition offset appears.
        if (dts <= state.LastDts)
        {
            var fixedDts = state.LastDts + 1;
            if (state.Config.Kind == TrackKind.Video)
                cto -= fixedDts - dts;
            dts = fixedDts;
        }

        state.LastDts = dts;
        var position = _out.Position;
        var scale = state.Timescale;
        if (_chunkTrack != state || dts - _chunkStartDts > scale || _chunkBytes + data.Length > ChunkByteLimit)
        {
            _chunkTrack = state;
            _chunkStartDts = dts;
            _chunkBytes = 0;
            state.ChunkOffsets.Add(position);
            state.ChunkCounts.Add(0);
        }

        _out.Write(data);
        _chunkBytes += data.Length;
        state.ChunkCounts[^1]++;
        state.Sizes.Add(data.Length);
        state.Dts.Add(dts);
        state.Cto.Add(cto);
        state.Durations.Add(duration);
        if (sync)
            state.SyncSamples.Add(state.Sizes.Count);
        state.FirstSample ??= data.ToArray();
        state.TotalBytes += data.Length;
        state.MaxSampleSize = Math.Max(state.MaxSampleSize, data.Length);
        var second = dts / Math.Max(1, scale);
        state.BytesPerSecond[second] = state.BytesPerSecond.GetValueOrDefault(second) + data.Length;
    }

    /// <summary>Writes the reserved header space and the mdat header before the first sample.</summary>
    private void EnsureHeader()
    {
        if (_headerWritten)
            return;
        _headerWritten = true;
        _out.SetLength(0);
        _out.Position = 0;
        _out.Write(BoxWriter.ToArray(BuildFtyp(_settings.OutputPath, _tracks.Select(t => t.Config).ToList())));
        _reservedStart = _out.Position;
        _reservedSize = EstimateMoovSize();
        WriteFree(_out, _reservedSize);

        // 'free' (8) + 'mdat' (8): turned into a 16-byte large-size mdat header if the data exceeds 4 GB.
        _out.Write(BoxWriter.Header("free", 8));
        _mdatStart = _out.Position;
        _out.Write(BoxWriter.Header("mdat", 8));
    }

    private long EstimateMoovSize()
    {
        long total = 16 * 1024;
        foreach (var t in _tracks)
        {
            var seconds = Math.Max(1, t.Settings.EstimatedDuration.TotalSeconds);
            var count = t.Settings.EstimatedSampleCount > 0
                ? t.Settings.EstimatedSampleCount
                : t.Config.Kind switch
                {
                    TrackKind.Video => (long)(seconds * Math.Max(30, t.Config.FrameRate)),
                    TrackKind.Audio => (long)(seconds * 50),
                    _ => (long)(seconds / 2),
                };
            var perSample = t.Config.Kind == TrackKind.Video || t.WebVttTimeline is not null ? 24 : t.TextConversion ? 16 : 8;
            total += 4096 + count * perSample + (long)(seconds * 2 + 1) * 8 + (t.Config.Extradata?.Length ?? 0);
        }

        total += _settings.Document.Metadata.Artworks.Sum(a => (long)a.Data.Length + 32);
        total += _settings.Document.Metadata.Keys.Count() * 512L;
        total += _settings.Document.Chapters.Count * 64L + 2048;
        return total + total / 10;
    }

    // ------------------------------------------------------------------ finishing

    public void Finish(CancellationToken cancellationToken)
    {
        if (_finished)
            throw new InvalidOperationException("The muxer was already finished.");
        _finished = true;
        EnsureHeader();
        if (_tracks.FirstOrDefault(t => t.TrimEnd > 0 && t.Config.Kind == TrackKind.Audio) is { Timescale: > 0 and <= 192000 } trimmed)
            MovieTimescale = trimmed.Timescale;

        foreach (var t in _tracks.Where(t => t.TextConversion))
        {
            foreach (var cue in t.Timeline!.Complete())
                AppendCue(t, cue);
        }

        foreach (var t in _tracks)
        {
            foreach (var interval in t.WebVttTimeline?.Complete() ?? [])
                AppendWebVtt(t, interval);
        }

        // Chapter text samples go at the end of the media data.
        var chapters = _settings.Document.Chapters.OrderBy(c => c.Start).ToList();
        var chapterOffset = _out.Position;
        var chapterData = chapters.Count > 0
            ? Mp4Chapters.Build(1, chapters, TimeSpan.Zero, MovieTimescale, 0, false).Data
            : [];
        _out.Write(chapterData);

        var mdatEnd = _out.Position;
        var mdatSize = mdatEnd - _mdatStart;
        var largeMdat = mdatSize > uint.MaxValue;
        cancellationToken.ThrowIfCancellationRequested();

        // Fast start when the header fits in the reserved space (no copy).
        var moov = BuildMoov(chapters, chapterOffset, 0);
        var moovBytes = BoxWriter.ToArray(moov);
        if (moovBytes.Length == _reservedSize || moovBytes.Length + 8 <= _reservedSize)
        {
            PatchMdatHeader(mdatSize, largeMdat);
            _out.Position = _reservedStart;
            _out.Write(moovBytes);
            if (_reservedSize > moovBytes.Length)
                WriteFree(_out, _reservedSize - moovBytes.Length);
            _out.Position = mdatEnd;
            _out.SetLength(mdatEnd);
            return;
        }

        if (!_settings.Options.Optimize)
        {
            PatchMdatHeader(mdatSize, largeMdat);
            _out.Position = mdatEnd;
            _out.Write(moovBytes);
            _out.SetLength(_out.Position);
            return;
        }

        // Make room: move the media data forward so the header precedes it.
        long delta;
        var attempts = 0;
        while (true)
        {
            delta = moovBytes.Length + 1024 - _reservedSize;
            var shifted = BoxWriter.ToArray(BuildMoov(chapters, chapterOffset, delta));
            if (shifted.Length == moovBytes.Length || ++attempts > 4)
            {
                moovBytes = shifted;
                if (shifted.Length + 8 > _reservedSize + delta)
                    throw new InvalidOperationException("Could not lay out the movie header.");
                break;
            }

            moovBytes = shifted;
        }

        AppLog.Info($"Moving {mdatEnd - _mdatStart + 8} bytes of media data to put the movie header first.");
        ShiftForward(_reservedStart + _reservedSize, mdatEnd, delta, cancellationToken);
        _mdatStart += delta;
        PatchMdatHeader(mdatSize, largeMdat);
        _out.Position = _reservedStart;
        _out.Write(moovBytes);
        WriteFree(_out, _reservedSize + delta - moovBytes.Length);
        _out.SetLength(mdatEnd + delta);
    }

    private void PatchMdatHeader(long mdatSize, bool large)
    {
        if (large)
        {
            // Overwrite the 'free' + 'mdat' pair with a 16-byte large-size header.
            _out.Position = _mdatStart - 8;
            _out.Write(BoxWriter.Header("mdat", mdatSize + 8));
            return;
        }

        _out.Position = _mdatStart;
        _out.Write(BoxWriter.Header("mdat", mdatSize));
    }

    /// <summary>Moves bytes [<paramref name="start"/>, <paramref name="end"/>) forward by <paramref name="delta"/>.</summary>
    private void ShiftForward(long start, long end, long delta, CancellationToken ct)
    {
        var buffer = new byte[4 * 1024 * 1024];
        _out.SetLength(end + delta);
        var pos = end;
        while (pos > start)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(buffer.Length, pos - start);
            pos -= n;
            _out.Position = pos;
            _out.ReadExactly(buffer, 0, n);
            _out.Position = pos + delta;
            _out.Write(buffer, 0, n);
        }
    }

    // ------------------------------------------------------------------ moov

    private Box BuildMoov(List<Chapter> chapters, long chapterOffset, long offsetDelta)
    {
        var options = _settings.Options;
        var document = _settings.Document;
        var force64 = options.Use64BitOffsets;
        var traks = new List<Box>();
        long movieDuration = 0;
        var ids = _tracks.Where(t => t.Settings.Model is not null).ToDictionary(t => t.Settings.Model!, t => t.TrackId);

        foreach (var t in _tracks)
        {
            var (trak, duration) = BuildTrak(t, ids, offsetDelta, force64);
            traks.Add(trak);
            movieDuration = Math.Max(movieDuration, duration);
        }

        var nextId = (uint)_tracks.Count + 1;
        if (chapters.Count > 0)
        {
            var total = TimeSpan.FromSeconds((double)movieDuration / MovieTimescale);
            if (document.Duration > total)
                total = document.Duration;
            var built = Mp4Chapters.Build(nextId, chapters, total, MovieTimescale, chapterOffset + offsetDelta, force64);
            nextId += (uint)built.TrackIds.Count;
            traks.AddRange(built.Traks);
            foreach (var target in new[] { _tracks.FirstOrDefault(t => t.Config.Kind == TrackKind.Video), _tracks.FirstOrDefault(t => t.Config.Kind == TrackKind.Audio) })
            {
                if (target is not null)
                    AddReference(traks[_tracks.IndexOf(target)], "chap", [.. built.TrackIds]);
            }
        }

        var use64Times = options.Use64BitTimes || movieDuration > uint.MaxValue;
        var mvhd = new PayloadBuilder().FullBox(use64Times ? 1 : 0, 0);
        if (use64Times)
            mvhd.U64(0).U64(0).U32(MovieTimescale).U64((ulong)movieDuration);
        else
            mvhd.U32(0).U32(0).U32(MovieTimescale).U32((uint)movieDuration);
        mvhd.U32(0x00010000).U16(0x0100).Zeros(10).UnityMatrix().Zeros(24).U32(nextId);

        var children = new List<Box> { new("mvhd", mvhd.ToArray()) };
        children.AddRange(traks);

        var udtaChildren = new List<Box>();
        var preserved = (document.ContainerState as Mp4State)?.PreservedItems ?? [];
        var ilst = ItunesMetadata.Build(document.Metadata, preserved);
        if (ilst.Children!.Count > 0)
        {
            udtaChildren.Add(new Box("meta", new byte[4],
            [
                new Box("hdlr", new PayloadBuilder().FullBox(0, 0).U32(0).Type("mdir").Type("appl").U32(0).U32(0).U8(0).ToArray()),
                ilst,
            ]));
        }

        if (chapters.Count > 0)
            udtaChildren.Add(Mp4Chapters.BuildChpl(chapters));
        if (udtaChildren.Count > 0)
            children.Add(new Box("udta", null, udtaChildren));
        return new Box("moov", null, children);
    }

    private (Box Trak, long MovieDuration) BuildTrak(TrackState t, Dictionary<Track, uint> ids, long offsetDelta, bool force64)
    {
        var model = t.Settings.Model;
        var config = t.Config;
        var n = t.Sizes.Count;

        // Durations from decoding-time deltas; the last sample keeps its own duration.
        var durations = new long[n];
        for (var i = 0; i < n; i++)
        {
            if (i + 1 < n)
                durations[i] = t.Dts[i + 1] - t.Dts[i];
            else
                durations[i] = t.Durations[i] > 0 ? t.Durations[i]
                    : config.Kind == TrackKind.Subtitle ? 0 // e.g. the empty sample ending the last cue
                    : i > 0 ? durations[i - 1] : Math.Max(1, config.DefaultSampleDuration);
        }

        var minCto = n == 0 ? 0 : t.Cto.Min();
        var shift = Math.Max(0, -minCto);
        var t0 = n == 0 ? 0 : t.Dts[0];
        long earliest = long.MaxValue, end = long.MinValue;
        for (var i = 0; i < n; i++)
        {
            var pts = t.Dts[i] + t.Cto[i];
            earliest = Math.Min(earliest, pts);
            end = Math.Max(end, pts + durations[i]);
        }

        if (n == 0)
            earliest = end = 0;
        else if (t.TrimEnd > 0 && t.Dts[n - 1] + t.Cto[n - 1] + durations[n - 1] == end)
        {
            // The edit stops before the encoder padding: the last frame's decoded length (its bitstream says) minus the
            // trim, whatever duration the source gave it; the sample table keeps the whole frame.
            var frame = Math.Max(durations[n - 1], t.LastFrameSamples);
            durations[n - 1] = frame;
            end = t.Dts[n - 1] + t.Cto[n - 1] + Math.Max(0, frame - t.TrimEnd);
        }
        // Presentation starts at the first sample shown: samples before zero, or before the source's own edit, are hidden.
        var start = Math.Max(earliest, (long)Math.Round(t.Settings.VisibleFrom.TotalSeconds * t.Timescale));
        var mediaTime = start - t0 + shift;
        var mediaDuration = n == 0 ? 0 : t.Dts[n - 1] - t0 + durations[n - 1];
        var scale = (double)t.Timescale;
        var emptyEdit = (long)Math.Round(start / scale * MovieTimescale);
        // Rounded up: an edit shorter than the media (by a fraction of the movie timescale) makes players drop the end of
        // the last sample.
        var editDuration = (long)Math.Ceiling(Math.Max(0, end - start) / scale * MovieTimescale - 1e-9);
        var trackDuration = emptyEdit + editDuration;

        // Sample entry.
        var seconds = Math.Max(mediaDuration / scale, 1e-3);
        var stats = new TrackStatistics(
            (uint)t.MaxSampleSize,
            (uint)Math.Min(uint.MaxValue, t.BytesPerSecond.Count == 0 ? 0 : t.BytesPerSecond.Values.Max() * 8),
            (uint)Math.Min(uint.MaxValue, t.TotalBytes * 8 / seconds));
        var canvas = TextCanvas(t);
        var entry = Mp4SampleEntries.Build(config, new Mp4SampleEntries.EntryContext
        {
            FirstSample = t.FirstSample,
            Statistics = stats,
            InBandParameterSets = t.InBandParameterSets,
            TextWidth = canvas.Width,
            TextHeight = canvas.Height,
            Tx3gDisplayFlags = model is SubtitleTrack s ? ForcedFlags(s) : 0,
            EsId = (ushort)t.TrackId,
        });
        if (config.Native is Mp4NativeTrack)
        {
            ApplyModelToNativeEntry(entry, model);
            DolbyVisionEntry.Apply(entry, config.DolbyVisionConfig); // copied entries follow the specification too
            Mp4SampleEntries.AddMissingColorBoxes(entry, config);
        }
        var handler = Mp4SampleEntries.HandlerFor(config);

        var use64 = _settings.Options.Use64BitTimes || trackDuration > uint.MaxValue || mediaDuration > uint.MaxValue;

        // tkhd
        var flags = (model?.Enabled ?? true ? HeaderBoxes.TrackEnabled : 0) | HeaderBoxes.TrackInMovie | HeaderBoxes.TrackInPreview;
        double width = 0, height = 0;
        if (config.Kind == TrackKind.Video)
        {
            var (w, h) = DisplaySize(config, model as VideoTrack);
            (width, height) = (w, h);
        }
        else if (config.Kind == TrackKind.Subtitle)
        {
            (width, height) = (canvas.Width, canvas.Height);
        }

        var volume = model is AudioTrack a ? a.Volume : config.Kind == TrackKind.Audio ? 1.0 : 0;
        var tkhd = new PayloadBuilder().FullBox(use64 ? 1 : 0, flags);
        if (use64)
            tkhd.U64(0).U64(0).U32(t.TrackId).U32(0).U64((ulong)trackDuration);
        else
            tkhd.U32(0).U32(0).U32(t.TrackId).U32(0).U32((uint)trackDuration);
        tkhd.Zeros(8).U16(0).U16((short)(model?.AlternateGroup ?? 0))
            .U16((short)Math.Clamp(Math.Round(volume * 256), 0, short.MaxValue)).U16(0)
            .UnityMatrix().U32((uint)Math.Round(width * 65536)).U32((uint)Math.Round(height * 65536));
        var children = new List<Box> { new("tkhd", tkhd.ToArray()) };

        // References between output tracks.
        if (model is AudioTrack audio)
        {
            if (audio.Fallback is { } f && ids.TryGetValue(f, out var fid))
                AddReference(children, "fall", [fid]);
            if (audio.FollowsSubtitle is { } fs && ids.TryGetValue(fs, out var sid))
                AddReference(children, "folw", [sid]);
        }
        else if (model is SubtitleTrack sub && sub.ForcedTrack is { } ft && ids.TryGetValue(ft, out var forcedId))
        {
            AddReference(children, "forc", [forcedId]);
        }
        else if (config.Kind == TrackKind.Video && DvInfo(config) is { } dv && DolbyVision.IsEnhancementLayerTrack(dv) &&
                 BaseLayerFor(t) is { } baseLayer)
        {
            AddReference(children, "vdep", [baseLayer.TrackId]); // dual-track Dolby Vision: the EL depends on the BL track
        }

        // Edit list (always written: players use it for the presentation duration).
        if (n > 0)
        {
            var edits = new List<(long Duration, long MediaTime)>();
            if (emptyEdit > 0)
                edits.Add((emptyEdit, -1));
            edits.Add((editDuration, mediaTime));
            var elst64 = use64 || mediaTime > int.MaxValue || editDuration > uint.MaxValue;
            var elst = new PayloadBuilder().FullBox(elst64 ? 1 : 0, 0).U32((uint)edits.Count);
            foreach (var (d, m) in edits)
            {
                if (elst64)
                    elst.U64((ulong)d).U64((ulong)m);
                else
                    elst.U32((uint)d).I32((int)m);
                elst.U32(0x00010000);
            }

            children.Add(new Box("edts", null, [new Box("elst", elst.ToArray())]));
        }

        // mdia
        var language = string.IsNullOrWhiteSpace(config.Language) ? LanguageTable.Undetermined : config.Language;
        var iso = LanguageTable.ToIso639_2T(language);
        var mdhd = new PayloadBuilder().FullBox(use64 ? 1 : 0, 0);
        if (use64)
            mdhd.U64(0).U64(0).U32(t.Timescale).U64((ulong)mediaDuration);
        else
            mdhd.U32(0).U32(0).U32(t.Timescale).U32((uint)mediaDuration);
        mdhd.U16(HeaderBoxes.PackLanguage(iso)).U16(0);
        var mdiaChildren = new List<Box> { new("mdhd", mdhd.ToArray()) };
        if (!string.Equals(LanguageTable.ToBcp47(iso), language, StringComparison.OrdinalIgnoreCase) && language != LanguageTable.Undetermined)
            mdiaChildren.Add(new Box("elng", new PayloadBuilder().FullBox(0, 0).Utf8(language, nullTerminated: true).ToArray()));
        var handlerName = handler switch
        {
            "vide" => "VideoHandler",
            "soun" => "SoundHandler",
            "sbtl" or "subp" or "text" => "SubtitleHandler",
            "clcp" => "ClosedCaptionHandler",
            _ => "DataHandler",
        };
        mdiaChildren.Add(new Box("hdlr", new PayloadBuilder().FullBox(0, 0).U32(0).Type(handler).Zeros(12).Utf8(handlerName, nullTerminated: true).ToArray()));

        var mediaHeader = (config.Native as Mp4NativeTrack)?.MediaHeader is { } nativeHeader
            ? BoxParser.ParseSingle(BoxWriter.ToArray(nativeHeader))
            : handler switch
            {
                "vide" => new Box("vmhd", new PayloadBuilder().FullBox(0, 1).Zeros(8).ToArray()),
                "soun" => new Box("smhd", new PayloadBuilder().FullBox(0, 0).U32(0).ToArray()),
                _ => new Box("nmhd", new PayloadBuilder().FullBox(0, 0).ToArray()),
            };
        var dinf = new Box("dinf", null,
            [new Box("dref", new PayloadBuilder().FullBox(0, 0).U32(1).ToArray(), [new Box("url ", new PayloadBuilder().FullBox(0, 1).ToArray())])]);
        var stbl = BuildStbl(t, entry, durations, shift, offsetDelta, force64);
        mdiaChildren.Add(new Box("minf", null, [mediaHeader, dinf, stbl]));
        children.Add(new Box("mdia", null, mdiaChildren));

        // Track name and media characteristics.
        var udta = new List<Box>();
        var name = model?.Name ?? config.Name;
        if (!string.IsNullOrEmpty(name))
            udta.Add(new Box("name", Encoding.UTF8.GetBytes(name)));
        foreach (var tag in model?.MediaCharacteristics.Distinct(StringComparer.Ordinal) ?? [])
            udta.Add(new Box("tagc", Encoding.UTF8.GetBytes(tag)));
        if (udta.Count > 0)
            children.Add(new Box("udta", null, udta));

        return (new Box("trak", null, children), trackDuration);
    }

    private static Box BuildStbl(TrackState t, Box entry, long[] durations, long shift, long offsetDelta, bool force64)
    {
        var n = t.Sizes.Count;
        // ISO/IEC 14496-12: an AudioSampleEntryV1 ('srat' for rates above 65535 Hz) needs a version 1 'stsd'; readers
        // otherwise take it for a QuickTime version 1 sound description.
        var isoV1 = entry.Payload.Length >= 10 && entry.Type is "ipcm" or "fpcm" && entry.Payload[9] == 1;
        var stsd = new Box("stsd", new PayloadBuilder().FullBox(isoV1 ? 1 : 0, 0).U32(1).ToArray(), [entry]);
        if (PcmFrameTables(t, durations, offsetDelta, force64) is { } pcm)
            return new Box("stbl", null, [stsd, .. pcm]);

        // stts (run-length)
        var stts = new List<(uint Count, uint Delta)>();
        foreach (var d in durations)
        {
            var delta = (uint)Math.Clamp(d, 0, uint.MaxValue);
            if (stts.Count > 0 && stts[^1].Delta == delta)
                stts[^1] = (stts[^1].Count + 1, delta);
            else
                stts.Add((1, delta));
        }

        var sttsBox = new PayloadBuilder().FullBox(0, 0).U32((uint)stts.Count);
        foreach (var (count, delta) in stts)
            sttsBox.U32(count).U32(delta);
        var children = new List<Box> { stsd, new("stts", sttsBox.ToArray()) };

        // ctts
        if (t.Cto.Any(c => c + shift != 0))
        {
            var runs = new List<(uint Count, long Offset)>();
            foreach (var c in t.Cto)
            {
                var o = c + shift;
                if (runs.Count > 0 && runs[^1].Offset == o)
                    runs[^1] = (runs[^1].Count + 1, o);
                else
                    runs.Add((1, o));
            }

            var ctts = new PayloadBuilder().FullBox(0, 0).U32((uint)runs.Count);
            foreach (var (count, offset) in runs)
                ctts.U32(count).U32((uint)Math.Clamp(offset, 0, uint.MaxValue));
            children.Add(new Box("ctts", ctts.ToArray()));
        }

        // stss (omitted when every sample is a sync sample)
        if (t.SyncSamples.Count != n)
        {
            var stss = new PayloadBuilder().FullBox(0, 0).U32((uint)t.SyncSamples.Count);
            foreach (var s in t.SyncSamples)
                stss.U32((uint)s);
            children.Add(new Box("stss", stss.ToArray()));
        }

        // stsc (run-length over chunks)
        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        for (var i = 0; i < t.ChunkCounts.Count; i++)
        {
            if (stsc.Count == 0 || stsc[^1].PerChunk != t.ChunkCounts[i])
                stsc.Add(((uint)i + 1, (uint)t.ChunkCounts[i]));
        }

        var stscBox = new PayloadBuilder().FullBox(0, 0).U32((uint)stsc.Count);
        foreach (var (first, per) in stsc)
            stscBox.U32(first).U32(per).U32(1);
        children.Add(new Box("stsc", stscBox.ToArray()));

        // stsz
        var uniform = n > 0 && t.Sizes.All(s => s == t.Sizes[0]);
        var stsz = new PayloadBuilder().FullBox(0, 0).U32(uniform ? (uint)t.Sizes[0] : 0).U32((uint)n);
        if (!uniform)
        {
            foreach (var s in t.Sizes)
                stsz.U32((uint)s);
        }

        children.Add(new Box("stsz", stsz.ToArray()));
        children.Add(SampleTable.BuildChunkOffsets(t.ChunkOffsets.Select(o => o + offsetDelta).ToList(), force64));
        return new Box("stbl", null, children);
    }

    /// <summary>
    /// PCM is written with one sample per audio frame, as ISO/IEC 23003-5 and QuickTime describe it: the blocks the
    /// muxer received become chunks of frames, so the tables stay a few entries long (constant size, unit duration).
    /// Null when the blocks do not hold whole frames at one tick each (the track is then written block by block).
    /// </summary>
    private static List<Box>? PcmFrameTables(TrackState t, long[] durations, long offsetDelta, bool force64)
    {
        var frameBytes = Mp4SampleEntries.PcmFrameBytes(t.Config);
        if (frameBytes <= 0 || t.Timescale != t.Config.SampleRate || t.Sizes.Count == 0)
            return null;
        long frames = 0;
        for (var i = 0; i < t.Sizes.Count; i++)
        {
            if (t.Sizes[i] % frameBytes != 0 || durations[i] != t.Sizes[i] / frameBytes)
                return null;
            frames += t.Sizes[i] / frameBytes;
        }

        if (frames > uint.MaxValue)
            return null;
        var chunkFrames = new List<long>(t.ChunkCounts.Count);
        var sample = 0;
        foreach (var count in t.ChunkCounts)
        {
            long sum = 0;
            for (var k = 0; k < count; k++)
                sum += t.Sizes[sample++] / frameBytes;
            chunkFrames.Add(sum);
        }

        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        for (var i = 0; i < chunkFrames.Count; i++)
        {
            if (stsc.Count == 0 || stsc[^1].PerChunk != chunkFrames[i])
                stsc.Add(((uint)i + 1, (uint)chunkFrames[i]));
        }

        var stscBox = new PayloadBuilder().FullBox(0, 0).U32((uint)stsc.Count);
        foreach (var (first, per) in stsc)
            stscBox.U32(first).U32(per).U32(1);
        return
        [
            new Box("stts", new PayloadBuilder().FullBox(0, 0).U32(1).U32((uint)frames).U32(1).ToArray()),
            new Box("stsc", stscBox.ToArray()),
            new Box("stsz", new PayloadBuilder().FullBox(0, 0).U32((uint)frameBytes).U32((uint)frames).ToArray()),
            SampleTable.BuildChunkOffsets(t.ChunkOffsets.Select(o => o + offsetDelta).ToList(), force64),
        ];
    }

    /// <summary>Applies edited values (colour, forced flags) of the document track to a reused sample entry.</summary>
    private static void ApplyModelToNativeEntry(Box entry, Track? model)
    {
        switch (model)
        {
            case VideoTrack v when entry.Children is not null:
                entry.Children.RemoveAll(c => c.Type == "colr" && c.Payload.Length >= 4 && Box.Latin1.GetString(c.Payload, 0, 4) is "nclx" or "nclc");
                if (v.Color.IsSpecified)
                {
                    var colr = new Box("colr", new PayloadBuilder().Type("nclx").U16(v.Color.Primaries).U16(v.Color.Transfer).U16(v.Color.Matrix)
                        .U8(v.Color.FullRange == true ? 0x80 : 0).ToArray());
                    var index = entry.Children.FindIndex(c => c.Type is "pasp" or "btrt");
                    if (index >= 0)
                        entry.Children.Insert(index, colr);
                    else
                        entry.Children.Add(colr);
                }

                break;
            case SubtitleTrack s when entry.Type == "tx3g" && entry.Payload.Length >= 12:
            {
                var display = BinaryPrimitives.ReadUInt32BigEndian(entry.Payload.AsSpan(8)) & 0x3FFFFFFF;
                BinaryPrimitives.WriteUInt32BigEndian(entry.Payload.AsSpan(8), display | ForcedFlags(s));
                break;
            }
        }
    }

    private static uint ForcedFlags(SubtitleTrack s) => s.ForcedMode switch
    {
        ForcedSubtitleMode.AllSamplesForced => 0x80000000u,
        ForcedSubtitleMode.SomeSamplesForced => 0x40000000u,
        _ => 0u,
    };

    private static (double Width, double Height) DisplaySize(CodecConfig config, VideoTrack? model)
    {
        if (model is { DisplayWidth: > 0, DisplayHeight: > 0 })
            return (model.DisplayWidth, model.DisplayHeight);
        var par = config.ParDenominator > 0 ? (double)config.ParNumerator / config.ParDenominator : 1;
        if (model is { ParNumerator: > 0, ParDenominator: > 0 })
            par = (double)model.ParNumerator / model.ParDenominator;
        return (Math.Round(config.Width * par), config.Height);
    }

    /// <summary>Text box of a subtitle track: the size of the first video track.</summary>
    private (int Width, int Height) TextCanvas(TrackState t)
    {
        if (t.Config.SubtitleWidth > 0 && t.Config.SubtitleHeight > 0 && t.Config.Codec is CodecType.Tx3g or CodecType.VobSub)
            return (t.Config.SubtitleWidth, t.Config.SubtitleHeight);
        var video = _tracks.FirstOrDefault(x => x.Config.Kind == TrackKind.Video);
        if (video is not null)
        {
            var (w, h) = DisplaySize(video.Config, video.Settings.Model as VideoTrack);
            return ((int)w, (int)h);
        }

        return (t.Config.SubtitleWidth > 0 ? t.Config.SubtitleWidth : 640, t.Config.SubtitleHeight > 0 ? t.Config.SubtitleHeight : 480);
    }

    private static void AddReference(Box trak, string type, uint[] ids) => AddReference(trak.Children!, type, ids);

    private static void AddReference(List<Box> trakChildren, string type, uint[] ids)
    {
        var tref = trakChildren.FirstOrDefault(c => c.Type == "tref");
        if (tref is null)
        {
            tref = new Box("tref", null, []);
            trakChildren.Insert(1, tref);
        }

        var b = new PayloadBuilder();
        foreach (var id in ids)
            b.U32(id);
        tref.SetChild(new Box(type, b.ToArray()));
    }

    private static DolbyVisionInfo? DvInfo(CodecConfig config) =>
        config.DolbyVisionConfig is { Length: >= 5 } record ? DolbyVision.ParseConfigurationRecord(record) : null;

    /// <summary>
    /// The base layer track for a Dolby Vision enhancement-layer track: another video track of the same codec that
    /// carries a base layer (no Dolby Vision, or a configuration with bl_present_flag), like GPAC.
    /// </summary>
    private TrackState? BaseLayerFor(TrackState el) => _tracks.FirstOrDefault(o =>
        o != el && o.Config.Kind == TrackKind.Video && o.Config.Codec == el.Config.Codec &&
        (DvInfo(o.Config) is not { } dv || dv.BlPresent));

    private static Box BuildFtyp(string path, IReadOnlyCollection<CodecConfig> configs)
    {
        var codecs = configs.Select(c => c.Codec).ToList();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (major, minor, brands) = ext switch
        {
            ".m4v" => ("M4V ", 1u, new List<string> { "M4V ", "M4A ", "mp42", "isom" }),
            ".m4a" or ".m4r" => ("M4A ", 0u, ["M4A ", "mp42", "isom"]),
            ".m4b" => ("M4B ", 0u, ["M4B ", "mp42", "isom"]),
            ".mov" or ".qt" => ("qt  ", 0x200u, ["qt  "]),
            ".3gp" => ("3gp6", 0u, ["3gp6", "isom", "mp42"]),
            _ => ("mp42", 0u, ["mp42", "isom", "mp41"]),
        };
        if (codecs.Contains(CodecType.Av1) && !brands.Contains("av01"))
            brands.Add("av01");
        if (codecs.Contains(CodecType.Av2))
            brands.AddRange(new[] { "av02", "iso6" }.Where(b => !brands.Contains(b))); // AV2 binding: 'av02' and a structural brand
        foreach (var c in configs.Where(c => c.Kind == TrackKind.Video))
        {
            if (DvInfo(c) is { } dv)
                brands.AddRange(DolbyVision.Mp4Brands(dv, c.Color).Where(b => !brands.Contains(b)));
            if (c.Codec == CodecType.Av1 && c.Hdr10Plus && !c.Hdr10PlusInBlockAdditions && !brands.Contains("cdm4"))
                brands.Add("cdm4"); // HDR10+ metadata OBUs in AV1 ("HDR10+ Metadata in AV1", §3)
        }
        var b = new PayloadBuilder().Type(major).U32(minor);
        foreach (var brand in brands)
            b.Type(brand);
        return new Box("ftyp", b.ToArray());
    }

    private static void WriteFree(Stream stream, long size)
    {
        if (size == 0)
            return;
        if (size < 8)
            throw new InvalidOperationException("A free box needs at least 8 bytes.");
        stream.Write(BoxWriter.Header("free", size));
        var zeros = new byte[(int)Math.Min(size - 8, 64 * 1024)];
        var left = size - 8;
        while (left > 0)
        {
            var n = (int)Math.Min(zeros.Length, left);
            stream.Write(zeros, 0, n);
            left -= n;
        }
    }

    public void Dispose()
    {
        // The stream belongs to the caller.
    }
}
