using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// Writes one logical Ogg bitstream (RFC 3533): packets are laced into pages of about 4 KiB, each page carrying the
/// granule position of the last packet completed on it (-1 when none is).
/// </summary>
internal sealed class OggPageWriter(Stream output, uint serial)
{
    private const int TargetPageSize = 4096;

    private static readonly uint[] s_crc = BuildCrcTable();

    private readonly List<byte> _segments = new(255);
    private byte[] _body = new byte[TargetPageSize + 255 * 255];
    private int _bodyLength;
    private uint _sequence;
    private long _granule = -1;
    private bool _continued;
    private bool _bos = true;

    /// <summary>Adds a packet that ends at <paramref name="granule"/>.</summary>
    public void AddPacket(ReadOnlySpan<byte> packet, long granule)
    {
        if (_bodyLength >= TargetPageSize && _segments.Count > 0)
            EmitPage(false);
        var offset = 0;
        while (true)
        {
            if (_segments.Count == 255)
            {
                EmitPage(false);
                _continued = offset > 0;
            }

            var lace = Math.Min(packet.Length - offset, 255);
            _segments.Add((byte)lace);
            if (_bodyLength + lace > _body.Length)
                Array.Resize(ref _body, _body.Length * 2);
            packet.Slice(offset, lace).CopyTo(_body.AsSpan(_bodyLength));
            _bodyLength += lace;
            offset += lace;
            if (lace < 255)
                break;
        }

        _granule = granule;
    }

    /// <summary>Changes the granule position of the last packet added (still pending), e.g. to trim the end of the stream.</summary>
    public void SetLastGranule(long granule) => _granule = granule;

    /// <summary>Writes the pending packets as a page (headers must end their page).</summary>
    public void Flush(bool endOfStream = false)
    {
        if (_segments.Count > 0 || endOfStream)
            EmitPage(endOfStream);
    }

    private void EmitPage(bool endOfStream)
    {
        var header = new byte[27 + _segments.Count];
        "OggS"u8.CopyTo(header);
        header[4] = 0;
        header[5] = (byte)((_continued ? 1 : 0) | (_bos ? 2 : 0) | (endOfStream ? 4 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), _granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), _sequence++);
        header[26] = (byte)_segments.Count;
        _segments.CopyTo(header, 27);
        var body = _body.AsSpan(0, _bodyLength);
        var crc = Crc(0, header);
        crc = Crc(crc, body);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), crc);
        output.Write(header);
        output.Write(body);

        _segments.Clear();
        _bodyLength = 0;
        _granule = -1;
        _continued = false;
        _bos = false;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = (crc << 8) ^ s_crc[((crc >> 24) & 0xFF) ^ b];
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var j = 0; j < 8; j++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }

        return table;
    }
}

/// <summary>
/// Ogg Opus (.opus, RFC 7845): the OpusHead of the track, an OpusTags header, then the packets with granule positions
/// in 48 kHz samples (pre-skip included); the last page's granule position drops the end padding of the last packet.
/// </summary>
internal sealed class OggOpusWriter(ExportContext context) : TrackWriter(context)
{
    private OggPageWriter _ogg = null!;
    private long _granule;

    public override void Start()
    {
        _ogg = new OggPageWriter(Output, 0x4F707573);
        var head = Config.Extradata is { Length: >= 19 } h && h.AsSpan().StartsWith("OpusHead"u8)
            ? h
            : Opus.DefaultHead(Config.Channels, (int)Math.Round(Config.CodecDelay.TotalSeconds * 48000), Config.SampleRate > 0 ? Config.SampleRate : 48000);
        _ogg.AddPacket(head, 0);
        _ogg.Flush();
        _ogg.AddPacket(OggTags.Build("OpusTags"u8), 0);
        _ogg.Flush();
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        _granule += Opus.PacketSamples(data);
        var trim = sample.TrimEnd > 0 && Config.Timescale > 0 ? sample.TrimEnd * 48000 / Config.Timescale : 0;
        _ogg.AddPacket(data, Math.Max(0, _granule - trim));
    }

    public override void Finish() => _ogg.Flush(true);
}

/// <summary>The comment header of an Ogg stream: a vendor string and no comments.</summary>
internal static class OggTags
{
    private static ReadOnlySpan<byte> Vendor => "Media Muxing Wizard"u8;

    public static byte[] Build(ReadOnlySpan<byte> magic, bool framingBit = false)
    {
        var b = new byte[magic.Length + 4 + Vendor.Length + 4 + (framingBit ? 1 : 0)];
        magic.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(magic.Length), (uint)Vendor.Length);
        Vendor.CopyTo(b.AsSpan(magic.Length + 4));
        if (framingBit)
            b[^1] = 1;
        return b;
    }
}

