using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4.Media;

/// <summary>Reads the tracks of an MP4/MOV file from its sample tables and, for fragmented files, its movie fragments.</summary>
internal sealed class Mp4Demuxer : IDemuxer
{
    private readonly FileSampleReader _reader;

    private Mp4Demuxer(string path, FileSampleReader reader, List<ISampleSource> tracks, TimeSpan duration)
    {
        Path = path;
        _reader = reader;
        Tracks = tracks;
        Duration = duration;
    }

    public string Path { get; }

    public string FormatName => "MP4";

    public ContainerKind Container => ContainerKind.Mp4;

    public IReadOnlyList<ISampleSource> Tracks { get; }

    public TimeSpan Duration { get; }

    public static Mp4Demuxer Open(string path)
    {
        var reader = new FileSampleReader(path);
        try
        {
            Mp4Layout layout;
            Dictionary<uint, List<SampleInfo>>? fragments = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            layout = Mp4Layout.Read(fs);
            var moov = layout.Moov.Loaded!;
            var mvhd = moov.Find("mvhd") ?? throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoBox, "mvhd"));
            var movieTimescale = Math.Max(1u, HeaderBoxes.MvhdTimescale(mvhd));
            var traks = moov.FindAll("trak").ToList();
            var chapterIds = traks.SelectMany(t => Mp4Reader.References(t, "chap")).ToHashSet();

            var tracks = new List<ISampleSource>();
            foreach (var trak in traks)
            {
                var tkhd = trak.Find("tkhd");
                var stbl = trak.FindPath("mdia/minf/stbl");
                var entry = stbl?.Find("stsd")?.Children?.FirstOrDefault();
                var mdhd = trak.FindPath("mdia/mdhd");
                if (tkhd is null || stbl is null || entry is null || mdhd is null)
                    continue;
                var id = HeaderBoxes.TkhdTrackId(tkhd);
                if (chapterIds.Contains(id))
                    continue;
                var handler = trak.FindPath("mdia/hdlr") is { } h ? HeaderBoxes.HdlrType(h) : string.Empty;
                var timescale = Math.Max(1u, HeaderBoxes.MdhdTimescale(mdhd));
                var config = Mp4SampleEntries.Describe(trak, entry, handler, timescale);
                var frameTicks = TypicalSampleDuration(stbl);
                if (frameTicks > 0)
                {
                    config = config with { DefaultSampleDuration = frameTicks };
                    if (config.Kind == TrackKind.Video && config.FrameRate == 0)
                        config = config with { FrameRate = (double)timescale / frameTicks };
                }

                var source = new Mp4SampleSource(id, config, stbl, reader);
                if (layout.Boxes.Any(b => b.Type == "moof"))
                {
                    // Fragmented file: the samples are in the movie fragments (after any in the sample tables).
                    fragments ??= Mp4Fragments.Read(fs, layout, moov, traks.ToDictionary(t => HeaderBoxes.TkhdTrackId(t.Find("tkhd")!), EndOfTables));
                    if (fragments.TryGetValue(id, out var more) && more.Count > 0)
                        source.AppendFragments(more);
                }

                source.ReadEditList(trak.FindPath("edts/elst"), movieTimescale);
                var mediaDuration = HeaderBoxes.MdhdDuration(mdhd);
                source.Duration = TimeSpan.FromSeconds((double)(mediaDuration > 0 ? mediaDuration : source.EndOfSamples) / timescale);
                source.RefineCodec();
                tracks.Add(source);
            }

            var duration = TimeSpan.FromSeconds((double)HeaderBoxes.MvhdDuration(mvhd) / movieTimescale);
            return new Mp4Demuxer(path, reader, tracks, duration);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The decoding time after the last sample of a track's sample tables (where its fragments continue).</summary>
    private static long EndOfTables(Box trak)
    {
        if (trak.FindPath("mdia/minf/stbl") is not { } stbl)
            return 0;
        var samples = SampleTable.Expand(stbl);
        return samples.Length == 0 ? 0 : samples[^1].Dts + samples[^1].Duration;
    }

    /// <summary>The most common sample duration in the stts table (0 when there is none).</summary>
    private static long TypicalSampleDuration(Box stbl)
    {
        if (stbl.Find("stts")?.Payload is not { Length: >= 8 } stts)
            return 0;
        var entries = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(4));
        uint best = 0, bestCount = 0;
        for (var i = 0; i < entries && 16 + i * 8 <= stts.Length; i++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(8 + i * 8));
            var delta = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12 + i * 8));
            if (count > bestCount && delta > 0)
                (best, bestCount) = (delta, count);
        }

        return best;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>One track of an MP4 file.</summary>
