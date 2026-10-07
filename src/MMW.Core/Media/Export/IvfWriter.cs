using System.Buffers.Binary;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// IVF files ("DKIF"; AV1, AV2, VP8, VP9): a 32-byte header, then every frame with its size and presentation time.
/// AV1 and AV2 samples get their temporal delimiter back (and the configuration's OBUs where a sample needs them).
/// </summary>
/// <remarks>
/// The time base is the track's frame duration in its timescale when it has one (reduced, e.g. 1/25 s), so timestamps
/// count frames as in the files encoders write; otherwise the timescale itself.
/// </remarks>
internal sealed class IvfWriter : TrackWriter
{
    private static readonly byte[] s_av1TemporalDelimiter = [0x12, 0x00];

    private readonly uint _rate;
    private readonly uint _scale;
    private readonly long _ticksPerUnit;
    private readonly List<byte[]> _av2Configuration;
    private readonly byte[] _av1ConfigObus;
    private uint _frames;
    private bool _first = true;

    public IvfWriter(ExportContext context)
        : base(context)
    {
        var timescale = Math.Max(1u, Config.Timescale);
        var duration = Config.DefaultSampleDuration is > 0 and <= uint.MaxValue ? Config.DefaultSampleDuration : 1;
        var gcd = Gcd(timescale, duration);
        _rate = (uint)(timescale / gcd);
        _scale = (uint)(duration / gcd);
        _ticksPerUnit = duration;
        _av2Configuration = Config.Codec == CodecType.Av2 && Config.Extradata is { } av2C ? Av2.ConfigurationObus(av2C) : [];
        _av1ConfigObus = Config.Codec == CodecType.Av1 && Config.Extradata is { Length: > 4 } av1C && av1C[0] == 0x81 ? av1C[4..] : [];
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return Math.Max(1, a);
    }

    public override void Start()
    {
        Span<byte> h = stackalloc byte[32];
        "DKIF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 32);
        var fourCc = Config.Codec switch
        {
            CodecType.Av1 => "AV01"u8,
            CodecType.Av2 => "AV02"u8,
            CodecType.Vp8 => "VP80"u8,
            _ => "VP90"u8,
        };
        fourCc.CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], (ushort)Math.Clamp(Config.Width, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)Math.Clamp(Config.Height, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], _rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], _scale);
        Output.Write(h);
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        byte[]? frame = null;
        switch (Config.Codec)
        {
            case CodecType.Av1:
            {
                var config = _first && _av1ConfigObus.Length > 0 && Av1.FindSequenceHeader(data).IsEmpty ? _av1ConfigObus : [];
                frame = [.. s_av1TemporalDelimiter, .. config, .. data];
                break;
            }

            case CodecType.Av2:
                // A sample that carries configuration OBUs (a new sequence) keeps them instead of the track's.
                frame = Av2.ToTemporalUnit(data, _av2Configuration, (_first || sample.IsSync) && !HasConfiguration(data));
                break;
        }

        _first = false;
        var payload = frame ?? data;
        var pts = sample.Pts - Context.Source.MediaStart;
        var units = (long)Math.Round(pts / (double)_ticksPerUnit, MidpointRounding.AwayFromZero);
        Span<byte> h = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(h[4..], units);
        Output.Write(h);
        Output.Write(payload);
        _frames++;
    }

    private static bool HasConfiguration(ReadOnlySpan<byte> sample)
    {
        var found = false;
        Av2.ForEachObu(sample, (header, _, _) => found = Av2.IsConfiguration(header.Type));
        return found;
    }

    public override void Finish()
    {
        var end = Output.Position;
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, _frames);
        Output.Position = 24;
        Output.Write(count);
        Output.Position = end;
    }
}
