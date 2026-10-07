using System.Buffers.Binary;

namespace MMW.Formats.MpegTs;

/// <summary>A transport stream packet header (ISO/IEC 13818-1 §2.4.3.2).</summary>
internal readonly record struct TsPacketHeader(int Pid, bool PayloadStart, bool RandomAccess, bool Discontinuity, int ContinuityCounter);

/// <summary>
/// Reads 188-byte transport stream packets sequentially; 192-byte BDAV (M2TS) packets carry a 4-byte
/// TP_extra_header first and 204-byte packets 16 Reed-Solomon bytes after; both are skipped.
/// </summary>
internal sealed class TsPacketReader : IDisposable
{
    public const int PacketSize = 188;
    private readonly Stream _stream;
    private readonly int _stride;
    private readonly int _prefix;
    private readonly byte[] _buffer;
    private int _pos;
    private int _end;

    public TsPacketReader(Stream stream, int stride, long start = 0)
    {
        _stream = stream;
        _stride = stride;
        _prefix = stride == 192 ? 4 : 0;
        _buffer = new byte[stride * 4096];
        _stream.Position = start;
    }

    /// <summary>Position of the next packet in the file.</summary>
    public long Position => _stream.Position - (_end - _pos);

    /// <summary>Reads the next packet; false at the end of the file. Packets that lost sync are skipped.</summary>
    public bool Next(out TsPacketHeader header, out ReadOnlySpan<byte> payload)
    {
        while (true)
        {
            if (_end - _pos < _stride && !Fill())
            {
                header = default;
                payload = default;
                return false;
            }

            var packet = _buffer.AsSpan(_pos + _prefix, PacketSize);
            if (packet[0] != 0x47)
            {
                Resync();
                continue;
            }

            _pos += _stride;
            if ((packet[1] & 0x80) != 0) // transport_error_indicator
                continue;
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            var control = (packet[3] >> 4) & 3;
            var offset = 4;
            bool randomAccess = false, discontinuity = false;
            if ((control & 2) != 0)
            {
                var length = packet[4];
                if (length > 0)
                {
                    discontinuity = (packet[5] & 0x80) != 0;
                    randomAccess = (packet[5] & 0x40) != 0;
                }

                offset = 5 + length;
            }

            if ((control & 1) == 0 || offset >= PacketSize)
            {
                header = new TsPacketHeader(pid, false, randomAccess, discontinuity, packet[3] & 0x0F);
                payload = default;
                return true;
            }

            header = new TsPacketHeader(pid, (packet[1] & 0x40) != 0, randomAccess, discontinuity, packet[3] & 0x0F);
            payload = packet[offset..];
            return true;
        }
    }

    private bool Fill()
    {
        if (_pos > 0)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _end - _pos);
            _end -= _pos;
            _pos = 0;
        }

        while (_end < _buffer.Length)
        {
            var n = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (n == 0)
                break;
            _end += n;
        }

        return _end - _pos >= _stride;
    }

    /// <summary>Finds the next position where sync bytes repeat at the packet stride.</summary>
    private void Resync()
    {
        _pos++;
        while (true)
        {
            if (_end - _pos < _stride * 3 && !Fill())
                return;
            for (; _pos + _stride * 2 + _prefix < _end; _pos++)
            {
                if (_buffer[_pos + _prefix] == 0x47 && _buffer[_pos + _prefix + _stride] == 0x47 && _buffer[_pos + _prefix + _stride * 2] == 0x47)
                    return;
            }

            if (!Fill())
                return;
        }
    }

    /// <summary>Detects the packet stride (188, 192 or 204) and the offset of the first packet; null when not a transport stream.</summary>
    public static (int Stride, int Offset)? Detect(ReadOnlySpan<byte> header)
    {
        foreach (var stride in new[] { 188, 192, 204 })
        {
            var prefix = stride == 192 ? 4 : 0;
            for (var offset = 0; offset < stride && offset + prefix + stride * 4 < header.Length; offset++)
            {
                var ok = true;
                for (var i = 0; i < 5 && ok; i++)
                    ok = header[offset + prefix + i * stride] == 0x47;
                if (ok)
                    return (stride, offset);
            }
        }

        return null;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>A program-specific information section being assembled across packets.</summary>
internal sealed class PsiAssembler
{
    private readonly List<byte> _data = [];
    private bool _started;

    /// <summary>Feeds a packet payload; returns the completed section (table_id onwards) or null.</summary>
    public byte[]? Add(ReadOnlySpan<byte> payload, bool start)
    {
        if (start)
        {
            if (payload.Length < 1)
                return null;
            var pointer = payload[0];
            if (1 + pointer > payload.Length)
                return null;
            _data.Clear();
            _data.AddRange(payload[(1 + pointer)..]);
            _started = true;
        }
        else if (_started)
        {
            _data.AddRange(payload);
        }
        else
        {
            return null;
        }

        if (_data.Count < 3)
            return null;
        var length = ((_data[1] & 0x0F) << 8) | _data[2];
        if (_data.Count < 3 + length)
            return null;
        _started = false;
        return _data.GetRange(0, 3 + length).ToArray();
    }
}

/// <summary>An elementary stream of a program (PMT entry).</summary>
internal sealed record TsStreamInfo(int Pid, int StreamType, IReadOnlyList<(int Tag, byte[] Data)> Descriptors)
{
    public byte[]? Descriptor(int tag) => Descriptors.FirstOrDefault(d => d.Tag == tag).Data;

    /// <summary>Registration descriptor format identifier (e.g. "HDMV", "AC-3", "HEVC").</summary>
    public string? Registration => Descriptor(0x05) is { Length: >= 4 } r ? System.Text.Encoding.ASCII.GetString(r, 0, 4) : null;

    /// <summary>ISO 639 language descriptor.</summary>
    public string? Language => Descriptor(0x0A) is { Length: >= 3 } l ? System.Text.Encoding.ASCII.GetString(l, 0, 3).Trim('\0', ' ') : null;
}

/// <summary>PAT/PMT parsing.</summary>
internal static class Psi
{
    /// <summary>Program number → PMT PID from a program association section.</summary>
    public static List<(int Program, int PmtPid)> ParsePat(ReadOnlySpan<byte> s)
    {
        var result = new List<(int, int)>();
        if (s.Length < 12 || s[0] != 0x00)
            return result;
        var end = 3 + (((s[1] & 0x0F) << 8) | s[2]) - 4; // without CRC
        for (var i = 8; i + 4 <= end; i += 4)
        {
            var program = BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
            var pid = BinaryPrimitives.ReadUInt16BigEndian(s[(i + 2)..]) & 0x1FFF;
            if (program != 0)
                result.Add((program, pid));
        }

        return result;
    }

    /// <summary>The elementary streams of a program map section.</summary>
    public static List<TsStreamInfo>? ParsePmt(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16 || s[0] != 0x02)
            return null;
        var end = Math.Min(s.Length, 3 + (((s[1] & 0x0F) << 8) | s[2])) - 4;
        var programInfoLength = BinaryPrimitives.ReadUInt16BigEndian(s[10..]) & 0x0FFF;
        var pos = 12 + programInfoLength;
        var streams = new List<TsStreamInfo>();
        while (pos + 5 <= end)
        {
            var type = s[pos];
            var pid = BinaryPrimitives.ReadUInt16BigEndian(s[(pos + 1)..]) & 0x1FFF;
            var infoLength = BinaryPrimitives.ReadUInt16BigEndian(s[(pos + 3)..]) & 0x0FFF;
            var descriptors = new List<(int, byte[])>();
            var d = pos + 5;
            var dEnd = Math.Min(end, d + infoLength);
            while (d + 2 <= dEnd)
            {
                var tag = s[d];
                var length = s[d + 1];
                if (d + 2 + length > dEnd)
                    break;
                descriptors.Add((tag, s.Slice(d + 2, length).ToArray()));
                d += 2 + length;
            }

            streams.Add(new TsStreamInfo(pid, type, descriptors));
            pos += 5 + infoLength;
        }

        return streams;
    }
}