internal sealed class Mp4SampleSource : ISampleSource
{
    /// <summary>PCM samples are grouped into packets of at most this many frames.</summary>
    private const int PcmGroupLimit = 4096;

    private readonly Box _stbl;
    private readonly FileSampleReader _reader;
    private SampleInfo[]? _samples;
    private int[]? _chunkFirstSample;
    private int _next;
    private readonly Queue<MediaSample> _webVttReady = new();
    private Mp4WebVttCueMerger? _webVtt;

    public Mp4SampleSource(uint trackId, CodecConfig config, Box stbl, FileSampleReader reader)
    {
        TrackId = trackId;
        Config = config;
        _stbl = stbl;
        _reader = reader;
        SampleCountHint = SampleTable.Summary(stbl).Count;
    }

    /// <summary>Adds the samples of the movie fragments; the typical frame duration comes from them when the tables had none.</summary>
    public void AppendFragments(List<SampleInfo> fragments)
    {
        _samples = [.. SampleTable.Expand(_stbl), .. fragments];
        SampleCountHint = _samples.Length;
        if (Config.DefaultSampleDuration == 0)
        {
            var typical = fragments.GroupBy(s => s.Duration).Where(g => g.Key > 0).OrderByDescending(g => g.Count()).Select(g => (long)g.Key).FirstOrDefault();
            if (typical > 0)
            {
                Config = Config with { DefaultSampleDuration = typical };
                if (Config.Kind == TrackKind.Video && Config.FrameRate == 0)
                    Config = Config with { FrameRate = (double)Config.Timescale / typical };
            }
        }
    }

    /// <summary>The decoding time after the last sample.</summary>
    public long EndOfSamples => Samples.Length == 0 ? 0 : Samples[^1].Dts + Samples[^1].Duration;

    public uint TrackId { get; }

    public CodecConfig Config { get; private set; }

    public TimeSpan StartOffset { get; private set; }

    public long MediaStart { get; private set; }

    /// <summary>Media time where the edit list ends the presentation (long.MaxValue when it does not).</summary>
    public long MediaEnd { get; private set; } = long.MaxValue;

    /// <summary>
    /// Audio and subtitle samples presented after the end of the edit list are dropped, as players do. Video samples
    /// are kept: a frame presented after the end may still be a reference for earlier ones.
    /// </summary>
    private bool TrimsEnd => MediaEnd != long.MaxValue && Config.Kind is TrackKind.Audio or TrackKind.Subtitle;

    public TimeSpan Duration { get; set; }

    public long SampleCountHint { get; private set; }

    private SampleInfo[] Samples => _samples ??= SampleTable.Expand(_stbl);

    /// <summary>Reads the initial empty edit (start offset) and the first media edit (media start).</summary>
    public void ReadEditList(Box? elst, uint movieTimescale)
    {
        if (elst is null || elst.Payload.Length < 8)
            return;
        var p = elst.Payload.AsSpan();
        var version = p[0];
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        var entrySize = version == 1 ? 20 : 12;
        long empty = 0;
        for (var i = 0; i < count && 8 + (i + 1) * entrySize <= p.Length; i++)
        {
            var e = p[(8 + i * entrySize)..];
            long duration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(e) : BinaryPrimitives.ReadUInt32BigEndian(e);
            long mediaTime = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(e[8..]) : BinaryPrimitives.ReadInt32BigEndian(e[4..]);
            if (mediaTime == -1)
            {
                empty += duration;
                continue;
            }

            MediaStart = mediaTime;

            // A single media edit shorter than the media ends the presentation early (players drop the rest).
            if (i == count - 1 && duration > 0)
                MediaEnd = mediaTime + (long)Math.Round(duration * (double)Config.Timescale / movieTimescale);
            break;
        }

        StartOffset = TimeSpan.FromSeconds((double)empty / movieTimescale);
    }