/// <summary>The three Vorbis headers of a Matroska-style (Xiph-laced) CodecPrivate.</summary>
internal sealed record VorbisHeaders(byte[] Identification, byte[] Comment, byte[] Setup)
{
    public static VorbisHeaders? Parse(byte[]? extradata)
    {
        if (extradata is not { Length: > 3 } d || d[0] != 2)
            return null;
        var pos = 1;
        var sizes = new int[2];
        for (var i = 0; i < 2; i++)
        {
            while (pos < d.Length && d[pos] == 255)
            {
                sizes[i] += 255;
                pos++;
            }

            if (pos >= d.Length)
                return null;
            sizes[i] += d[pos++];
        }

        if (pos + sizes[0] + sizes[1] > d.Length)
            return null;
        var id = d.AsSpan(pos, sizes[0]).ToArray();
        var comment = d.AsSpan(pos + sizes[0], sizes[1]).ToArray();
        var setup = d.AsSpan(pos + sizes[0] + sizes[1]).ToArray();
        if (id.Length < 30 || id[0] != 1 || setup.Length == 0 || setup[0] != 5)
            return null;
        return new VorbisHeaders(id, comment, setup);
    }
}

/// <summary>
/// Ogg Vorbis (.ogg): the three headers of the track, then the packets with granule positions counted from the block
/// sizes (the modes are read from the end of the setup header, as FFmpeg's Vorbis parser does). When the modes cannot
/// be read, the granule positions come from the sample times.
/// </summary>
internal sealed class OggVorbisWriter : TrackWriter
{
    private readonly VorbisHeaders _headers;
    private readonly int[] _blockSizes;
    private readonly bool[]? _modeLongBlock;
    private OggPageWriter _ogg = null!;
    private long _granule;
    private int _previousBlock;

    public OggVorbisWriter(ExportContext context)
        : base(context)
    {
        _headers = VorbisHeaders.Parse(Config.Extradata)!;
        var sizes = _headers.Identification[28];
        _blockSizes = [1 << (sizes & 0x0F), 1 << (sizes >> 4)];
        _modeLongBlock = ReadModes(_headers.Setup);
    }

    public override void Start()
    {
        _ogg = new OggPageWriter(Output, 0x566F7262);
        _ogg.AddPacket(_headers.Identification, 0);
        _ogg.Flush();
        _ogg.AddPacket(_headers.Comment, 0);
        _ogg.AddPacket(_headers.Setup, 0);
        _ogg.Flush();
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        var rate = Config.SampleRate > 0 ? Config.SampleRate : (int)BinaryPrimitives.ReadUInt32LittleEndian(_headers.Identification.AsSpan(12));
        if (_modeLongBlock is not null && data.Length > 0 && (data[0] & 1) == 0)
        {
            var modeBits = _modeLongBlock.Length > 1 ? 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)_modeLongBlock.Length - 1) : 0;
            var mode = (data[0] >> 1) & ((1 << modeBits) - 1);
            var block = mode < _modeLongBlock.Length && _modeLongBlock[mode] ? _blockSizes[1] : _blockSizes[0];
            if (_previousBlock > 0)
                _granule += (_previousBlock + block) / 4;
            _previousBlock = block;
        }
        else if (_modeLongBlock is null && Config.Timescale > 0)
        {
            var end = sample.Pts + sample.Duration - Context.Source.MediaStart;
            _granule = Math.Max(_granule, (long)Math.Round(end * (double)rate / Config.Timescale));
        }

        var trim = sample.TrimEnd > 0 && Config.Timescale > 0 ? sample.TrimEnd * rate / Config.Timescale : 0;
        _ogg.AddPacket(data, Math.Max(0, _granule - trim));
    }

    public override void Finish() => _ogg.Flush(true);

    /// <summary>
    /// The block flag of each mode of a setup header, read backwards from its framing bit (each mode is a block flag,
    /// two zero 16-bit fields and an 8-bit mapping, preceded by the 6-bit mode count); null when they cannot be found.
    /// </summary>
    private static bool[]? ReadModes(byte[] setup)
    {
        // The setup header packs bits from the least significant bit of each byte: reversed, it reads MSB-first backwards.
        var reversed = setup.Reverse().ToArray();
        var r = new ReverseReader(reversed);
        var framing = -1;
        while (r.Left > 97)
        {
            if (r.Read(1) == 1)
            {
                framing = r.Position;
                break;
            }
        }

        if (framing < 0)
            return null;
        var lastCount = 0;
        var count = 0;
        while (r.Left >= 97)
        {
            if (r.Read(8) > 63 || r.Read(16) != 0 || r.Read(16) != 0)
                break;
            r.Read(1);
            count++;
            if (count > 64)
                break;
            var saved = r.Position;
            if ((int)r.Read(6) + 1 == count)
                lastCount = count;
            r.Position = saved;
        }

        if (lastCount == 0)
            return null;
        r.Position = framing;
        var modes = new bool[lastCount];
        for (var i = lastCount - 1; i >= 0; i--)
        {
            r.Read(40);
            modes[i] = r.Read(1) == 1;
        }

        return modes;
    }

    private sealed class ReverseReader(byte[] data)
    {
        public int Position { get; set; }

        public int Left => data.Length * 8 - Position;

        public ulong Read(int bits)
        {
            ulong v = 0;
            for (var i = 0; i < bits; i++, Position++)
                v = (v << 1) | (uint)((data[Position >> 3] >> (7 - (Position & 7))) & 1);
            return v;
        }
    }
}
