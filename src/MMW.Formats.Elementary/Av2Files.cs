using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>
/// Raw AV2 files as the reference encoder writes them: IVF ("DKIF", FourCC "AV02"; each frame one encoder packet of
/// one or more temporal units, timed in the header's time base) and length-delimited OBU streams (.obu, AV2 Annex B;
/// no timing). Their packets are turned into samples by <see cref="Av2TemporalUnitSource"/>.
/// </summary>
internal static class Av2Files
{
    public static bool LooksLikeIvf(ReadOnlySpan<byte> header) =>
        header.Length >= 32 && header.StartsWith("DKIF"u8) && header.Slice(8, 4).SequenceEqual("AV02"u8);

    /// <summary>A length-delimited stream starting with a temporal delimiter OBU (AV1 low-overhead streams do not).</summary>
    public static bool LooksLikeObu(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && ((header[0] == 1 && header[1] == Av2.ObuTemporalDelimiter << 2) ||
                               (header[0] == 2 && header[1] == (0x80 | (Av2.ObuTemporalDelimiter << 2))));

    /// <summary>Frame timescale and duration for a frame rate: N/1001 rates exactly, others in milliseconds.</summary>
    public static (uint Timescale, long FrameTicks) Timescale(double fps)
    {
        foreach (var num in new[] { 24000u, 30000u, 48000u, 60000u, 120000u })
        {
            if (Math.Abs(fps - num / 1001.0) < 0.001)
                return (num, 1001);
        }

        return ((uint)Math.Max(1, Math.Round(fps * 1000)), 1000);
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

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        if (rate == 0 || scale == 0)
            (rate, scale) = (25, 1);
        var config = new CodecConfig
        {
            Codec = CodecType.Av2,
            Kind = TrackKind.Video,
            SourceCodecId = "AV02",
            Width = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]),
            Timescale = rate,
            DefaultSampleDuration = scale,
        };
        var source = new ElementarySource(config, () => new IvfParser(open(path), Math.Max(32, (int)headerLength), scale), TimeSpan.Zero, frames);
        var av2 = new Av2TemporalUnitSource(source);
        return new ElementaryDemuxer(path, "AV2 IVF", av2, TimeSpan.FromSeconds(Count(av2) * (double)av2.Config.DefaultSampleDuration / rate));
    }

    public static IDemuxer OpenObu(string path, Func<string, FileStream> open, double? frameRate)
    {
        // Timing comes from the content interpretation OBU when it has some, otherwise from the caller (25 fps by default).
        double streamRate = 0;
        using (var fs = open(path))
        {
            var head = new byte[(int)Math.Min(fs.Length, 1 << 16)];
            fs.ReadExactly(head);
            Av2.ForEachObu(head, (header, _, payload) =>
            {
                if (header.Type == Av2.ObuContentInterpretation && Av2.ParseContentInterpretation(payload) is { FrameRate: > 0 } ci)
                    streamRate = ci.FrameRate;
                return header.Type != Av2.ObuTemporalDelimiter && Av2.IsFrame(header.Type);
            });
        }

        var fps = frameRate is > 0 ? frameRate.Value : streamRate > 0 ? streamRate : 25;
        var (timescale, ticks) = Timescale(fps);
        var config = new CodecConfig
        {
            Codec = CodecType.Av2,
            Kind = TrackKind.Video,
            SourceCodecId = "AV02",
            Timescale = timescale,
            DefaultSampleDuration = ticks,
            FrameRate = fps,
        };
        var source = new ElementarySource(config, () => new ObuParser(open(path), ticks), TimeSpan.Zero, -1);
        var av2 = new Av2TemporalUnitSource(source);
        return new ElementaryDemuxer(path, "AV2 OBU stream", av2, TimeSpan.FromSeconds(Count(av2) * (double)ticks / timescale))
        {
            RequiresFrameRate = streamRate <= 0,
        };
    }

    /// <summary>Temporal units in the stream (read once to know the duration).</summary>
    private static long Count(ISampleSource source)
    {
        long count = 0;
        source.Reset();
        while (source.ReadNext() is not null)
            count++;
        source.Reset();
        return count;
    }
}

/// <summary>IVF frames as packets timed in the file's time base.</summary>
internal sealed class IvfParser(Stream stream, int headerLength, uint scale) : IElementaryParser
{
    private bool _started;

    public MediaSample? Next()
    {
        if (!_started)
        {
            stream.Position = headerLength;
            _started = true;
        }

        Span<byte> h = stackalloc byte[12];
        if (stream.ReadAtLeast(h, 12, throwOnEndOfStream: false) < 12)
            return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(h);
        var pts = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[4..]);
        if (size == 0 || size > stream.Length - stream.Position)
            return null;
        var data = new byte[size];
        stream.ReadExactly(data);
        return new MediaSample { Dts = pts * scale, Duration = scale, IsSync = true, Data = data };
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>The temporal units of a length-delimited OBU stream, one packet each, at a fixed frame duration.</summary>
internal sealed class ObuParser(Stream stream, long frameTicks) : IElementaryParser
{
    private readonly List<byte> _unit = [];
    private byte[]? _pending;
    private long _index;

    public MediaSample? Next()
    {
        while (true)
        {
            var obu = _pending ?? ReadObu();
            _pending = null;
            if (obu is null)
                return Emit();
            var type = (obu[0] >> 2) & 0x1F;
            if (type == Av2.ObuTemporalDelimiter && _unit.Count > 0)
            {
                _pending = obu;
                return Emit();
            }

            Av2.WriteLeb128(_unit, obu.Length);
            _unit.AddRange(obu);
        }
    }

    private MediaSample? Emit()
    {
        if (_unit.Count == 0)
            return null;
        var sample = new MediaSample { Dts = _index++ * frameTicks, Duration = frameTicks, IsSync = true, Data = _unit.ToArray() };
        _unit.Clear();
        return sample;
    }

    private byte[]? ReadObu()
    {
        long size = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return null;
            size |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                break;
        }

        if (size < 1 || size > stream.Length - stream.Position)
            return null;
        var obu = new byte[size];
        stream.ReadExactly(obu);
        return obu;
    }

    public void Dispose() => stream.Dispose();
}