    /// <summary>Resolves details that need the first sample (MPEG audio layer, a missing 'av1C').</summary>
    public void RefineCodec()
    {
        if (Config.Codec == CodecType.Av1 && Config.Extradata is not { Length: >= 4 } && SampleCountHint > 0)
        {
            var data = new byte[Samples[0].Size];
            _reader.Read(Samples[0].Offset, data);
            if (Av1.ConfigurationFromSample(data) is { } av1)
            {
                AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_Av1ConfigRebuilt, TrackId));
                Config = Config with { Extradata = av1.Av1C };
            }

            return;
        }

        if (Config.Codec != CodecType.Mp3 || SampleCountHint == 0)
            return;
        var first = Samples[0];
        if (first.Size < 4)
            return;
        Span<byte> header = stackalloc byte[4];
        _reader.Read(first.Offset, header);
        if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            var layer = (header[1] >> 1) & 3;
            Config = Config with { Codec = layer == 2 ? CodecType.Mp2 : layer == 3 ? CodecType.Mp1 : CodecType.Mp3 };
        }
    }

    public void Reset()
    {
        _next = 0;
        _webVtt?.Clear();
        _webVttReady.Clear();
    }

    public MediaSample? ReadNext()
    {
        if (Config.Codec == CodecType.WebVtt)
            return ReadWebVtt();
        var samples = Samples;
        while (_next < samples.Length)
        {
            var s = samples[_next];
            if (TrimsEnd && s.Dts + s.CompositionOffset >= MediaEnd)
            {
                _next = samples.Length;
                return null;
            }

            if (Config.Codec == CodecType.Pcm)
                return ReadPcmGroup(samples);
            _next++;
            var sample = new MediaSample
            {
                Dts = s.Dts,
                CtsOffset = s.CompositionOffset,
                Duration = s.Duration,
                IsSync = s.IsSync,
                Reader = _reader,
                Position = s.Offset,
                StoredSize = s.Size,
            };

            // An edit ending inside the last presented audio sample trims its end (encoder padding).
            if (TrimsEnd && Config.Kind == TrackKind.Audio && sample.Pts + s.Duration > MediaEnd)
                sample.TrimEnd = Math.Min(s.Duration, sample.Pts + s.Duration - MediaEnd);

            return sample;
        }

        return null;
    }

    /// <summary>
    /// ISO/IEC 14496-30 samples: one sample per cue (text, with its settings), cues repeated in consecutive samples
    /// merged back into one; 'vtte' (no cue) samples are gaps.
    /// </summary>
    private MediaSample? ReadWebVtt()
    {
        var samples = Samples;
        var merger = _webVtt ??= new Mp4WebVttCueMerger();
        while (_webVttReady.Count == 0)
        {
            if (_next >= samples.Length || (TrimsEnd && samples[_next].Dts + samples[_next].CompositionOffset >= MediaEnd))
            {
                _next = samples.Length;
                foreach (var cue in merger.Complete())
                    _webVttReady.Enqueue(cue);
                break;
            }

            var s = samples[_next++];
            var data = new byte[s.Size];
            _reader.Read(s.Offset, data);
            var start = s.Dts + s.CompositionOffset;
            foreach (var cue in merger.Add(start, start + s.Duration, Mp4WebVtt.Parse(data)))
                _webVttReady.Enqueue(cue);
        }

        return _webVttReady.TryDequeue(out var next) ? next : null;
    }

    private MediaSample ReadPcmGroup(SampleInfo[] samples)
    {
        _chunkFirstSample ??= ChunkStarts(samples);
        var first = samples[_next];
        long size = 0, duration = 0;
        var end = _next;
        while (end < samples.Length && end - _next < PcmGroupLimit)
        {
            var s = samples[end];
            if (end > _next && (s.Offset != first.Offset + size || Array.BinarySearch(_chunkFirstSample, end) >= 0))
                break;
            if (TrimsEnd && s.Dts >= MediaEnd)
                break;
            size += s.Size;
            duration += s.Duration;
            end++;
        }

        _next = end;
        return new MediaSample
        {
            Dts = first.Dts,
            Duration = duration,
            IsSync = true,
            Reader = _reader,
            Position = first.Offset,
            StoredSize = (int)size,
        };
    }

    private static int[] ChunkStarts(SampleInfo[] samples)
    {
        var starts = new List<int>();
        for (var i = 0; i < samples.Length; i++)
        {
            if (i == 0 || samples[i].Offset != samples[i - 1].Offset + samples[i - 1].Size)
                starts.Add(i);
        }

        return [.. starts];
    }
}
