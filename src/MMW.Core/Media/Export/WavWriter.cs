using System.Buffers.Binary;

namespace MMW.Core.Media.Export;

/// <summary>
/// RIFF WAVE files (.wav). PCM is written as WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT or, for more than two channels or
/// more than 16 bits, WAVE_FORMAT_EXTENSIBLE, little-endian (big-endian samples are byte-swapped; 8-bit samples are
/// unsigned). ACM audio keeps its own WAVEFORMATEX. The RIFF sizes are capped at 4 GiB (no RF64).
/// </summary>
internal sealed class WavWriter : TrackWriter
{
    private static readonly byte[] s_pcmGuidTail = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private readonly int _bytesPerSample;
    private readonly bool _swap;
    private readonly bool _flipSign;
    private long _dataStart;
    private long _dataLength;
    private byte[] _buffer = [];

    public WavWriter(ExportContext context)
        : base(context)
    {
        if (Config.Codec != CodecType.Pcm)
            return;
        _bytesPerSample = Bits(Config) / 8;
        _swap = Config.PcmBigEndian && _bytesPerSample > 1;

        // QuickTime 'twos' 8-bit is signed; WAVE 8-bit is unsigned.
        _flipSign = _bytesPerSample == 1 && Config.SourceCodecId == "twos";
    }

    private static int Bits(CodecConfig config) => config.BitsPerSample > 0 ? config.BitsPerSample : config.PcmFloat ? 32 : 16;

    /// <summary>True for PCM layouts a WAVE file stores (whole bytes per sample, a known channel count).</summary>
    public static bool CanWrite(CodecConfig config) =>
        config.Channels > 0 && Bits(config) % 8 == 0 && Bits(config) <= 64 && (!config.PcmFloat || Bits(config) is 32 or 64);

    public override void Start()
    {
        Output.Write("RIFF"u8);
        WriteU32(0);
        Output.Write("WAVE"u8);
        var format = Config.Codec == CodecType.Pcm ? PcmFormat() : Config.Extradata!;
        Output.Write("fmt "u8);
        WriteU32((uint)format.Length);
        Output.Write(format);
        if (format.Length % 2 != 0)
            Output.WriteByte(0);
        Output.Write("data"u8);
        WriteU32(0);
        _dataStart = Output.Position;
    }

    private byte[] PcmFormat()
    {
        var bits = Bits(Config);
        var channels = Config.Channels;
        var blockAlign = channels * bits / 8;
        var extensible = channels > 2 || bits > 16;
        var f = new byte[extensible ? 40 : Config.PcmFloat ? 18 : 16];
        var tag = extensible ? 0xFFFE : Config.PcmFloat ? 3 : 1;
        BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), (uint)Config.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), (uint)(Config.SampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(14), (ushort)bits);
        if (!extensible)
            return f; // the float format's cbSize stays 0
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(18), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(20), ChannelMask(channels));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(24), Config.PcmFloat ? 3u : 1u);
        s_pcmGuidTail.CopyTo(f, 28);
        return f;
    }

    /// <summary>The usual speaker positions for a channel count (WAVEFORMATEXTENSIBLE dwChannelMask).</summary>
    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x13F,
        8 => 0x63F,
        _ => 0,
    };

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData();
        if (!_swap && !_flipSign)
        {
            Output.Write(data.Span);
        }
        else
        {
            if (_buffer.Length < data.Length)
                _buffer = new byte[data.Length];
            var b = _buffer.AsSpan(0, data.Length);
            data.Span.CopyTo(b);
            if (_flipSign)
            {
                foreach (ref var x in b)
                    x ^= 0x80;
            }
            else
            {
                for (var i = 0; i + _bytesPerSample <= b.Length; i += _bytesPerSample)
                    b.Slice(i, _bytesPerSample).Reverse();
            }

            Output.Write(b);
        }

        _dataLength += data.Length;
    }

    public override void Finish()
    {
        if (_dataLength % 2 != 0)
            Output.WriteByte(0);
        var end = Output.Position;
        Output.Position = 4;
        WriteU32((uint)Math.Min(end - 8, uint.MaxValue));
        Output.Position = _dataStart - 4;
        WriteU32((uint)Math.Min(_dataLength, uint.MaxValue));
        Output.Position = end;
    }

    private void WriteU32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        Output.Write(b);
    }
}
