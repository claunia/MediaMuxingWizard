using System.Buffers.Binary;
using System.IO.Compression;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;
using static MMW.Formats.Matroska.Media.MatroskaMediaIds;

namespace MMW.Formats.Matroska.Media;

/// <summary>
/// Reads the tracks of a Matroska/WebM file.
/// </summary>
/// <remarks>
/// <para>
/// Clusters are scanned in file order by a scanner shared by all tracks being read; only block headers are read
/// and samples are queued as (position, size) descriptors whose payload is loaded when the muxer copies it. Laced
/// and zlib-compressed blocks are loaded at scan time; header-stripped blocks get their stripped bytes as a prefix.
/// </para>
/// <para>
/// Timestamps: Matroska stores presentation times in TimestampScale units. Each track gets an integer timescale
/// (the frame grid for video with a DefaultDuration, e.g. 24000 for 23.976 fps; the sample rate for audio; 1000 for
/// subtitles) and timestamps are snapped to the frame grid when they are within the file's timestamp precision.
/// For codecs with B-frames the decoding times are derived from the presentation times: blocks are stored in decoding
/// order, so the n-th decoding time is the n-th smallest presentation time, found with a sliding window over the
/// next <see cref="ReorderWindow"/> frames; the composition offset is the difference (it may be negative; muxers
/// normalise it).
/// </para>
/// </remarks>
internal sealed class MatroskaDemuxer : IDemuxer
{
    /// <summary>Frames of look-ahead used to derive decoding times from presentation times.</summary>
    public const int ReorderWindow = 16;

    private readonly FileSampleReader _reader;
    private readonly List<MatroskaTrackSource> _tracks = [];

    /// <summary>The tracks as exposed: AV2 blocks (whole temporal units, possibly several) become one sample per unit.</summary>
    private readonly List<ISampleSource> _sources = [];

    private MatroskaDemuxer(string path, FileSampleReader reader, MatroskaLayout layout, TimeSpan duration)
    {
        Path = path;
        _reader = reader;
        Ebml = new EbmlReader(reader.Handle);
        TimestampScale = layout.TimestampScale;
        Clusters = layout.Elements.Where(e => e.Id == Cluster).Select(e => (e.Position, e.HeaderLength, e.End)).ToList();
        Duration = duration;
        Shared = new ClusterScanner(this);
    }

    public string Path { get; }

    public string FormatName => "Matroska";

    public ContainerKind Container => ContainerKind.Matroska;

    public IReadOnlyList<ISampleSource> Tracks => _sources;

    public TimeSpan Duration { get; }

    internal EbmlReader Ebml { get; }

    internal FileSampleReader Reader => _reader;

    internal ulong TimestampScale { get; }

    internal List<(long Position, int HeaderLength, long End)> Clusters { get; }

    /// <summary>The scanner tracks join when they start reading from the beginning.</summary>
    internal ClusterScanner Shared { get; }

