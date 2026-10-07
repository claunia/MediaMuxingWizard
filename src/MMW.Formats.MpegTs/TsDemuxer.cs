using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.MpegTs;

/// <summary>MPEG transport streams (broadcast .ts, Blu-ray/AVCHD .m2ts) as an <see cref="IDemuxer"/>.</summary>
public static class TsFormat
{
    private static int s_registered;

    public static IReadOnlyList<string> Extensions { get; } = [".ts", ".m2ts", ".mts", ".m2t", ".tts", ".trp"];

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
            MediaFormatRegistry.Register(new TsDemuxerFactory());
    }
}

/// <summary>Opens MPEG transport streams.</summary>
public sealed class TsDemuxerFactory : IDemuxerFactory
{
    public string Name => "MPEG-TS";

    public int Probe(string path, ReadOnlySpan<byte> header) =>
        TsPacketReader.Detect(header) is null ? 0 : TsFormat.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ? 95 : 70;

    public IDemuxer Open(string path, DemuxOptions? options = null) => TsDemuxer.Open(path);
}

internal sealed class TsDemuxer : IDemuxer
{
    /// <summary>Bytes read from the start to find the programs and describe their streams.</summary>
    private const long ProbeBytes = 96L << 20;

    /// <summary>Bytes read from the end to find the last timestamps (duration).</summary>
    private const long TailBytes = 8L << 20;

    private TsScanner? _shared;

    private TsDemuxer(string path, int stride, int offset)
    {
        Path = path;
        Stride = stride;
        Offset = offset;
    }

    public string Path { get; }

    public string FormatName => Stride == 192 ? "BDAV MPEG-TS" : "MPEG-TS";

    public ContainerKind Container => ContainerKind.Unknown;

    public IReadOnlyList<ISampleSource> Tracks { get; private set; } = [];

    public TimeSpan Duration { get; private set; }

    internal int Stride { get; }

    internal int Offset { get; }

    /// <summary>The scanner shared by tracks read together from the start.</summary>
    internal TsScanner Shared => _shared ??= new TsScanner(this);

    public static TsDemuxer Open(string path)
    {
        Span<byte> header = stackalloc byte[192 * 8];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (TsPacketReader.Detect(header[..read]) is not var (stride, offset))
            throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' is not an MPEG transport stream.");
        var demuxer = new TsDemuxer(path, stride, offset);
        demuxer.Probe();
        return demuxer;
    }

