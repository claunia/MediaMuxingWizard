using System.Buffers.Binary;
using System.Globalization;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Ogg.Resources;

namespace MMW.Formats.Ogg;

/// <summary>Ogg files (.ogg, .oga, .opus, .spx …) as an <see cref="IDemuxer"/>: Opus, Vorbis and FLAC streams.</summary>
public static class OggFormat
{
    private static int s_registered;

    public static IReadOnlyList<string> Extensions { get; } = [".ogg", ".oga", ".opus", ".ogx"];

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
            MediaFormatRegistry.Register(new OggDemuxerFactory());
    }
}

/// <summary>Opens Ogg files.</summary>
public sealed class OggDemuxerFactory : IDemuxerFactory
{
    public string Name => "Ogg";

    public int Probe(string path, ReadOnlySpan<byte> header) => header.StartsWith("OggS"u8) ? 95 : 0;

    public IDemuxer Open(string path, DemuxOptions? options = null) => OggDemuxer.Open(path);
}

/// <summary>
/// Reads an Ogg file (RFC 3533): pages are indexed once and each logical stream's packets rebuilt across pages. Packets
/// in one page are read lazily; packets that span pages are kept in memory. Times come from the packets' own durations
/// anchored on the granule positions: Opus (RFC 7845; the pre-skip is the media start, the end granule trims the last
/// packet), Vorbis (block sizes from the setup header's modes) and FLAC (frame headers).
/// </summary>
internal sealed class OggDemuxer : IDemuxer
{
    private readonly FileSampleReader _reader;

    private OggDemuxer(string path, FileSampleReader reader)
    {
        Path = path;
        _reader = reader;
    }

    public string Path { get; }

    public string FormatName => "Ogg";

    public ContainerKind Container => ContainerKind.Unknown;

    public IReadOnlyList<ISampleSource> Tracks { get; private set; } = [];

    public TimeSpan Duration { get; private set; }

    /// <summary>A packet of a logical stream.</summary>
    internal sealed class Packet
    {
        /// <summary>File position of a packet stored in one page; -1 when <see cref="Data"/> holds it.</summary>
        public long Position { get; set; } = -1;

        public int Size { get; set; }

        public byte[]? Data { get; set; }

        /// <summary>Granule position of the page the packet ends on, when it is the last packet ending there; else -1.</summary>
        public long Granule { get; set; } = -1;
    }

    /// <summary>A logical stream while indexing.</summary>
    private sealed class Stream(uint serial)
    {
        public uint Serial { get; } = serial;

        public List<Packet> Packets { get; } = [];

        /// <summary>Fragments of the packet being assembled (position, size).</summary>
        public List<(long Position, int Size)> Pending { get; } = [];
    }

