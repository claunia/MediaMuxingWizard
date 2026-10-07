using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>Reads syncframes from a byte stream with a growable look-ahead buffer.</summary>
internal sealed class FrameReader : IDisposable
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[256 * 1024];
    private int _start;
    private int _end;
    private bool _eof;

    public FrameReader(Stream stream) => _stream = stream;

    public long Position => _stream.Position - (_end - _start);

    /// <summary>Makes at least <paramref name="count"/> bytes available; false at the end of the stream.</summary>
    public bool Ensure(int count)
    {
        while (_end - _start < count)
        {
            if (_eof)
                return false;
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_buffer.Length < count)
                Array.Resize(ref _buffer, Math.Max(count, _buffer.Length * 2));
            var n = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (n == 0)
                _eof = true;
            _end += n;
        }

        return true;
    }

    public ReadOnlySpan<byte> Available => _buffer.AsSpan(_start, _end - _start);

    public void Skip(int count) => _start += Math.Min(count, _end - _start);

    public byte[] Take(int count)
    {
        var data = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        return data;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>ADTS AAC streams (.aac): headers are removed and an AudioSpecificConfig is built from the first one.</summary>
internal sealed class AdtsParser : IElementaryParser
{
    private readonly FrameReader _reader;
    private long _dts;

    public AdtsParser(Stream stream) => _reader = new FrameReader(stream);

    public static CodecConfig Probe(Stream stream, out long sampleCountHint)
    {
        using var reader = new FrameReader(stream);
        if (!Resync(reader) || Aac.ParseAdts(reader.Available) is not { } h)
            throw new InvalidDataException("No ADTS frame found.");
        if (h.RawBlocks > 1)
            throw new NotSupportedException("ADTS frames carrying several raw data blocks are not supported.");
        sampleCountHint = stream.Length / Math.Max(1, h.FrameLength);
        var channels = h.ChannelConfig == 7 ? 8 : h.ChannelConfig;
        return new CodecConfig
        {
            Codec = CodecType.Aac,
            Kind = TrackKind.Audio,
            SourceCodecId = "adts",
            Extradata = Aac.BuildConfig(h.ObjectType, h.SampleRate, channels),
            Timescale = (uint)h.SampleRate,
            DefaultSampleDuration = 1024,
            SampleRate = h.SampleRate,
            Channels = channels,
        };
    }

    private static bool Resync(FrameReader reader)
    {
        while (reader.Ensure(9))
        {
            var span = reader.Available;
            if (Aac.ParseAdts(span) is { } h)
            {
                // Confirm with the next header, or accept a last frame that ends the stream.
                if (reader.Ensure(h.FrameLength + 2))
                {
                    var next = reader.Available[h.FrameLength..];
                    if (next[0] == 0xFF && (next[1] & 0xF6) == 0xF0)
                        return true;
                }
                else if (reader.Ensure(h.FrameLength))
                {
                    return true;
                }
            }

            reader.Skip(1);
        }

        return false;
    }

    public MediaSample? Next()
    {
        while (_reader.Ensure(7))
        {
            if (Aac.ParseAdts(_reader.Available) is not { } h)
            {
                if (!Resync(_reader))
                    return null;
                continue;
            }

            if (h.RawBlocks > 1)
                throw new NotSupportedException("ADTS frames carrying several raw data blocks are not supported.");
            if (!_reader.Ensure(h.FrameLength))
                return null;
            _reader.Skip(h.HeaderLength);
            var data = _reader.Take(h.FrameLength - h.HeaderLength);
            var sample = new MediaSample { Dts = _dts, Duration = 1024, IsSync = true, Data = data };
            _dts += 1024;
            return sample;
        }

        return null;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>AC-3 and E-AC-3 streams (.ac3, .eac3, .ec3); E-AC-3 access units group the substreams of one frame.</summary>
internal sealed class Ac3Parser : IElementaryParser
{
    private readonly FrameReader _reader;
    private long _dts;

    public Ac3Parser(Stream stream) => _reader = new FrameReader(stream);

    public static CodecConfig Probe(Stream stream, out long sampleCountHint)
    {
        using var parser = new Ac3Parser(stream);
        var first = parser.Next() ?? throw new InvalidDataException("No AC-3 syncframe found.");
        var frames = Ac3.ParseAccessUnit(first.Data.Span);
        var h = frames[0];
        sampleCountHint = stream.Length / Math.Max(1, first.Data.Length);
        var eac3 = h.IsEac3;
        var config = eac3 ? Ac3.BuildDec3(frames) : Ac3.BuildDac3(h);
        var (channels, _, atmos) = Ac3.Describe(config, eac3);
        return new CodecConfig
        {
            Codec = eac3 ? CodecType.Eac3 : CodecType.Ac3,
            Kind = TrackKind.Audio,
            SourceCodecId = eac3 ? "ec-3" : "ac-3",
            Extradata = config,
            Timescale = (uint)h.SampleRate,
            DefaultSampleDuration = h.Samples,
            SampleRate = h.SampleRate,
            Channels = channels > 0 ? channels : h.Channels,
            IsAtmos = atmos,
        };
    }

    public MediaSample? Next()
    {
        if (!Sync())
            return null;
        var first = Ac3.Parse(_reader.Available)!;
        var length = first.FrameSize;

        // E-AC-3: dependent substreams and further independent substreams up to the next substream 0 frame.
        if (first.IsEac3)
        {
            while (_reader.Ensure(length + 8))
            {
                var next = Ac3.Parse(_reader.Available[length..]);
                if (next is null || (next.StreamType != 1 && next.SubstreamId == 0))
                    break;
                if (!_reader.Ensure(length + next.FrameSize))
                    break;
                length += next.FrameSize;
            }
        }

        if (!_reader.Ensure(length))
            return null;
        var sample = new MediaSample { Dts = _dts, Duration = first.Samples, IsSync = true, Data = _reader.Take(length) };
        _dts += first.Samples;
        return sample;
    }

    private bool Sync()
    {
        while (_reader.Ensure(8))
        {
            if (Ac3.Parse(_reader.Available) is { } h && _reader.Ensure(h.FrameSize))
                return true;
            _reader.Skip(1);
        }

        return false;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// Raw AC-4 (.ac4): sync frames (0xAC40, or 0xAC41 with a CRC) whose raw frames are the samples; the 'ac-4' sample
/// entry and its 'dac4' are built from the first frame's table of contents. Times are in the frame rate's media
/// timescale (48000, or 240000 for the NTSC rates).
/// </summary>
internal sealed class Ac4Parser : IElementaryParser
{
    private readonly FrameReader _reader;
    private readonly int _duration;
    private long _dts;

    public Ac4Parser(Stream stream, int duration)
    {
        _reader = new FrameReader(stream);
        _duration = duration;
    }

    public static CodecConfig Probe(Stream stream, out long sampleCountHint)
    {
        using var parser = new Ac4Parser(stream, 0);
        var first = parser.Next() ?? throw new InvalidDataException("No AC-4 sync frame found.");
        var raw = first.Data.ToArray();
        var info = Ac4.Parse(raw) ?? throw new InvalidDataException("The AC-4 table of contents cannot be read.");
        var entry = Ac4.BuildEntry(raw) ?? throw new InvalidDataException("The AC-4 decoder configuration cannot be built.");
        sampleCountHint = stream.Length / Math.Max(1, raw.Length + 4);
        return new CodecConfig
        {
            Codec = CodecType.Ac4,
            Kind = TrackKind.Audio,
            SourceCodecId = "ac-4",
            Extradata = entry,
            Timescale = (uint)(info.MediaTimescale > 0 ? info.MediaTimescale : info.SampleRate),
            DefaultSampleDuration = info.SampleDuration > 0 ? info.SampleDuration : 2048,
            SampleRate = info.SampleRate,
            Channels = info.ChannelCount,
            AudioProfile = Ac4.Describe(info),
        };
    }

    public MediaSample? Next()
    {
        while (_reader.Ensure(7))
        {
            var length = Ac4.SyncFrameLength(_reader.Available);
            if (length > 0 && _reader.Ensure(length))
            {
                var raw = Ac4.RawFrame(_reader.Available[..length]);
                _reader.Skip(length);
                if (raw is null)
                    continue;
                var sample = new MediaSample { Dts = _dts, Duration = _duration, IsSync = true, Data = raw };
                _dts += _duration;
                return sample;
            }

            _reader.Skip(1);
        }

        return null;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>
/// Raw DTS (.dts: core frames, each optionally followed by a DTS-HD extension substream) and DTS-HD Master Audio
/// stream files (.dtshd: the same frames in the STRMDATA chunk of a DTSHDHDR file).
/// </summary>
internal sealed class DtsParser : IElementaryParser
{
    private readonly FrameReader _reader;
    private long _dts;

    public DtsParser(Stream stream)
    {
        stream.Position = DataOffset(stream);
        _reader = new FrameReader(stream);
    }

    /// <summary>Where the frames start: after the STRMDATA chunk header of a .dtshd file, else at 0.</summary>
    public static long DataOffset(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, 16, throwOnEndOfStream: false) < 16 || !header[..8].SequenceEqual("DTSHDHDR"u8))
            return 0;
        // Chunks: 8-byte identifier, 64-bit big-endian size, data.
        long position = 0;
        while (position + 16 <= stream.Length)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(header, 16, throwOnEndOfStream: false) < 16)
                break;
            var size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            if (header[..8].SequenceEqual("STRMDATA"u8))
                return position + 16;
            position += 16 + size;
        }

        throw new InvalidDataException("The DTS-HD file has no STRMDATA chunk.");
    }

    public static CodecConfig Probe(Stream stream, out long sampleCountHint)
    {
        using var parser = new DtsParser(stream);
        var units = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < DtsDetector.MaxSamples && parser.Next() is { } sample; i++)
            units.Add(sample.Data);
        if (units.Count == 0 || Dts.Parse(units[0].Span) is not { } first)
            throw new InvalidDataException("No DTS frame found.");
        sampleCountHint = stream.Length / Math.Max(1, units[0].Length);
        var config = new CodecConfig
        {
            Codec = CodecType.Dts,
            Kind = TrackKind.Audio,
            SourceCodecId = "dts",
            Timescale = (uint)first.SampleRate, // timing follows the core
            DefaultSampleDuration = first.Samples,
            SampleRate = first.SampleRate,
            Channels = first.Channels,
            BitsPerSample = first.BitsPerSample,
        };
        return Dts.Describe(units) is { } best ? DtsDetector.Apply(config, best) : config;
    }

    public MediaSample? Next()
    {
        while (_reader.Ensure(16))
        {
            var length = Dts.AccessUnitLength(_reader.Available, atEnd: false);
            if (length < 0 && !_reader.Ensure(_reader.Available.Length + 4096))
                length = Dts.AccessUnitLength(_reader.Available, atEnd: true);
            if (length < 0)
                continue;
            if (length == 0)
            {
                _reader.Skip(1);
                continue;
            }

            var samples = Dts.Parse(_reader.Available[..length])?.Samples ?? 512;
            var sample = new MediaSample { Dts = _dts, Duration = samples, IsSync = true, Data = _reader.Take(length) };
            _dts += samples;
            return sample;
        }

        return null;
    }

    public void Dispose() => _reader.Dispose();
}
