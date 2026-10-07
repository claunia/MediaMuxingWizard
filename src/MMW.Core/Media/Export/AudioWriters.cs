using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// ADTS AAC (.aac): every raw AAC frame behind a 7-byte header built from the AudioSpecificConfig. ADTS can only
/// signal the four MPEG-2 profiles: HE-AAC (SBR/PS) is written as its AAC-LC core (decoders find the SBR data in the
/// frames, as with mkvextract), other object types as AAC-LC; a channel layout given by a program config element is
/// written as channel configuration 0.
/// </summary>
internal sealed class AdtsWriter : TrackWriter
{
    private readonly int _profile;
    private readonly int _samplingIndex;
    private readonly int _channelConfig;

    public AdtsWriter(ExportContext context)
        : base(context)
    {
        var asc = Config.Extradata ?? [];
        var (objectType, rateIndex, channelConfig) = ReadConfig(asc);
        _profile = objectType is >= 1 and <= 4 ? objectType - 1 : 1;
        _samplingIndex = rateIndex;
        _channelConfig = channelConfig;
    }

    /// <summary>The core object type, sampling frequency index and channel configuration of an AudioSpecificConfig.</summary>
    private (int ObjectType, int RateIndex, int ChannelConfig) ReadConfig(ReadOnlySpan<byte> asc)
    {
        try
        {
            var r = new BitReader(asc);
            var aot = ObjectType(ref r);
            var rateIndex = RateIndex(ref r);
            var channels = (int)r.Read(4);
            if (aot is 5 or 29)
            {
                // Explicit SBR / PS: the sampling frequency index above is the core's; the core object type follows.
                RateIndex(ref r);
                aot = ObjectType(ref r);
            }

            return (aot, rateIndex, channels <= 7 ? channels : 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return (2, Aac.SamplingIndex(Config.SampleRate), Config.Channels is > 0 and <= 6 ? Config.Channels : Config.Channels == 8 ? 7 : 0);
        }

        static int ObjectType(ref BitReader r)
        {
            var aot = (int)r.Read(5);
            return aot == 31 ? 32 + (int)r.Read(6) : aot;
        }

        int RateIndex(ref BitReader r)
        {
            var index = (int)r.Read(4);
            return index == 15 ? Aac.SamplingIndex((int)r.Read(24)) : index;
        }
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        var length = Math.Min(data.Length + 7, 0x1FFF);
        Span<byte> h = stackalloc byte[7];
        h[0] = 0xFF;
        h[1] = 0xF1; // MPEG-4, layer 0, no CRC
        h[2] = (byte)((_profile << 6) | (_samplingIndex << 2) | (_channelConfig >> 2));
        h[3] = (byte)(((_channelConfig & 3) << 6) | (length >> 11));
        h[4] = (byte)(length >> 3);
        h[5] = (byte)(((length & 7) << 5) | 0x1F); // buffer fullness 0x7FF (variable rate)
        h[6] = 0xFC; // one raw data block
        Output.Write(h);
        Output.Write(data);
    }
}

/// <summary>AC-4 (.ac4): every raw_ac4_frame in an ac4_syncframe (ETSI TS 103 190-1 Annex G) without CRC.</summary>
internal sealed class Ac4Writer(ExportContext context) : TrackWriter(context)
{
    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        if (Ac4.SyncFrameLength(data) == data.Length && data.Length > 0)
        {
            Output.Write(data); // already a sync frame
            return;
        }

        Span<byte> h = stackalloc byte[7];
        h[0] = 0xAC;
        h[1] = 0x40;
        int length;
        if (data.Length < 0xFFFF)
        {
            BinaryPrimitives.WriteUInt16BigEndian(h[2..], (ushort)data.Length);
            length = 4;
        }
        else
        {
            h[2] = 0xFF;
            h[3] = 0xFF;
            h[4] = (byte)(data.Length >> 16);
            h[5] = (byte)(data.Length >> 8);
            h[6] = (byte)data.Length;
            length = 7;
        }

        Output.Write(h[..length]);
        Output.Write(data);
    }
}

/// <summary>
/// MPEG-H 3D Audio MHAS streams (.mhas, ISO/IEC 23008-3 §14): 'mhm1'/'mhm2' samples are MHAS packets already; the raw
/// access units of 'mha1'/'mha2' are wrapped in frame packets, with a sync and a configuration packet (from 'mhaC')
/// before the first frame and every sync frame.
/// </summary>
internal sealed class MpegHWriter : TrackWriter
{
    private const int Label = 1;

    private readonly bool _packetized;
    private readonly byte[]? _config;
    private bool _first = true;

    public MpegHWriter(ExportContext context)
        : base(context)
    {
        var entry = Config.Extradata ?? [];
        _packetized = QuickTime.EntryType(entry) is not ("mha1" or "mha2");
        _config = MpegH.EntryConfig(entry)?.Config;
    }

    /// <summary>True when the track's samples can be written as MHAS (raw access units need the 'mhaC' configuration).</summary>
    public static bool CanWrite(CodecConfig config)
    {
        var entry = config.Extradata ?? [];
        return QuickTime.EntryType(entry) is not ("mha1" or "mha2") || MpegH.EntryConfig(entry) is not null;
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        if (_packetized)
        {
            // A stream that does not start with its configuration gets the one of the sample entry first.
            if (_first && _config is not null && !StartsWithConfig(data))
                WriteConfiguration();
            _first = false;
            Output.Write(data);
            return;
        }

        if ((_first || sample.IsSync) && _config is not null)
            WriteConfiguration();
        _first = false;
        Output.Write(Packet(MpegH.PacketFrame, Label, data.Length));
        Output.Write(data);
    }