    public static OggDemuxer Open(string path)
    {
        var reader = new FileSampleReader(path);
        var demuxer = new OggDemuxer(path, reader);
        try
        {
            demuxer.Index();
            return demuxer;
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    private void Index()
    {
        var streams = new Dictionary<uint, Stream>();
        var order = new List<Stream>();
        long pos = 0;
        Span<byte> header = stackalloc byte[27 + 255];
        while (pos + 27 <= _reader.Length)
        {
            _reader.Read(pos, header[..27]);
            if (!header.StartsWith("OggS"u8))
            {
                pos = Resync(pos + 1);
                if (pos < 0)
                    break;
                continue;
            }

            int segments = header[26];
            if (pos + 27 + segments > _reader.Length)
                break;
            _reader.Read(pos + 27, header.Slice(27, segments));
            var granule = BinaryPrimitives.ReadInt64LittleEndian(header[6..]);
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            var continued = (header[5] & 1) != 0;
            if (!streams.TryGetValue(serial, out var stream))
            {
                stream = new Stream(serial);
                streams[serial] = stream;
                order.Add(stream);
            }

            if (!continued)
                stream.Pending.Clear(); // a packet left unfinished by a lost page

            var body = pos + 27 + segments;
            long offset = body;
            var packetStart = offset;
            var packetSize = 0;
            Packet? lastCompleted = null;
            for (var i = 0; i < segments; i++)
            {
                int lace = header[27 + i];
                packetSize += lace;
                offset += lace;
                if (lace == 255)
                    continue;
                stream.Pending.Add((packetStart, packetSize));
                lastCompleted = Complete(stream.Pending);
                stream.Packets.Add(lastCompleted);
                stream.Pending.Clear();
                packetStart = offset;
                packetSize = 0;
            }

            if (packetSize > 0)
                stream.Pending.Add((packetStart, packetSize)); // continues on the next page
            if (lastCompleted is not null)
                lastCompleted.Granule = granule;
            pos = offset;
        }

        var tracks = new List<ISampleSource>();
        foreach (var stream in order)
        {
            if (OggTrack.Create(this, stream.Serial, stream.Packets) is { } track)
                tracks.Add(track);
        }

        if (tracks.Count == 0)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoSupportedStream, System.IO.Path.GetFileName(Path)));
        Tracks = tracks;
        Duration = tracks.Select(t => t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max();
    }

    /// <summary>A packet from its fragments: one is read lazily, several are joined now.</summary>
    private Packet Complete(List<(long Position, int Size)> fragments)
    {
        if (fragments.Count == 1)
            return new Packet { Position = fragments[0].Position, Size = fragments[0].Size };
        var data = new byte[fragments.Sum(f => f.Size)];
        var at = 0;
        foreach (var (position, size) in fragments)
        {
            _reader.Read(position, data.AsSpan(at, size));
            at += size;
        }

        return new Packet { Data = data, Size = data.Length };
    }

    /// <summary>The next "OggS" at or after <paramref name="from"/>, or -1.</summary>
    private long Resync(long from)
    {
        var buffer = new byte[64 * 1024];
        while (from < _reader.Length)
        {
            var n = (int)Math.Min(buffer.Length, _reader.Length - from);
            _reader.Read(from, buffer.AsSpan(0, n));
            var at = buffer.AsSpan(0, n).IndexOf("OggS"u8);
            if (at >= 0)
                return from + at;
            from += Math.Max(1, n - 3);
        }

        return -1;
    }

    internal byte[] Read(Packet packet)
    {
        if (packet.Data is { } data)
            return data;
        var bytes = new byte[packet.Size];
        _reader.Read(packet.Position, bytes);
        return bytes;
    }

    internal ISampleDataReader Reader => _reader;

    public void Dispose() => _reader.Dispose();
}

/// <summary>One Opus, Vorbis or FLAC stream of an Ogg file.</summary>
internal sealed class OggTrack : ISampleSource
{
    private readonly OggDemuxer _demuxer;
    private readonly List<(OggDemuxer.Packet Packet, long Time, long Duration)> _samples;
    private readonly long _trimEnd;
    private int _next;

    private OggTrack(OggDemuxer demuxer, uint serial, CodecConfig config, List<(OggDemuxer.Packet, long, long)> samples, long mediaStart, long trimEnd)
    {
        _demuxer = demuxer;
        TrackId = serial;
        Config = config;
        _samples = samples;
        MediaStart = mediaStart;
        _trimEnd = trimEnd;
        var end = samples.Count == 0 ? 0 : samples[^1].Item2 + samples[^1].Item3 - trimEnd;
        Duration = TimeSpan.FromSeconds(Math.Max(0, end - mediaStart) / (double)Math.Max(1u, config.Timescale));
    }

    public uint TrackId { get; }

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart { get; }

    public TimeSpan Duration { get; }

    public long SampleCountHint => _samples.Count;

    public MediaSample? ReadNext()
    {
        if (_next >= _samples.Count)
            return null;
        var (packet, time, duration) = _samples[_next++];
        var sample = new MediaSample { Dts = time, Duration = duration, IsSync = true };
        if (packet.Data is { } data)
        {
            sample.Data = data;
        }
        else
        {
            sample.Reader = _demuxer.Reader;
            sample.Position = packet.Position;
            sample.StoredSize = packet.Size;
        }

        if (_next == _samples.Count)
            sample.TrimEnd = _trimEnd;
        return sample;
    }

    public void Reset() => _next = 0;

    /// <summary>The track for a stream's packets; null for streams that are not Opus, Vorbis or FLAC.</summary>
    public static OggTrack? Create(OggDemuxer demuxer, uint serial, List<OggDemuxer.Packet> packets)
    {
        if (packets.Count == 0)
            return null;
        var first = demuxer.Read(packets[0]);
        try
        {
            if (first.AsSpan().StartsWith("OpusHead"u8))
                return Opus(demuxer, serial, packets, first);
            if (first.Length >= 30 && first[0] == 1 && first.AsSpan(1, 6).SequenceEqual("vorbis"u8))
                return Vorbis(demuxer, serial, packets, first);
            if (first.Length >= 51 && first[0] == 0x7F && first.AsSpan(1, 4).SequenceEqual("FLAC"u8))
                return Flac(demuxer, serial, packets, first);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Log_StreamError, serial, ex.Message));
            return null;
        }