/// <summary>A reassembled PES packet.</summary>
internal sealed record Pes(long? Pts, long? Dts, byte[] Data, bool RandomAccess);

/// <summary>Assembles PES packets from the payloads of one PID.</summary>
internal sealed class PesAssembler
{
    private readonly List<byte> _data = [];
    private bool _randomAccess;
    private int _expected = -1;

    /// <summary>Feeds a packet; returns a PES completed by it (by the start of the next one or by its declared length).</summary>
    public Pes? Add(ReadOnlySpan<byte> payload, bool start, bool randomAccess)
    {
        Pes? completed = null;
        if (start)
        {
            completed = Complete();
            _randomAccess = randomAccess;
            _data.AddRange(payload);
            _expected = payload.Length >= 6 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1
                ? BinaryPrimitives.ReadUInt16BigEndian(payload[4..]) is var length and > 0 ? length + 6 : -1
                : -1;
        }
        else if (_data.Count > 0)
        {
            _data.AddRange(payload);
        }

        if (completed is null && _expected > 0 && _data.Count >= _expected)
            completed = Complete();
        return completed;
    }

    /// <summary>The PES being assembled (end of the file).</summary>
    public Pes? Flush() => Complete();

    private Pes? Complete()
    {
        if (_data.Count == 0)
            return null;
        var data = _data.ToArray();
        _data.Clear();
        if (_expected > 0 && data.Length > _expected)
            data = data[.._expected];
        _expected = -1;
        return Parse(data, _randomAccess);
    }

    /// <summary>Parses a PES packet (ISO/IEC 13818-1 §2.4.3.6); null when malformed.</summary>
    public static Pes? Parse(byte[] data, bool randomAccess)
    {
        if (data.Length < 9 || data[0] != 0 || data[1] != 0 || data[2] != 1)
            return null;
        var streamId = data[3];
        if (streamId is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xFF or 0xF2 or 0xF8)
            return new Pes(null, null, data[6..], randomAccess);
        var flags = data[7] >> 6;
        var headerLength = data[8];
        if (9 + headerLength > data.Length)
            return null;
        long? pts = flags >= 2 && headerLength >= 5 ? Timestamp(data.AsSpan(9)) : null;
        long? dts = flags == 3 && headerLength >= 10 ? Timestamp(data.AsSpan(14)) : null;
        return new Pes(pts, dts, data[(9 + headerLength)..], randomAccess);
    }

    /// <summary>A 33-bit 90 kHz timestamp coded in 5 bytes.</summary>
    public static long Timestamp(ReadOnlySpan<byte> p) =>
        ((long)(p[0] & 0x0E) << 29) | ((long)p[1] << 22) | ((long)(p[2] & 0xFE) << 14) | ((long)p[3] << 7) | ((long)p[4] >> 1);
}

/// <summary>Extends 33-bit timestamps across wraparounds (every 26.5 hours) relative to the last value seen.</summary>
internal sealed class TimestampUnwrapper
{
    private const long Wrap = 1L << 33;
    private long? _last;

    public long Unwrap(long raw)
    {
        if (_last is not { } last)
        {
            _last = raw;
            return raw;
        }

        var k = (long)Math.Round((last - raw) / (double)Wrap);
        var value = raw + k * Wrap;
        _last = value;
        return value;
    }
}
