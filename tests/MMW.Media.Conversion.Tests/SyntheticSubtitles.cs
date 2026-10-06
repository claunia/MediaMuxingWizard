using System.Text;
using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Media.Conversion.Tests;

/// <summary>An in-memory track.</summary>
internal sealed class MemorySampleSource(CodecConfig config, IReadOnlyList<MediaSample> samples) : ISampleSource
{
    private int _next;

    public uint TrackId => 1;

    public CodecConfig Config { get; } = config;

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart => 0;

    public TimeSpan Duration { get; init; }

    public long SampleCountHint => samples.Count;

    public MediaSample? ReadNext() => _next < samples.Count ? samples[_next++].Clone() : null;

    public void Reset() => _next = 0;
}

/// <summary>Builds PGS display sets and DVD subpicture (SPU) packets for decoder tests.</summary>
internal static class SyntheticSubtitles
{
    public const int Width = 40;
    public const int Height = 20;

    /// <summary>
    /// A PGS display set showing a <see cref="Width"/>×<see cref="Height"/> object at (<paramref name="x"/>,
    /// <paramref name="y"/>) on a 1920×1080 canvas: the top half is opaque white (palette entry 1), the bottom half
    /// transparent.
    /// </summary>
    public static byte[] PgsShow(int x, int y, bool forced, ushort composition)
    {
        var s = new List<byte>();

        // PCS
        var pcs = new List<byte>();
        U16(pcs, 1920);
        U16(pcs, 1080);
        pcs.Add(0x10); // frame rate
        U16(pcs, composition);
        pcs.Add(0x80); // epoch start
        pcs.Add(0); // palette update flag
        pcs.Add(0); // palette id
        pcs.Add(1); // objects
        U16(pcs, 0); // object id
        pcs.Add(0); // window id
        pcs.Add((byte)(forced ? 0x40 : 0x00));
        U16(pcs, (ushort)x);
        U16(pcs, (ushort)y);
        Segment(s, 0x16, pcs);

        // WDS
        var wds = new List<byte> { 1, 0 };
        U16(wds, (ushort)x);
        U16(wds, (ushort)y);
        U16(wds, Width);
        U16(wds, Height);
        Segment(s, 0x17, wds);

        // PDS: entry 1 = opaque white (Y=235, Cr=Cb=128).
        Segment(s, 0x14, [0, 0, 1, 235, 128, 128, 255]);

        // ODS
        var rle = new List<byte>();
        for (var row = 0; row < Height; row++)
        {
            if (row < Height / 2)
                rle.AddRange([0x00, 0x80 | Width, 0x01]); // Width pixels of colour 1
            else
                rle.AddRange([0x00, Width]); // Width pixels of colour 0
            rle.AddRange([0x00, 0x00]); // end of line
        }

        var ods = new List<byte>();
        U16(ods, 0); // object id
        ods.Add(0); // version
        ods.Add(0xC0); // first and last fragment
        var length = rle.Count + 4;
        ods.AddRange([(byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        U16(ods, Width);
        U16(ods, Height);
        ods.AddRange(rle);
        Segment(s, 0x15, ods);

        Segment(s, 0x80, []);
        return [.. s];
    }

    /// <summary>A PGS display set clearing the screen.</summary>
    public static byte[] PgsClear(ushort composition)
    {
        var s = new List<byte>();
        var pcs = new List<byte>();
        U16(pcs, 1920);
        U16(pcs, 1080);
        pcs.Add(0x10);
        U16(pcs, composition);
        pcs.Add(0x00); // normal case
        pcs.Add(0);
        pcs.Add(0);
        pcs.Add(0); // no objects
        Segment(s, 0x16, pcs);
        var wds = new List<byte> { 1, 0 };
        U16(wds, 0);
        U16(wds, 0);
        U16(wds, Width);
        U16(wds, Height);
        Segment(s, 0x17, wds);
        Segment(s, 0x80, []);
        return [.. s];
    }

    /// <summary>.idx header for VobSub: 720×480, palette entry 1 white, others black.</summary>
    public static byte[] VobSubIdx()
    {
        var palette = string.Join(", ", Enumerable.Range(0, 16).Select(i => i == 1 ? "ffffff" : "000000"));
        return Encoding.ASCII.GetBytes($"# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: {palette}\n");
    }

    /// <summary>
    /// An SPU showing a <see cref="Width"/>×<see cref="Height"/> opaque white rectangle at (<paramref name="x"/>,
    /// <paramref name="y"/>) for <paramref name="durationMs"/> (stop command), optionally forced.
    /// </summary>
    public static byte[] Spu(int x, int y, int durationMs, bool forced)
    {
        var pixels = new List<byte>();
        for (var line = 0; line < Height; line += 2)
            pixels.AddRange([0x00, 0x01]); // top field: colour 1 to the end of the line
        var bottom = pixels.Count + 4;
        for (var line = 1; line < Height; line += 2)
            pixels.AddRange([0x00, 0x01]);

        var ctrl = 4 + pixels.Count;
        var x2 = x + Width - 1;
        var y2 = y + Height - 1;
        var seq1 = new List<byte> { 0, 0, 0, 0 }; // date, next (patched)
        if (forced)
            seq1.Add(0x00);
        seq1.Add(0x01);
        seq1.AddRange([0x03, 0x32, 0x10]); // colours: pixel n → palette n
        seq1.AddRange([0x04, 0xFF, 0xF0]); // alpha: pixel 0 transparent, others opaque
        seq1.AddRange([0x05, (byte)(x >> 4), (byte)(((x & 0xF) << 4) | (x2 >> 8)), (byte)x2, (byte)(y >> 4), (byte)(((y & 0xF) << 4) | (y2 >> 8)), (byte)y2]);
        seq1.AddRange([0x06, 0x00, 0x04, (byte)(bottom >> 8), (byte)bottom]);
        seq1.Add(0xFF);
        var seq2Offset = ctrl + seq1.Count;
        seq1[2] = (byte)(seq2Offset >> 8);
        seq1[3] = (byte)seq2Offset;
        var date = (int)Math.Round(durationMs * 90.0 / 1024);
        List<byte> seq2 = [(byte)(date >> 8), (byte)date, (byte)(seq2Offset >> 8), (byte)seq2Offset, 0x02, 0xFF];

        var total = ctrl + seq1.Count + seq2.Count;
        var spu = new List<byte> { (byte)(total >> 8), (byte)total, (byte)(ctrl >> 8), (byte)ctrl };
        spu.AddRange(pixels);
        spu.AddRange(seq1);
        spu.AddRange(seq2);
        return [.. spu];
    }

    public static CodecConfig PgsConfig() => new() { Codec = CodecType.Pgs, Kind = TrackKind.Subtitle, Timescale = 1000, SourceCodecId = "S_HDMV/PGS" };

    public static CodecConfig VobSubConfig() => new()
    {
        Codec = CodecType.VobSub, Kind = TrackKind.Subtitle, Timescale = 1000, SourceCodecId = "S_VOBSUB", Extradata = VobSubIdx(),
        SubtitleWidth = 720, SubtitleHeight = 480,
    };

    public static MediaSample Sample(long ms, byte[] data, long duration = 0) => new() { Dts = ms, Data = data, Duration = duration, IsSync = true };

    private static void U16(List<byte> b, ushort v)
    {
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void Segment(List<byte> s, byte type, List<byte> payload)
    {
        s.Add(type);
        U16(s, (ushort)payload.Count);
        s.AddRange(payload);
    }
}