    public static MatroskaDemuxer Open(string path)
    {
        var (document, layout) = MatroskaReader.Read(path, CancellationToken.None);
        if (layout.ScanProblem is not null && !layout.Elements.Any(e => e.Id == Cluster))
            throw new InvalidDataException(layout.ScanProblem);
        var reader = new FileSampleReader(path);
        try
        {
            var demuxer = new MatroskaDemuxer(path, reader, layout, document.Duration);
            foreach (var entry in layout.Tracks)
            {
                var config = MatroskaCodecMapping.Describe(entry.Payload, path);
                var encoding = ContentEncodingInfo.Parse(entry.Payload);
                var defaultDuration = EbmlParser.Children(entry.Payload).GetUInt(DefaultDuration, 0);
                demuxer._tracks.Add(new MatroskaTrackSource(demuxer, (uint)entry.TrackNumber, config, encoding, defaultDuration, document.Duration));
            }

            foreach (var t in demuxer._tracks)
                t.Prepare();
            foreach (var t in demuxer._tracks)
                demuxer._sources.Add(t.Config.Codec == CodecType.Av2 ? new Av2TemporalUnitSource(t) : t);
            return demuxer;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    internal MatroskaTrackSource? Track(ulong number) => _tracks.FirstOrDefault(t => t.TrackId == number);

    /// <summary>Reads the first frame of a track (decoded), scanning at most a few clusters.</summary>
    internal byte[]? PeekFirstFrame(MatroskaTrackSource track)
    {
        byte[]? found = null;
        var scanner = new ClusterScanner(this, maxClusters: 64);
        scanner.Peek = (number, frame) =>
        {
            if (number == track.TrackId && found is null)
                found = frame;
        };
        while (found is null && scanner.Advance())
        {
        }

        return found;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>A frame of a block: lazily read (position/size) or already in memory.</summary>
internal readonly record struct BlockFrame(long Position, int Size, ReadOnlyMemory<byte> Data, bool InMemory);

/// <summary>A parsed block (SimpleBlock or BlockGroup).</summary>
internal sealed record BlockInfo(ulong Track, long Timestamp, bool Keyframe, bool Discardable, long? Duration, List<BlockFrame> Frames)
{
    /// <summary>BlockAdditions of a BlockGroup (BlockMore elements); null when there are none.</summary>
    public List<BlockAddition>? Additions { get; init; }

    /// <summary>DiscardPadding of a BlockGroup: nanoseconds at the end of the block not to be played; 0 for none.</summary>
    public long DiscardPaddingNs { get; init; }
}

/// <summary>Walks the clusters of a file in order and dispatches blocks to the attached tracks.</summary>
internal sealed class ClusterScanner
{
    private readonly MatroskaDemuxer _demuxer;
    private readonly int _maxClusters;
    private readonly Dictionary<ulong, MatroskaTrackSource> _attached = [];
    private int _cluster = -1;
    private long _pos;
    private long _end;
    private long _clusterTimestamp;

    public ClusterScanner(MatroskaDemuxer demuxer, int maxClusters = int.MaxValue)
    {
        _demuxer = demuxer;
        _maxClusters = maxClusters;
    }

    /// <summary>True once the scanner has read past the start of the first cluster.</summary>
    public bool Started => _cluster >= 0;

    /// <summary>Peek mode: called with every frame (loaded) instead of dispatching to attached tracks.</summary>
    public Action<ulong, byte[]>? Peek { get; set; }

    public void Attach(MatroskaTrackSource track) => _attached[track.TrackId] = track;

    public void Detach(MatroskaTrackSource track) => _attached.Remove(track.TrackId);

    /// <summary>Reads the next block; false at the end of the file.</summary>
    public bool Advance()
    {
        var ebml = _demuxer.Ebml;
        while (true)
        {
            if (_cluster < 0 || _pos >= _end)
            {
                if (++_cluster >= _demuxer.Clusters.Count || _cluster >= _maxClusters)
                {
                    _cluster = Math.Min(_cluster, _demuxer.Clusters.Count);
                    return false;
                }

                var (position, headerLength, end) = _demuxer.Clusters[_cluster];
                _pos = position + headerLength;
                _end = end;
                _clusterTimestamp = 0;
                continue;
            }

            if (!ebml.TryReadHeader(_pos, _end, out var h) || h.IsUnknownSize)
            {
                _pos = _end;
                continue;
            }

            var elementEnd = Math.Min(h.End, _end);
            _pos = elementEnd;
            switch (h.Id)
            {
                case Timestamp:
                    _clusterTimestamp = (long)EbmlParser.ReadUInt(ebml.ReadData(h));
                    continue;
                case SimpleBlock:
                {
                    var block = ReadBlock(h.DataPosition, elementEnd - h.DataPosition, simple: true, keyframe: false, duration: null, discardable: false);
                    if (block is not null)
                    {
                        Dispatch(block);
                        return true;
                    }

                    continue;
                }

                case BlockGroup:
                {
                    var block = ReadBlockGroup(h, elementEnd);
                    if (block is not null)
                    {
                        Dispatch(block);
                        return true;
                    }

                    continue;
                }

                default:
                    continue;
            }
        }
    }

    private void Dispatch(BlockInfo block)
    {
        if (Peek is { } peek)
        {
            var track = _demuxer.Track(block.Track);
            foreach (var f in block.Frames)
            {
                var data = f.InMemory ? f.Data.ToArray() : _demuxer.Reader.ReadAvailable(f.Position, f.Size);
                peek(block.Track, track?.Encoding.Decode(data) ?? data);
            }

            return;
        }

        if (_attached.TryGetValue(block.Track, out var source))
            source.OnBlock(block);
    }

    private BlockInfo? ReadBlockGroup(EbmlElementHeader group, long end)
    {
        var ebml = _demuxer.Ebml;
        long blockPos = -1, blockSize = 0;
        long? duration = null;
        var hasReference = false;
        List<BlockAddition>? additions = null;
        long discardPadding = 0;
        var pos = group.DataPosition;
        while (pos < end && ebml.TryReadHeader(pos, end, out var child) && !child.IsUnknownSize)
        {
            switch (child.Id)
            {
                case MatroskaMediaIds.Block:
                    blockPos = child.DataPosition;
                    blockSize = (long)child.Size;
                    break;
                case BlockDuration:
                    duration = (long)EbmlParser.ReadUInt(ebml.ReadData(child));
                    break;
                case ReferenceBlock:
                    hasReference = true;
                    break;
                case BlockAdditions:
                    additions = ReadBlockAdditions(ebml.ReadData(child));
                    break;
                case DiscardPadding:
                    discardPadding = EbmlParser.ReadInt(ebml.ReadData(child));
                    break;
            }

            pos = child.End;
        }

        if (blockPos < 0)
            return null;
        var block = ReadBlock(blockPos, blockSize, simple: false, keyframe: !hasReference, duration: duration, discardable: false);
        if (block is null)
            return null;
        return block with { Additions = additions is { Count: > 0 } ? additions : null, DiscardPaddingNs = discardPadding };
    }

    /// <summary>Reads the BlockMore elements of a BlockAdditions (BlockAddID defaults to 1).</summary>
    private static List<BlockAddition> ReadBlockAdditions(ReadOnlyMemory<byte> data)
    {
        var result = new List<BlockAddition>();
        foreach (var more in EbmlParser.Children(data))
        {
            if (more.Id != BlockMore)
                continue;
            var children = EbmlParser.Children(more.Data);
            if (children.Child(BlockAdditional) is { } payload)
                result.Add(new BlockAddition(children.GetUInt(BlockAddId, 1), payload.Data.ToArray()));
        }

        return result;
    }

    private BlockInfo? ReadBlock(long position, long size, bool simple, bool keyframe, long? duration, bool discardable)
    {
        if (size < 4)
            return null;
        var reader = _demuxer.Reader;
        Span<byte> head = stackalloc byte[12];
        var n = (int)Math.Min(head.Length, size);
        reader.Read(position, head[..n]);
        if (!EbmlVarInt.TryReadSize(head[..n], out var track, out var trackLength) || trackLength + 3 > n)
            return null;

        var wanted = Peek is not null || _attached.ContainsKey(track);
        if (!wanted)
            return new BlockInfo(track, 0, false, false, null, []);

        var relative = BinaryPrimitives.ReadInt16BigEndian(head[trackLength..]);
        var flags = head[trackLength + 2];
        if (simple)
        {
            keyframe = (flags & 0x80) != 0;
            discardable = (flags & 0x01) != 0;
        }

        var lacing = (flags >> 1) & 3;
        var headerLength = trackLength + 3;
        var frames = new List<BlockFrame>();
        if (lacing == 0)
        {
            frames.Add(new BlockFrame(position + headerLength, (int)(size - headerLength), default, false));
        }
        else
        {
            var data = new byte[size];
            reader.Read(position, data);
            foreach (var (offset, length) in Lace(data, headerLength, lacing))
                frames.Add(new BlockFrame(position + offset, length, data.AsMemory(offset, length), true));
        }

        return new BlockInfo(track, _clusterTimestamp + relative, keyframe, discardable, duration, frames);
    }

    /// <summary>Splits a laced block payload into frames (offsets relative to the block data).</summary>
    private static List<(int Offset, int Length)> Lace(byte[] data, int pos, int lacing)
    {
        var count = data[pos++] + 1;
        var sizes = new int[count];
        switch (lacing)
        {
            case 1: // Xiph
                for (var i = 0; i < count - 1; i++)
                {
                    var s = 0;
                    byte b;
                    do
                    {
                        b = data[pos++];
                        s += b;
                    }
                    while (b == 255);
                    sizes[i] = s;
                }

                break;
            case 3: // EBML
            {
                if (!EbmlVarInt.TryReadSize(data.AsSpan(pos), out var first, out var len))
                    throw new InvalidDataException("Invalid EBML lacing.");
                pos += len;
                sizes[0] = (int)first;
                for (var i = 1; i < count - 1; i++)
                {
                    if (!EbmlVarInt.TryReadSize(data.AsSpan(pos), out var raw, out len))
                        throw new InvalidDataException("Invalid EBML lacing.");
                    pos += len;
                    var bias = (1L << (7 * len - 1)) - 1;
                    sizes[i] = (int)(sizes[i - 1] + ((long)raw - bias));
                }

                break;
            }

            case 2: // fixed
            {
                var each = (data.Length - pos) / count;
                for (var i = 0; i < count - 1; i++)
                    sizes[i] = each;
                break;
            }
        }

        var used = 0;
        for (var i = 0; i < count - 1; i++)
            used += sizes[i];
        sizes[^1] = data.Length - pos - used;
        if (sizes.Any(s => s < 0))
            throw new InvalidDataException("Invalid block lacing.");

        var result = new List<(int, int)>(count);
        foreach (var s in sizes)
        {
            result.Add((pos, s));
            pos += s;
        }

        return result;
    }
}

/// <summary>ContentEncodings of a track: header stripping and zlib compression (encryption is rejected).</summary>
internal sealed record ContentEncodingInfo(byte[]? StrippedHeader, bool Zlib)
{
    public static ContentEncodingInfo None { get; } = new(null, false);

    public static ContentEncodingInfo Parse(ReadOnlyMemory<byte> entry)
    {
        var children = EbmlParser.Children(entry);
        if (children.Child(ContentEncodings) is not { } encodings)
            return None;
        byte[]? stripped = null;
        var zlib = false;
        foreach (var enc in EbmlParser.Children(encodings.Data).Where(c => c.Id == ContentEncoding))
        {
            var e = EbmlParser.Children(enc.Data);
            if ((e.GetUInt(ContentEncodingScope, 1) & 1) == 0)
                continue;
            if (e.GetUInt(ContentEncodingType, 0) != 0 || e.Child(ContentEncryption) is not null)
                throw new NotSupportedException("Encrypted Matroska tracks are not supported.");
            var comp = e.Child(ContentCompression) is { } c ? EbmlParser.Children(c.Data) : [];
            switch (comp.GetUInt(ContentCompAlgo, 0))
            {
                case 0:
                    zlib = true;
                    break;
                case 3:
                    stripped = comp.Child(ContentCompSettings)?.Data.ToArray() ?? [];
                    break;
                default:
                    throw new NotSupportedException("Matroska tracks compressed with bzlib or LZO are not supported.");
            }
        }

        return new ContentEncodingInfo(stripped, zlib);
    }

    public bool IsNone => StrippedHeader is null && !Zlib;

    /// <summary>Decodes a stored frame.</summary>
    public byte[] Decode(byte[] stored)
    {
        if (Zlib)
        {
            using var input = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            stored = output.ToArray();
        }

        return StrippedHeader is { Length: > 0 } prefix ? [.. prefix, .. stored] : stored;
    }
}

/// <summary>One track of a Matroska file.</summary>
internal sealed class MatroskaTrackSource : ISampleSource
{
    private readonly MatroskaDemuxer _demuxer;
    private readonly ulong _defaultDurationNs;
    private readonly Queue<MediaSample> _out = new();
    private readonly Queue<MediaSample> _reorder = new();
    private readonly PriorityQueue<long, long> _ptsHeap = new();
    private ClusterScanner? _scanner;
    private bool _ended;
    private long _delivered;
    private long _lastDts = long.MinValue;
    private long _expectedNext = long.MinValue;

    /// <summary>Frame grid (ticks) for video snapping; 0 when none.</summary>
    private long _grid;

    /// <summary>Timestamp precision of the file in ticks.</summary>
    private long _tolerance;

    /// <summary>Nominal audio frame duration in ticks (0 when unknown).</summary>
    private long _frameTicks;

    public MatroskaTrackSource(MatroskaDemuxer demuxer, uint trackId, CodecConfig config, ContentEncodingInfo encoding, ulong defaultDurationNs, TimeSpan duration)
    {
        _demuxer = demuxer;
        TrackId = trackId;
        Config = config;
        Encoding = encoding;
        _defaultDurationNs = defaultDurationNs;
        Duration = duration;
    }

    public uint TrackId { get; }

    public CodecConfig Config { get; private set; }

    public ContentEncodingInfo Encoding { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart { get; private set; }

    public TimeSpan Duration { get; }

    public long SampleCountHint { get; private set; } = -1;

    private bool Reorders => CodecNames.MayReorder(Config.Codec);

    /// <summary>Chooses the timescale and resolves codec details that need the first frame.</summary>
    public void Prepare()
    {
        uint timescale;
        long defaultTicks = 0;
        switch (Config.Kind)
        {
            case TrackKind.Video:
            {
                (timescale, defaultTicks) = VideoTimescale(_defaultDurationNs);
                _grid = defaultTicks;
                if (Config.Codec == CodecType.Vp9 && _demuxer.PeekFirstFrame(this) is { } frame)
                    Config = Config with { Extradata = Vp9.BuildVpcC(frame, Config) };
                else if (Config.Codec == CodecType.Av1 && Config.Extradata is not { Length: >= 4 } && _demuxer.PeekFirstFrame(this) is { } first &&
                         Av1.ConfigurationFromSample(first) is { } av1)
                {
                    // CodecPrivate is mandatory for V_AV1, but some files lack it: the sequence header in the first frame gives it.
                    AppLog.Info($"AV1 track {TrackId} has no CodecPrivate; its configuration was rebuilt from the first frame.");
                    Config = Config with { Extradata = av1.Av1C };
                }
                if (defaultTicks > 0)
                    SampleCountHint = (long)(Duration.TotalSeconds * timescale / defaultTicks);
                break;
            }

            case TrackKind.Audio:
            {
                timescale = (uint)Math.Max(1, Config.SampleRate);
                if (_defaultDurationNs > 0)
                {
                    defaultTicks = (long)Math.Round(_defaultDurationNs * (double)timescale / 1e9);
                }
                else
                {
                    var samples = Config.Codec == CodecType.Aac
                        ? AudioFrames.Samples(Config, default)
                        : _demuxer.PeekFirstFrame(this) is { } frame ? AudioFrames.Samples(Config, frame) : 0;
                    defaultTicks = samples;
                }

                _frameTicks = defaultTicks;
                if (defaultTicks > 0)
                    SampleCountHint = (long)(Duration.TotalSeconds * timescale / defaultTicks);
                break;
            }

            default:
                timescale = 1000;
                break;
        }

        _tolerance = Math.Max(1, (long)Math.Ceiling(_demuxer.TimestampScale * (double)timescale / 1e9));
        MediaStart = (long)Math.Round(Config.CodecDelay.TotalSeconds * timescale);
        Config = Config with { Timescale = timescale, DefaultSampleDuration = defaultTicks };
    }

    /// <summary>A timescale on which the frame duration is an integer (e.g. 24000/1001 for 23.976 fps).</summary>
    private static (uint Timescale, long FrameTicks) VideoTimescale(ulong defaultDurationNs)
    {
        if (defaultDurationNs == 0)
            return (90000, 0);
        var fps = 1e9 / defaultDurationNs;
        foreach (var num in new[] { 24000u, 30000u, 48000u, 60000u, 120000u })
        {
            if (Math.Abs(fps - num / 1001.0) < 0.0005)
                return (num, 1001);
        }

        var rounded = Math.Round(fps);
        if (rounded > 0 && Math.Abs(fps - rounded) < 0.0005)
            return ((uint)(rounded * 1000), 1000);
        return (90000, (long)Math.Round(defaultDurationNs * 90000 / 1e9));
    }

    public void Reset()
    {
        if (_scanner is not null && _delivered == 0)
            return;
        _scanner?.Detach(this);
        _scanner = null;
        _out.Clear();
        _reorder.Clear();
        _ptsHeap.Clear();
        _ended = false;
        _delivered = 0;
        _lastDts = long.MinValue;
        _expectedNext = long.MinValue;
        Attach();
    }

    private void Attach()
    {
        if (_scanner is not null)
            return;
        _scanner = _demuxer.Shared.Started ? new ClusterScanner(_demuxer) : _demuxer.Shared;
        _scanner.Attach(this);
    }

    public MediaSample? ReadNext()
    {
        Attach();
        while (_out.Count == 0)
        {
            if (_ended)
                return null;
            if (!_scanner!.Advance())
            {
                _ended = true;
                FlushReorder(all: true);
            }
        }

        _delivered++;
        return _out.Dequeue();
    }

    /// <summary>Receives a block of this track from the scanner.</summary>
    public void OnBlock(BlockInfo block)
    {
        var scale = (double)_demuxer.TimestampScale;
        var timescale = (double)Config.Timescale;
        var baseNs = block.Timestamp * scale;
        double frameNs = _defaultDurationNs;
        var count = block.Frames.Count;
        if (frameNs == 0 && count > 1 && block.Duration is { } bd)
            frameNs = bd * scale / count;

        double offsetNs = 0;
        for (var i = 0; i < count; i++)
        {
            var frame = block.Frames[i];
            var sample = new MediaSample
            {
                IsSync = block.Keyframe || Config.Kind != TrackKind.Video,
                IsDiscardable = block.Discardable,
                Additions = count == 1 ? block.Additions : null, // additions belong to an unlaced block's frame
            };
            if (frame.InMemory || Encoding.Zlib)
            {
                var stored = frame.InMemory ? frame.Data.ToArray() : _demuxer.Reader.ReadAvailable(frame.Position, frame.Size);
                sample.Data = Encoding.Decode(stored);
            }
            else
            {
                sample.Reader = _demuxer.Reader;
                sample.Position = frame.Position;
                sample.StoredSize = frame.Size;
                if (Encoding.StrippedHeader is { Length: > 0 } prefix)
                    sample.Prefix = prefix;
            }

            if (Config.Codec == CodecType.WebVtt && Config.SourceCodecId.StartsWith("D_WEBVTT", StringComparison.Ordinal))
                sample = WebVttCueText(sample);
            else if (Config.Codec == CodecType.WebVtt)
                WebVttSettingsFromAddition(sample);
            else if (Config.Codec == CodecType.ProRes)
                RestoreProResHeader(sample);

            var ticks = Ticks(baseNs + offsetNs, timescale);
            long durationTicks = 0;
            if (count == 1 && block.Duration is { } blockDuration)
                durationTicks = Ticks(blockDuration * scale, timescale);
            else if (frameNs > 0)
                durationTicks = Ticks(frameNs, timescale);

            // Laced audio: the frames' own durations place the following frames.
            if (count > 1 && Config.Kind == TrackKind.Audio && !sample.Data.IsEmpty)
            {
                var samples = AudioFrames.Samples(Config, sample.Data.Span);
                if (samples > 0)
                {
                    durationTicks = samples;
                    offsetNs += samples * 1e9 / timescale;
                }
                else
                {
                    offsetNs += frameNs;
                }
            }
            else
            {
                offsetNs += frameNs;
            }

            if (durationTicks == 0)
                durationTicks = Config.DefaultSampleDuration;
            sample.Dts = Snap(ticks, durationTicks);
            sample.Duration = durationTicks;
            if (i == count - 1 && block.DiscardPaddingNs > 0)
                sample.TrimEnd = Ticks(block.DiscardPaddingNs, timescale);
            Enqueue(sample);
        }
    }

    /// <summary>
    /// Matroska stores ProRes frames without their 8-byte header (frame size and 'icpf'); samples carry complete
    /// frames as in MP4.
    /// </summary>
    private static void RestoreProResHeader(MediaSample sample)
    {
        Span<byte> head = stackalloc byte[8];
        if (sample.Size >= 8)
        {
            if (sample.Reader is null)
                sample.Data.Span[..8].CopyTo(head);
            else
                sample.CopyHead(head);
            if (head[4..].SequenceEqual("icpf"u8))
                return;
        }

        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(sample.Size + 8));
        "icpf"u8.CopyTo(header.AsSpan(4));
        if (sample.Reader is null)
            sample.Data = (byte[])[.. header, .. sample.Data.Span];
        else
            sample.Prefix = (byte[])[.. header, .. sample.Prefix.Span];
    }

    /// <summary>
    /// D_WEBVTT blocks (FFmpeg) are "identifier\nsettings\ntext"; samples carry the cue text, with the settings in
    /// <see cref="MediaSample.CueSettings"/>.
    /// </summary>
    private static MediaSample WebVttCueText(MediaSample sample)
    {
        var data = sample.GetData().ToArray();
        var first = Array.IndexOf(data, (byte)'\n');
        var second = first < 0 ? -1 : Array.IndexOf(data, (byte)'\n', first + 1);
        sample.Reader = null;
        sample.Prefix = default;
        sample.Data = second < 0 ? data : data[(second + 1)..];
        if (second > first + 1)
            sample.CueSettings = NonEmpty(System.Text.Encoding.UTF8.GetString(data, first + 1, second - first - 1));
        return sample;
    }

    /// <summary>
    /// S_TEXT/WEBVTT blocks (mkvmerge) hold the cue text; a BlockAddition (BlockAddID 1) holds "settings\nidentifier"
    /// followed by the NOTE blocks before the cue. The addition stays on the sample (Matroska passthrough keeps it).
    /// </summary>
    private static void WebVttSettingsFromAddition(MediaSample sample)
    {
        foreach (var addition in sample.Additions ?? [])
        {
            if (addition.Id != 1)
                continue;
            var text = System.Text.Encoding.UTF8.GetString(addition.Data.Span);
            var end = text.IndexOf('\n', StringComparison.Ordinal);
            sample.CueSettings = NonEmpty(end < 0 ? text : text[..end]);
            return;
        }
    }

    private static string? NonEmpty(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static long Ticks(double ns, double timescale) => (long)Math.Round(ns * timescale / 1e9);

    /// <summary>Snaps a timestamp to the frame grid (video) or to the end of the previous frame (audio).</summary>
    private long Snap(long ticks, long durationTicks)
    {
        switch (Config.Kind)
        {
            case TrackKind.Video when _grid > 0:
            {
                var snapped = (long)Math.Round((double)ticks / _grid) * _grid;
                return Math.Abs(snapped - ticks) <= _tolerance ? snapped : ticks;
            }

            case TrackKind.Audio:
            {
                var expected = _expectedNext;
                var result = expected != long.MinValue && Math.Abs(ticks - expected) <= _tolerance ? expected : ticks;
                _expectedNext = durationTicks > 0 ? result + durationTicks : long.MinValue;
                return result;
            }

            default:
                return ticks;
        }
    }

    private void Enqueue(MediaSample sample)
    {
        if (!Reorders)
        {
            Emit(sample);
            return;
        }

        // Presentation time is in Dts for now; derive the decoding time from the sorted presentation times.
        _reorder.Enqueue(sample);
        _ptsHeap.Enqueue(sample.Dts, sample.Dts);
        FlushReorder(all: false);
    }

    private void FlushReorder(bool all)
    {
        while (_reorder.Count > (all ? 0 : MatroskaDemuxer.ReorderWindow))
        {
            var sample = _reorder.Dequeue();
            var pts = sample.Dts;
            var dts = _ptsHeap.Dequeue();
            sample.Dts = dts;
            sample.CtsOffset = pts - dts;
            Emit(sample);
        }
    }

    private void Emit(MediaSample sample)
    {
        if (sample.Dts <= _lastDts)
        {
            // Keep decoding times strictly increasing. Reordered video keeps its presentation time; for other tracks
            // (overlapping timestamps in the source) presentation follows decoding.
            var pts = sample.Pts;
            sample.Dts = _lastDts + 1;
            sample.CtsOffset = Reorders ? pts - sample.Dts : 0;
        }

        _lastDts = sample.Dts;
        _out.Enqueue(sample);
    }
}

/// <summary>VP9 uncompressed header parsing for the vpcC record.</summary>
internal static class Vp9
{
    /// <summary>Builds a VPCodecConfigurationRecord (vpcC payload) from the first frame.</summary>
    public static byte[]? BuildVpcC(byte[] frame, CodecConfig config)
    {
        if (frame.Length < 1 || (frame[0] >> 6) != 2)
            return null;
        var r = new BitReader(frame);
        try
        {
            r.Skip(2);
            var profile = (int)(r.Read(1) | (r.Read(1) << 1));
            if (profile == 3)
                r.Skip(1);
            var depth = 8;
            var subsampling = 1; // 4:2:0 colocated with luma
            if (!r.Flag() && !r.Flag()) // show_existing_frame == 0, frame_type == KEY_FRAME
            {
                r.Skip(2); // show_frame, error_resilient_mode
                r.Skip(24); // sync code
                if (profile >= 2)
                    depth = r.Flag() ? 12 : 10;
                var colorSpace = (int)r.Read(3);
                if (colorSpace != 7)
                {
                    r.Skip(1);
                    if (profile is 1 or 3)
                    {
                        var x = (int)r.Read(1);
                        var y = (int)r.Read(1);
                        subsampling = x == 1 && y == 1 ? 1 : x == 1 ? 2 : 3;
                    }
                }
                else
                {
                    subsampling = 3;
                }
            }

            if (config.BitsPerSample > 0)
                depth = config.BitsPerSample;
            var pixels = (long)config.Width * config.Height;
            var level = pixels <= 921600 ? 31 : pixels <= 2228224 ? 41 : pixels <= 8912896 ? 51 : 61;
            var color = config.Color;
            return
            [
                1, 0, 0, 0, // version 1, flags
                (byte)profile,
                (byte)level,
                (byte)((depth << 4) | (subsampling << 1) | (color.FullRange == true ? 1 : 0)),
                (byte)(color.IsSpecified ? color.Primaries : 2),
                (byte)(color.IsSpecified ? color.Transfer : 2),
                (byte)(color.IsSpecified ? color.Matrix : 2),
                0, 0, // codecInitializationDataSize
            ];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
