using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>
/// Raw AV1 files: IVF ("DKIF", FourCC "AV01", one temporal unit per frame, timed by the header's time base), the
/// low-overhead OBU format (.obu, as FFmpeg and aomenc write it: OBUs with size fields, temporal units started by
/// temporal delimiters) and the Annex B length-delimited format (aomenc --annexb). Without IVF timing, the sequence
/// header's timing info or the chosen frame rate times the units.
/// </summary>
internal static class Av1Files
{
    public static bool LooksLikeIvf(ReadOnlySpan<byte> header) =>
        header.Length >= 32 && header.StartsWith("DKIF"u8) && header.Slice(8, 4).SequenceEqual("AV01"u8);

    /// <summary>Low-overhead format: a temporal delimiter OBU with an empty size field first.</summary>
    public static bool LooksLikeLowOverhead(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == 0x12 && header[1] == 0x00;

    /// <summary>Annex B: temporal_unit_size, frame_unit_size, then the temporal delimiter (with or without size field).</summary>
    public static bool LooksLikeAnnexB(ReadOnlySpan<byte> header)
    {
        var pos = 0;
        if (!Av2.ReadLeb128(header, ref pos, out var tu) || tu < 2 || !Av2.ReadLeb128(header, ref pos, out var fu) || fu > tu ||
            !Av2.ReadLeb128(header, ref pos, out var length) || pos >= header.Length)
            return false;
        return (length == 1 && header[pos] == 0x10) || (length == 2 && header[pos] == 0x12 && pos + 1 < header.Length && header[pos + 1] == 0);
    }

    public static IDemuxer OpenIvf(string path, Func<string, FileStream> open)
    {
        Span<byte> h = stackalloc byte[32];
        long frames;
        using (var fs = open(path))
        {
            fs.ReadExactly(h);
            frames = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        }

        var rate = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        if (rate == 0 || scale == 0)
            (rate, scale) = (25, 1);
        var headerLength = Math.Max(32, (int)BinaryPrimitives.ReadUInt16LittleEndian(h[6..]));
        var config = new CodecConfig { Codec = CodecType.Av1, Kind = TrackKind.Video, SourceCodecId = "AV01", Timescale = rate, DefaultSampleDuration = scale };
        var source = new Av1Source(new ElementarySource(config, () => new IvfParser(open(path), headerLength, scale), TimeSpan.Zero, frames), 0);
        return new ElementaryDemuxer(path, "AV1 IVF", source, Duration(source));
    }

    public static IDemuxer OpenObu(string path, Func<string, FileStream> open, double? frameRate, bool annexB)
    {
        // Timing: the sequence header's timing info, otherwise the caller's frame rate (25 fps by default).
        var probe = new ElementarySource(new CodecConfig { Codec = CodecType.Av1, Kind = TrackKind.Video, Timescale = 1, DefaultSampleDuration = 1 },
            () => new Av1ObuParser(open(path), 1, annexB), TimeSpan.Zero, -1);
        var streamRate = probe.ReadNext() is { } first && SequenceHeader(Av1.FindSequenceHeader(first.Data.Span)) is { FrameRate: > 0 } seq ? seq.FrameRate : 0;
        probe.Dispose();

        var fps = frameRate is > 0 ? frameRate.Value : streamRate > 0 ? streamRate : 25;
        var (timescale, ticks) = Av2Files.Timescale(fps);
        var config = new CodecConfig { Codec = CodecType.Av1, Kind = TrackKind.Video, SourceCodecId = "AV01", Timescale = timescale, DefaultSampleDuration = ticks };
        var source = new Av1Source(new ElementarySource(config, () => new Av1ObuParser(open(path), ticks, annexB), TimeSpan.Zero, -1), fps);
        return new ElementaryDemuxer(path, annexB ? "AV1 Annex B" : "AV1 OBU stream", source, Duration(source))
        {
            RequiresFrameRate = streamRate <= 0,
        };
    }

    /// <summary>The sequence header of a sequence header OBU (header, size field, payload); null when there is none.</summary>
    internal static Av1SequenceHeader? SequenceHeader(ReadOnlySpan<byte> obu)
    {
        if (obu.IsEmpty)
            return null;
        var pos = 1 + ((obu[0] & 4) != 0 ? 1 : 0);
        return Av2.ReadLeb128(obu, ref pos, out _) ? Av1.ParseSequenceHeader(obu[pos..]) : null;
    }

    private static TimeSpan Duration(ISampleSource source)
    {
        long count = 0;
        source.Reset();
        while (source.ReadNext() is not null)
            count++;
        source.Reset();
        return TimeSpan.FromSeconds(count * (double)source.Config.DefaultSampleDuration / Math.Max(1u, source.Config.Timescale));
    }
}

/// <summary>
/// AV1 temporal units (low-overhead form) as ISOBMFF / Matroska samples: without temporal delimiters, every OBU with its
/// size field, sync at shown key frames; 'av1C' from the first sequence header.
/// </summary>
internal sealed class Av1Source : ISampleSource, IDisposable
{
    private readonly ElementarySource _inner;
    private readonly bool _reduced;