    private static bool StartsWithConfig(ReadOnlySpan<byte> data)
    {
        var config = false;
        var other = false;
        MpegH.ForEachPacket(data, (type, _, _) =>
        {
            if (type == MpegH.PacketConfig && !other)
                config = true;
            else if (type == MpegH.PacketFrame)
                other = true;
        });
        return config;
    }

    private void WriteConfiguration()
    {
        Output.Write(Packet(MpegH.PacketSync, 0, 1));
        Output.WriteByte(0xA5); // syncword
        Output.Write(Packet(MpegH.PacketConfig, Label, _config!.Length));
        Output.Write(_config);
    }

    /// <summary>An MHAS packet header: type escapedValue(3,8,8), label escapedValue(2,8,32), length escapedValue(11,24,24).</summary>
    private static byte[] Packet(int type, int label, int length)
    {
        var w = new BitWriter();
        Escaped(w, (ulong)type, 3, 8, 8);
        Escaped(w, (ulong)label, 2, 8, 32);
        Escaped(w, (ulong)length, 11, 24, 24);
        return w.ToArray();

        static void Escaped(BitWriter w, ulong value, int bits1, int bits2, int bits3)
        {
            var max1 = (1UL << bits1) - 1;
            if (value < max1)
            {
                w.Write(value, bits1);
                return;
            }

            w.Write(max1, bits1);
            value -= max1;
            var max2 = (1UL << bits2) - 1;
            if (value < max2)
            {
                w.Write(value, bits2);
                return;
            }

            w.Write(max2, bits2);
            w.Write(value - max2, bits3);
        }
    }
}

/// <summary>
/// ALAC in a Core Audio Format file (.caf): 'desc', the ALACSpecificConfig as 'kuki', the packets in 'data', and their
/// sizes and the valid frame count in a 'pakt' chunk after the data.
/// </summary>
internal sealed class CafWriter : TrackWriter
{
    private readonly List<int> _sizes = [];
    private readonly int _framesPerPacket;
    private long _dataStart;
    private long _duration;

    public CafWriter(ExportContext context)
        : base(context)
    {
        var cookie = Config.Extradata!;
        _framesPerPacket = (int)BinaryPrimitives.ReadUInt32BigEndian(cookie);
    }

    private (int Channels, int Bits, int Rate) Format()
    {
        var cookie = Config.Extradata!;
        return (cookie[9], cookie[5], (int)BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(20)));
    }

    public override void Start()
    {
        var (channels, bits, rate) = Format();
        Output.Write("caff"u8);
        WriteU16BigEndian(1);
        WriteU16BigEndian(0);

        Chunk("desc", 32);
        Span<byte> desc = stackalloc byte[32];
        BinaryPrimitives.WriteDoubleBigEndian(desc, rate > 0 ? rate : Config.SampleRate);
        "alac"u8.CopyTo(desc[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(desc[12..], bits switch // format flags: source bit depth
        {
            16 => 1u,
            20 => 2u,
            24 => 3u,
            32 => 4u,
            _ => 0u,
        });
        BinaryPrimitives.WriteUInt32BigEndian(desc[16..], 0); // bytes per packet: variable
        BinaryPrimitives.WriteUInt32BigEndian(desc[20..], (uint)_framesPerPacket);
        BinaryPrimitives.WriteUInt32BigEndian(desc[24..], (uint)channels);
        BinaryPrimitives.WriteUInt32BigEndian(desc[28..], 0);
        Output.Write(desc);

        var cookie = Config.Extradata!;
        Chunk("kuki", cookie.Length);
        Output.Write(cookie);

        Chunk("data", -1);
        _dataStart = Output.Position;
        WriteU32BigEndian(0); // edit count
    }

    private void Chunk(string type, long size)
    {
        Span<byte> h = stackalloc byte[12];
        System.Text.Encoding.ASCII.GetBytes(type, h);
        BinaryPrimitives.WriteInt64BigEndian(h[4..], size);
        Output.Write(h);
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        Output.Write(data);
        _sizes.Add(data.Length);
        _duration += sample.Duration > 0 ? sample.Duration : Config.DefaultSampleDuration;
    }

    public override void Finish()
    {
        var dataEnd = Output.Position;
        Output.Position = _dataStart - 8;
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(size, dataEnd - _dataStart);
        Output.Write(size);
        Output.Position = dataEnd;

        var (_, _, rate) = Format();
        var frames = (long)_sizes.Count * _framesPerPacket;
        var valid = Config.Timescale > 0 && _duration > 0
            ? Math.Min(frames, (long)Math.Round(_duration * (double)(rate > 0 ? rate : Config.SampleRate) / Config.Timescale))
            : frames;
        var table = new List<byte>(_sizes.Count * 2);
        foreach (var s in _sizes)
            WriteVarInt(table, (uint)s);
        Chunk("pakt", 24 + table.Count);
        Span<byte> pakt = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(pakt, _sizes.Count);
        BinaryPrimitives.WriteInt64BigEndian(pakt[8..], valid);
        BinaryPrimitives.WriteInt32BigEndian(pakt[16..], 0); // priming frames
        BinaryPrimitives.WriteInt32BigEndian(pakt[20..], (int)(frames - valid)); // remainder frames
        Output.Write(pakt);
        Output.Write(table.ToArray());
    }

    /// <summary>A CAF variable-length integer: 7 bits per byte, most significant first, high bit set on all but the last.</summary>
    private static void WriteVarInt(List<byte> output, uint value)
    {
        Span<byte> bytes = stackalloc byte[5];
        var n = 0;
        do
        {
            bytes[n++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value != 0);
        for (var i = n - 1; i >= 0; i--)
            output.Add((byte)(bytes[i] | (i > 0 ? 0x80 : 0)));
    }
}
