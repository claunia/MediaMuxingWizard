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