    public Av1Source(ElementarySource inner, double frameRate)
    {
        _inner = inner;
        var config = inner.Config;
        inner.Reset();
        var sample = inner.ReadNext() is { } first ? Av1.ToSample(first.Data.Span) : [];
        inner.Reset();
        var obu = Av1.FindSequenceHeader(sample);
        if (obu.IsEmpty)
            throw new InvalidDataException("The AV1 stream does not start with a sequence header.");
        var header = Av1Files.SequenceHeader(obu) ?? throw new InvalidDataException("The AV1 sequence header cannot be read.");
        _reduced = header.ReducedStillPictureHeader;
        Config = config with
        {
            Extradata = Av1.BuildAv1C(header, obu),
            Width = header.Width,
            Height = header.Height,
            BitsPerSample = header.BitDepth,
            Color = header.Color,
            FrameRate = frameRate > 0 ? frameRate : config.Timescale / (double)Math.Max(1, config.DefaultSampleDuration),
            VideoProfile = Av1.ProfileLevel(header.Profile, header.Level, header.Tier),
        };
    }

    public uint TrackId => 1;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart => 0;

    public TimeSpan Duration => _inner.Duration;

    public long SampleCountHint => _inner.SampleCountHint;

    public MediaSample? ReadNext()
    {
        if (_inner.ReadNext() is not { } packet)
            return null;
        var sample = Av1.ToSample(packet.Data.Span);
        return new MediaSample { Dts = packet.Dts, Duration = packet.Duration, IsSync = Av1.IsSync(sample, _reduced), Data = sample };
    }

    public void Reset() => _inner.Reset();

    public void Dispose() => _inner.Dispose();
}

/// <summary>Temporal units of a low-overhead or Annex B AV1 stream, as low-overhead data, at a fixed frame duration.</summary>
internal sealed class Av1ObuParser(Stream stream, long frameTicks, bool annexB) : IElementaryParser
{
    private readonly List<byte> _unit = [];
    private byte[]? _pending;
    private long _index;

    public MediaSample? Next()
    {
        byte[]? unit;
        if (annexB)
        {
            if (ReadLeb128() is not { } size || size < 1 || size > stream.Length - stream.Position)
                return null;
            var data = new byte[size];
            stream.ReadExactly(data);
            unit = Av1.FromAnnexB(data);
        }
        else
        {
            unit = NextLowOverhead();
        }

        return unit is null ? null : new MediaSample { Dts = _index++ * frameTicks, Duration = frameTicks, IsSync = true, Data = unit };
    }

    private byte[]? NextLowOverhead()
    {
        while (true)
        {
            var obu = _pending ?? ReadObu();
            _pending = null;
            if (obu is null)
                return Take();
            if (((obu[0] >> 3) & 0x0F) == Av1.ObuTemporalDelimiter && _unit.Count > 0)
            {
                _pending = obu;
                return Take();
            }

            _unit.AddRange(obu);
        }
    }

    private byte[]? Take()
    {
        if (_unit.Count == 0)
            return null;
        var unit = _unit.ToArray();
        _unit.Clear();
        return unit;
    }

    /// <summary>One low-overhead OBU (header, size field and payload).</summary>
    private byte[]? ReadObu()
    {
        var header = stream.ReadByte();
        if (header < 0)
            return null;
        if ((header & 0x02) == 0)
            throw new InvalidDataException("AV1 OBU without a size field in a low-overhead stream.");
        var o = new List<byte> { (byte)header };
        if ((header & 0x04) != 0)
            o.Add((byte)stream.ReadByte());
        long size = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return null;
            o.Add((byte)b);
            size |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                break;
        }

        if (size > stream.Length - stream.Position)
            return null;
        var payload = new byte[size];
        stream.ReadExactly(payload);
        o.AddRange(payload);
        return [.. o];
    }

    private long? ReadLeb128()
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return null;
            value |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                return value;
        }

        return null;
    }

    public void Dispose() => stream.Dispose();
}