    internal TsPacketReader OpenReader(long position = -1) =>
        new(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan), Stride,
            position < 0 ? Offset : position);

    /// <summary>
    /// Picks the program (the one with video and the most supported streams, for multi-program broadcast captures),
    /// describes its streams and measures them.
    /// </summary>
    private void Probe()
    {
        var streams = ChooseProgram() ?? throw new InvalidDataException($"'{System.IO.Path.GetFileName(Path)}' has no program map table.");
        var specs = Specs(streams, log: true);
        var probes = specs.Select(spec => (Spec: spec, Stream: spec.Create(null), Out: new Queue<MediaSample>())).ToList();
        var byPid = probes.GroupBy(p => p.Spec.Info.Pid).ToDictionary(g => g.Key, g => (Pes: new PesAssembler(), Streams: g.ToList()));
        var first = new Dictionary<uint, double>(); // seconds of the first sample, by track
        var unwrap = new TimestampUnwrapper();

        using (var reader = OpenReader())
        {
            while (reader.Position - Offset < ProbeBytes && reader.Next(out var h, out var payload))
            {
                if (!byPid.TryGetValue(h.Pid, out var p) || p.Pes.Add(payload, h.PayloadStart, h.RandomAccess) is not { } pes)
                    continue;
                foreach (var s in p.Streams)
                    Feed(s.Stream, Unwrapped(pes, unwrap), s.Out, first, s.Spec.TrackId);
                if (probes.All(x => x.Stream.Ready))
                    break;
            }

            // Streams still incomplete get what was assembled so far.
            foreach (var (_, p) in byPid)
            {
                if (p.Streams.All(s => s.Stream.Ready) || p.Pes.Flush() is not { } pes)
                    continue;
                foreach (var s in p.Streams)
                    Feed(s.Stream, Unwrapped(pes, unwrap), s.Out, first, s.Spec.TrackId);
            }
        }

        var usable = probes.Where(p => p.Stream.Ready || p.Stream is NalVideoStream { HasConfig: true }).ToList();
        if (usable.Count == 0)
            throw new InvalidDataException($"'{System.IO.Path.GetFileName(Path)}' has no stream that can be read.");
        var start = usable.Where(p => first.ContainsKey(p.Spec.TrackId)).Select(p => first[p.Spec.TrackId]).DefaultIfEmpty(0).Min();
        var last = LastTimestamps(usable.Select(p => p.Spec.Info.Pid).ToHashSet(), (long)Math.Round(start * 90000));

        var tracks = new List<ISampleSource>();
        foreach (var p in usable.OrderBy(p => p.Spec.Info.Pid).ThenBy(p => p.Spec.TrackId))
        {
            var config = p.Stream.Describe();
            // From the first sample to the end of the last one (its start plus one frame).
            var frame = config.DefaultSampleDuration > 0 && config.Timescale > 0 ? config.DefaultSampleDuration / (double)config.Timescale : 0;
            var begin = first.TryGetValue(p.Spec.TrackId, out var b) ? b : start;
            var duration = last.TryGetValue(p.Spec.Info.Pid, out var end)
                ? TimeSpan.FromSeconds(Math.Max(0, end / 90000.0 - begin + frame))
                : TimeSpan.Zero;
            var hint = config.DefaultSampleDuration > 0 && config.Timescale > 0
                ? (long)(duration.TotalSeconds * config.Timescale / config.DefaultSampleDuration)
                : -1;
            // Opus: the first unit's start trim (the pre-skip) is decoded but not played.
            var skip = config.Codec == CodecType.Opus ? Opus.Describe(config.Extradata).PreSkip : 0;
            tracks.Add(new TsTrackSource(this, p.Spec, config, (long)Math.Round(start * config.Timescale) + skip, duration, hint));
        }

        Tracks = tracks;
        Duration = tracks.Select(t => t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max();
    }

    /// <summary>
    /// Reads the PAT and every PMT, and returns the streams of the program to import: the one with a supported video
    /// stream and the most supported streams (broadcast captures may carry a whole multiplex).
    /// </summary>
    private List<TsStreamInfo>? ChooseProgram()
    {
        var pat = new PsiAssembler();
        var pmtAssemblers = new Dictionary<int, PsiAssembler>();
        var pmts = new Dictionary<int, List<TsStreamInfo>>();
        List<(int Program, int PmtPid)>? programs = null;
        using var reader = OpenReader();
        while (reader.Position - Offset < ProbeBytes && reader.Next(out var h, out var payload))
        {
            if (h.Pid == 0 && programs is null && pat.Add(payload, h.PayloadStart) is { } patSection && Psi.ParsePat(patSection) is { Count: > 0 } list)
            {
                programs = list;
                foreach (var (_, pmtPid) in list)
                    pmtAssemblers.TryAdd(pmtPid, new PsiAssembler());
            }
            else if (pmtAssemblers.TryGetValue(h.Pid, out var assembler) && !pmts.ContainsKey(h.Pid) &&
                     assembler.Add(payload, h.PayloadStart) is { } section && Psi.ParsePmt(section) is { } pmt)
            {
                pmts[h.Pid] = pmt;
            }

            if (programs is not null && programs.All(p => pmts.ContainsKey(p.PmtPid)))
                break;
        }

        if (programs is null || pmts.Count == 0)
            return null;
        var candidates = programs.Where(p => pmts.ContainsKey(p.PmtPid)).Select(p => (p.Program, Streams: pmts[p.PmtPid])).ToList();
        var best = candidates
            .OrderByDescending(c => Specs(c.Streams, log: false).Any(s => s.IsVideo))
            .ThenByDescending(c => Specs(c.Streams, log: false).Count)
            .First();
        if (candidates.Count > 1)
            AppLog.Info($"{System.IO.Path.GetFileName(Path)}: {candidates.Count} programs; importing program {best.Program}.");
        return best.Streams;
    }

    /// <summary>The tracks a program offers: one per supported stream, one per teletext subtitle page.</summary>
    private List<TsTrackSpec> Specs(List<TsStreamInfo> streams, bool log)
    {
        var specs = new List<TsTrackSpec>();
        foreach (var info in streams)
        {
            var pages = TeletextStream.SubtitlePages(info).ToList();
            if (pages.Count > 0)
            {
                foreach (var (magazine, page, language, hearingImpaired) in pages)
                {
                    var trackId = (uint)info.Pid | (uint)((magazine << 8 | page) + 1) << 16;
                    specs.Add(new TsTrackSpec(info, trackId, false, _ => new TeletextStream(info, magazine, page, language, hearingImpaired)));
                }

                continue;
            }

            if (TsStream.Create(info) is { } probe)
                specs.Add(new TsTrackSpec(info, (uint)info.Pid, probe is NalVideoStream or MpegVideoStream or Av1TsStream, known => TsStream.Create(info, known)!));
            else if (log)
                AppLog.Info($"{System.IO.Path.GetFileName(Path)}: stream 0x{info.Pid:X} of type 0x{info.StreamType:X2} is not supported.");
        }

        return specs;
    }

    private static Pes Unwrapped(Pes pes, TimestampUnwrapper unwrap) =>
        pes.Pts is null ? pes : pes with { Pts = unwrap.Unwrap(pes.Pts.Value), Dts = pes.Dts is { } d ? unwrap.Unwrap(d) : null };

    private static void Feed(TsStream stream, Pes pes, Queue<MediaSample> output, Dictionary<uint, double> first, uint trackId)
    {
        stream.OnPes(pes, output);
        while (output.Count > 0)
        {
            // The earliest presentation time: with reordering, the first frame decoded is not the first one shown.
            var pts = output.Dequeue().Pts / (double)Math.Max(1, stream.Timescale);
            first[trackId] = first.TryGetValue(trackId, out var earliest) ? Math.Min(earliest, pts) : pts;
        }
    }

    /// <summary>The last PTS (90 kHz, unwrapped near <paramref name="reference"/>) of each stream, from the end of the file.</summary>
    private Dictionary<int, long> LastTimestamps(HashSet<int> pids, long reference)
    {
        var result = new Dictionary<int, long>();
        var length = new FileInfo(Path).Length;
        var from = Math.Max(Offset, length - TailBytes);
        from = Offset + (from - Offset) / Stride * Stride;
        using var reader = OpenReader(from);
        var assemblers = pids.ToDictionary(p => p, _ => new PesAssembler());
        var wrap = 1L << 33;
        while (reader.Next(out var h, out var payload))
        {
            if (!h.PayloadStart || !assemblers.ContainsKey(h.Pid) || payload.Length < 14)
                continue;
            if (PesAssembler.Parse(payload.ToArray(), false) is { Pts: { } raw })
            {
                var k = Math.Max(0, (long)Math.Ceiling((reference - raw) / (double)wrap));
                result[h.Pid] = raw + k * wrap;
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var t in Tracks.OfType<TsTrackSource>())
            t.Dispose();
        _shared?.Dispose();
    }
}

/// <summary>Reads packets in order and dispatches the PES packets of the attached tracks.</summary>
internal sealed class TsScanner : IDisposable
{
    private readonly TsDemuxer _demuxer;
    private readonly Dictionary<int, List<(TsTrackSource Track, PesAssembler Pes)>> _attached = [];
    private readonly TimestampUnwrapper _unwrap = new();
    private TsPacketReader? _reader;
    private bool _ended;

    public TsScanner(TsDemuxer demuxer) => _demuxer = demuxer;

    public bool Started => _reader is not null;

    public void Attach(TsTrackSource track)
    {
        if (!_attached.TryGetValue(track.Pid, out var list))
            _attached[track.Pid] = list = [];
        list.Add((track, new PesAssembler()));
    }

    public void Detach(TsTrackSource track)
    {
        if (_attached.TryGetValue(track.Pid, out var list))
            list.RemoveAll(a => a.Track == track);
    }

    /// <summary>Processes the next packet; false at the end of the file (after the last PES were delivered).</summary>
    public bool Advance()
    {
        if (_ended)
            return false;
        _reader ??= _demuxer.OpenReader();
        while (_reader.Next(out var h, out var payload))
        {
            if (!_attached.TryGetValue(h.Pid, out var list) || list.Count == 0)
                continue;
            foreach (var (track, assembler) in list.ToList())
            {
                if (assembler.Add(payload, h.PayloadStart, h.RandomAccess) is { } pes)
                    track.OnPes(Unwrapped(pes));
            }

            return true;
        }

        _ended = true;
        foreach (var (track, assembler) in _attached.Values.SelectMany(l => l).ToList())
        {
            if (assembler.Flush() is { } pes)
                track.OnPes(Unwrapped(pes));
            track.OnEnd();
        }

        return false;
    }

    private Pes Unwrapped(Pes pes) =>
        pes.Pts is null ? pes : pes with { Pts = _unwrap.Unwrap(pes.Pts.Value), Dts = pes.Dts is { } d ? _unwrap.Unwrap(d) : null };

    public void Dispose() => _reader?.Dispose();
}

/// <summary>One elementary stream of a transport stream.</summary>
internal sealed class TsTrackSource : ISampleSource, IDisposable
{
    private readonly TsDemuxer _demuxer;
    private readonly TsTrackSpec _spec;
    private readonly Queue<MediaSample> _out = new();
    private TsStream? _stream;
    private TsScanner? _scanner;
    private bool _ownScanner;
    private bool _ended;

    public TsTrackSource(TsDemuxer demuxer, TsTrackSpec spec, CodecConfig config, long mediaStart, TimeSpan duration, long sampleCountHint)
    {
        _demuxer = demuxer;
        _spec = spec;
        Config = config;
        MediaStart = mediaStart;
        Duration = duration;
        SampleCountHint = sampleCountHint;
    }

    /// <summary>The PID (teletext pages: the PID with the page number in the high bits).</summary>
    public uint TrackId => _spec.TrackId;

    public int Pid => _spec.Info.Pid;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    /// <summary>The program's earliest presentation time in this track's timescale: all tracks share the origin.</summary>
    public long MediaStart { get; }

    public TimeSpan Duration { get; }

    public long SampleCountHint { get; }

    public MediaSample? ReadNext()
    {
        if (_scanner is null)
        {
            _stream = _spec.Create(Config);
            if (_stream is AudioStream audio)
                audio.WithConfig(Config);
            _ownScanner = _demuxer.Shared.Started;
            _scanner = _ownScanner ? new TsScanner(_demuxer) : _demuxer.Shared;
            _scanner.Attach(this);
        }

        while (_out.Count == 0)
        {
            if (_ended || !_scanner.Advance())
            {
                _ended = true;
                if (_out.Count == 0)
                    return null;
            }
        }

        return _out.Dequeue();
    }

    internal void OnPes(Pes pes) => _stream!.OnPes(pes, _out);

    internal void OnEnd()
    {
        _stream!.Flush(_out);
        _ended = true;
    }

    public void Reset()
    {
        _scanner?.Detach(this);
        if (_ownScanner)
            _scanner?.Dispose();
        _scanner = null;
        _stream = null;
        _out.Clear();
        _ended = false;
    }

    public void Dispose() => Reset();
}

/// <summary>A track of the chosen program: its stream, ID and how to create its handler (with the probed configuration).</summary>
internal sealed record TsTrackSpec(TsStreamInfo Info, uint TrackId, bool IsVideo, Func<CodecConfig?, TsStream> Create);