        var kind = first.AsSpan().StartsWith("\x80theora"u8) ? "Theora" : first.AsSpan().StartsWith("fishead"u8) ? "Skeleton" : Strings.Kind_Unknown;
        AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_UnsupportedStream, serial, kind));
        return null;
    }

    private static OggTrack Opus(OggDemuxer demuxer, uint serial, List<OggDemuxer.Packet> packets, byte[] head)
    {
        var (channels, preSkip, inputRate) = Core.Media.Codecs.Opus.Describe(head);
        var audio = packets.Skip(2).ToList(); // OpusHead, OpusTags
        var durations = audio.Select(p => (long)Core.Media.Codecs.Opus.PacketSamples(demuxer.Read(p))).ToList();
        var config = new CodecConfig
        {
            Codec = CodecType.Opus,
            Kind = TrackKind.Audio,
            SourceCodecId = "opus",
            Extradata = head,
            SampleRate = 48000,
            Channels = channels,
            Timescale = 48000,
            DefaultSampleDuration = 960,
            CodecDelay = TimeSpan.FromSeconds(preSkip / 48000.0),
            Language = "und",
        };
        _ = inputRate;
        var (samples, trim) = Timeline(audio, durations);
        return new OggTrack(demuxer, serial, config, samples, preSkip + (samples.Count > 0 ? Math.Min(0, samples[0].Item2) : 0), trim);
    }

    private static OggTrack Vorbis(OggDemuxer demuxer, uint serial, List<OggDemuxer.Packet> packets, byte[] identification)
    {
        if (packets.Count < 3)
            throw new InvalidDataException(Strings.Error_VorbisMissingHeaders);
        var comment = demuxer.Read(packets[1]);
        var setup = demuxer.Read(packets[2]);
        var channels = identification[11];
        var rate = BinaryPrimitives.ReadInt32LittleEndian(identification.AsSpan(12));
        int[] blocksize = [1 << (identification[28] & 0xF), 1 << (identification[28] >> 4)];
        var modes = VorbisModes(setup);
        var audio = packets.Skip(3).ToList();

        // A packet's duration is a quarter of its and the previous packet's block sizes; the first decodes to nothing.
        var durations = new List<long>(audio.Count);
        var previous = -1;
        foreach (var p in audio)
        {
            var data = demuxer.Read(p);
            if (data.Length == 0 || (data[0] & 1) != 0)
            {
                durations.Add(0);
                continue;
            }

            var mode = modes.Length == 1 ? 0 : (data[0] >> 1) & ((1 << ModeBits(modes.Length)) - 1);
            var current = blocksize[mode < modes.Length && modes[mode] ? 1 : 0];
            durations.Add(previous < 0 ? 0 : (previous + current) / 4);
            previous = current;
        }

        var config = new CodecConfig
        {
            Codec = CodecType.Vorbis,
            Kind = TrackKind.Audio,
            SourceCodecId = "vorbis",
            Extradata = XiphLace(identification, comment, setup),
            SampleRate = rate,
            Channels = channels,
            Timescale = (uint)rate,
            DefaultSampleDuration = blocksize[1] / 2,
            Language = "und",
        };
        var (samples, trim) = Timeline(audio, durations);
        return new OggTrack(demuxer, serial, config, samples, samples.Count > 0 ? Math.Max(0, -samples[0].Item2) : 0, trim);
    }

    private static OggTrack Flac(OggDemuxer demuxer, uint serial, List<OggDemuxer.Packet> packets, byte[] mapping)
    {
        // 0x7F "FLAC" major minor, the number of header packets that follow, "fLaC", then STREAMINFO.
        if (!mapping.AsSpan(9, 4).SequenceEqual("fLaC"u8))
            throw new InvalidDataException(Strings.Error_InvalidOggFlacMapping);
        int headers = BinaryPrimitives.ReadUInt16BigEndian(mapping.AsSpan(7));
        var blocks = new List<byte>(mapping.AsSpan(13).ToArray());
        var index = 1;
        while (index < packets.Count && (headers == 0 ? index == 1 || (blocks.Count > 0 && LastBlockMissing(blocks)) : index <= headers))
        {
            var block = demuxer.Read(packets[index]);
            if (block.Length > 0 && (block[0] & 0x7F) == 0x7F)
                break; // a frame (sync 0xFFF8): no more metadata
            blocks.AddRange(block);
            index++;
        }

        var metadata = Core.Media.Codecs.Flac.FixLastFlags(blocks.ToArray());
        var (rate, channels, bits) = Core.Media.Codecs.Flac.Describe(metadata);
        var audio = packets.Skip(index).ToList();
        var durations = audio.Select(p => (long)Core.Media.Codecs.Flac.FrameSamples(demuxer.Read(p))).ToList();
        var config = new CodecConfig
        {
            Codec = CodecType.Flac,
            Kind = TrackKind.Audio,
            SourceCodecId = "flac",
            Extradata = metadata,
            SampleRate = rate,
            Channels = channels,
            BitsPerSample = bits,
            Timescale = (uint)Math.Max(1, rate),
            DefaultSampleDuration = durations.Count > 0 ? durations[0] : 4096,
            Language = "und",
        };
        var (samples, _) = Timeline(audio, durations);
        return new OggTrack(demuxer, serial, config, samples, samples.Count > 0 ? Math.Max(0, -samples[0].Item2) : 0, 0);
    }

    private static bool LastBlockMissing(List<byte> blocks)
    {
        var pos = 0;
        while (pos + 4 <= blocks.Count)
        {
            var last = (blocks[pos] & 0x80) != 0;
            var length = (blocks[pos + 1] << 16) | (blocks[pos + 2] << 8) | blocks[pos + 3];
            if (last)
                return false;
            pos += 4 + length;
        }

        return true;
    }

    /// <summary>
    /// Packet times from their durations, anchored on the first granule position (the stream may start before zero, the
    /// pre-roll, or after it), and the end trim the last granule position asks for.
    /// </summary>
    private static (List<(OggDemuxer.Packet, long, long)> Samples, long TrimEnd) Timeline(List<OggDemuxer.Packet> packets, List<long> durations)
    {
        long start = 0;
        long sum = 0;
        for (var i = 0; i < packets.Count; i++)
        {
            sum += durations[i];
            if (packets[i].Granule >= 0)
            {
                start = packets[i].Granule - sum;
                break;
            }
        }

        var samples = new List<(OggDemuxer.Packet, long, long)>(packets.Count);
        var time = start;
        for (var i = 0; i < packets.Count; i++)
        {
            samples.Add((packets[i], time, durations[i]));
            time += durations[i];
        }

        long trim = 0;
        if (packets.Count > 0 && packets[^1].Granule >= 0 && packets[^1].Granule < time)
            trim = Math.Min(durations[^1], time - packets[^1].Granule);
        return (samples, trim);
    }

    /// <summary>
    /// The block flag of each Vorbis mode, read backwards from the end of the setup header (as FFmpeg and liboggz do:
    /// the modes are the last field, each 8 + 16 + 16 bits after its flag, preceded by the 6-bit mode count).
    /// </summary>
    private static bool[] VorbisModes(byte[] setup)
    {
        if (setup.Length < 7 || setup[0] != 5)
            throw new InvalidDataException(Strings.Error_InvalidVorbisSetup);
        var reversed = setup.Reverse().ToArray();
        var r = new BitReader(reversed);
        long framing = -1;
        while (r.BitsLeft > 97)
        {
            if (r.Flag())
            {
                framing = r.Position;
                break;
            }
        }

        if (framing < 0)
            throw new InvalidDataException(Strings.Error_InvalidVorbisSetup);
        var count = 0;
        var found = 0;
        while (r.BitsLeft >= 97)
        {
            if (r.Read(8) > 63 || r.Read(16) != 0 || r.Read(16) != 0)
                break;
            r.Skip(1);
            count++;
            if (count > 64)
                break;
            var peek = r;
            if (peek.Read(6) + 1 == count)
                found = count;
        }

        if (found == 0)
            throw new InvalidDataException(Strings.Error_VorbisModesNotFound);
        var flags = new bool[found];
        r = new BitReader(reversed);
        r.Skip(framing);
        for (var i = found - 1; i >= 0; i--)
        {
            r.Skip(40);
            flags[i] = r.Flag();
        }

        return flags;
    }

    private static int ModeBits(int modes) => modes <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(modes - 1));

    /// <summary>The three Vorbis headers Xiph-laced (Matroska CodecPrivate form).</summary>
    private static byte[] XiphLace(byte[] identification, byte[] comment, byte[] setup)
    {
        var o = new List<byte> { 2 };
        foreach (var header in new[] { identification, comment })
        {
            var n = header.Length;
            while (n >= 255)
            {
                o.Add(255);
                n -= 255;
            }

            o.Add((byte)n);
        }

        o.AddRange(identification);
        o.AddRange(comment);
        o.AddRange(setup);
        return [.. o];
    }
}
