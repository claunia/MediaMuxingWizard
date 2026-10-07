using MMW.Core.Diagnostics;
using MMW.Core.Media;
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

    /// <summary>Finds the first program, describes its streams and measures them.</summary>
    private void Probe()
    {
        var pat = new PsiAssembler();
        var pmt = new PsiAssembler();
        var pmtPid = -1;
        List<TsStreamInfo>? streams = null;
        var probes = new Dictionary<int, (TsStream Stream, PesAssembler Pes, Queue<MediaSample> Out)>();
        var first = new Dictionary<int, double>(); // seconds of the first sample
        var unwrap = new TimestampUnwrapper();

        using (var reader = OpenReader())
        {
            while (reader.Position - Offset < ProbeBytes && reader.Next(out var h, out var payload))
            {
                if (streams is null)
                {
                    if (h.Pid == 0 && pat.Add(payload, h.PayloadStart) is { } patSection && Psi.ParsePat(patSection) is { Count: > 0 } programs)
                        pmtPid = programs[0].PmtPid;
                    else if (h.Pid == pmtPid && pmt.Add(payload, h.PayloadStart) is { } pmtSection)
                    {
                        streams = Psi.ParsePmt(pmtSection);
                        foreach (var info in streams ?? [])
                        {
                            if (TsStream.Create(info) is { } stream)
                                probes[info.Pid] = (stream, new PesAssembler(), new Queue<MediaSample>());
                            else
                                AppLog.Info($"{System.IO.Path.GetFileName(Path)}: stream 0x{info.Pid:X} of type 0x{info.StreamType:X2} is not supported.");
                        }
                    }

                    continue;
                }

                if (!probes.TryGetValue(h.Pid, out var p) || p.Pes.Add(payload, h.PayloadStart, h.RandomAccess) is not { } pes)
                    continue;
                Feed(p.Stream, Unwrapped(pes, unwrap), p.Out, first, h.Pid);
                if (probes.Values.All(x => x.Stream.Ready))
                    break;
            }

            // Streams still incomplete get what was assembled so far.
            foreach (var (pid, p) in probes)
            {
                if (!p.Stream.Ready && p.Pes.Flush() is { } pes)
                    Feed(p.Stream, Unwrapped(pes, unwrap), p.Out, first, pid);
            }
        }

        if (streams is null)
            throw new InvalidDataException($"'{System.IO.Path.GetFileName(Path)}' has no program map table.");

        var usable = probes.Where(p => p.Value.Stream.Ready || p.Value.Stream is NalVideoStream { HasConfig: true }).ToList();
        if (usable.Count == 0)
            throw new InvalidDataException($"'{System.IO.Path.GetFileName(Path)}' has no stream that can be read.");
        var start = usable.Where(p => first.ContainsKey(p.Key)).Select(p => first[p.Key]).DefaultIfEmpty(0).Min();
        var last = LastTimestamps(usable.Select(p => p.Key).ToHashSet(), (long)Math.Round(start * 90000));

        var tracks = new List<ISampleSource>();
        foreach (var (pid, p) in usable.OrderBy(p => p.Key))
        {
            var config = p.Stream.Describe();
            // From the first sample to the end of the last one (its start plus one frame).
            var frame = config.DefaultSampleDuration > 0 && config.Timescale > 0 ? config.DefaultSampleDuration / (double)config.Timescale : 0;
            var duration = last.TryGetValue(pid, out var end) && first.TryGetValue(pid, out var begin)
                ? TimeSpan.FromSeconds(Math.Max(0, end / 90000.0 - begin + frame))
                : TimeSpan.Zero;
            var hint = config.DefaultSampleDuration > 0 && config.Timescale > 0
                ? (long)(duration.TotalSeconds * config.Timescale / config.DefaultSampleDuration)
                : -1;
            tracks.Add(new TsTrackSource(this, streams.First(s => s.Pid == pid), config, (long)Math.Round(start * config.Timescale), duration, hint));
        }

        Tracks = tracks;
        Duration = tracks.Select(t => t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max();
    }

    private static Pes Unwrapped(Pes pes, TimestampUnwrapper unwrap) =>
        pes.Pts is null ? pes : pes with { Pts = unwrap.Unwrap(pes.Pts.Value), Dts = pes.Dts is { } d ? unwrap.Unwrap(d) : null };

    private static void Feed(TsStream stream, Pes pes, Queue<MediaSample> output, Dictionary<int, double> first, int pid)
    {
        stream.OnPes(pes, output);
        while (output.Count > 0)
        {
            var sample = output.Dequeue();
            if (!first.ContainsKey(pid))
                first[pid] = sample.Pts / (double)Math.Max(1, stream.Timescale);
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
    private readonly Dictionary<int, (TsTrackSource Track, PesAssembler Pes)> _attached = [];
    private readonly TimestampUnwrapper _unwrap = new();
    private TsPacketReader? _reader;
    private bool _ended;

    public TsScanner(TsDemuxer demuxer) => _demuxer = demuxer;

    public bool Started => _reader is not null;

    public void Attach(TsTrackSource track) => _attached[(int)track.TrackId] = (track, new PesAssembler());

    public void Detach(TsTrackSource track) => _attached.Remove((int)track.TrackId);

    /// <summary>Processes the next packet; false at the end of the file (after the last PES were delivered).</summary>
    public bool Advance()
    {
        if (_ended)
            return false;
        _reader ??= _demuxer.OpenReader();
        while (_reader.Next(out var h, out var payload))
        {
            if (!_attached.TryGetValue(h.Pid, out var a))
                continue;
            if (a.Pes.Add(payload, h.PayloadStart, h.RandomAccess) is { } pes)
                a.Track.OnPes(Unwrapped(pes));
            return true;
        }

        _ended = true;
        foreach (var (track, assembler) in _attached.Values.ToList())
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
    private readonly TsStreamInfo _info;
    private readonly Queue<MediaSample> _out = new();
    private TsStream? _stream;
    private TsScanner? _scanner;
    private bool _ownScanner;
    private bool _ended;

    public TsTrackSource(TsDemuxer demuxer, TsStreamInfo info, CodecConfig config, long mediaStart, TimeSpan duration, long sampleCountHint)
    {
        _demuxer = demuxer;
        _info = info;
        Config = config;
        MediaStart = mediaStart;
        Duration = duration;
        SampleCountHint = sampleCountHint;
    }

    /// <summary>The PID.</summary>
    public uint TrackId => (uint)_info.Pid;

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
            _stream = TsStream.Create(_info, Config) ?? throw new InvalidDataException("Unsupported stream.");
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
