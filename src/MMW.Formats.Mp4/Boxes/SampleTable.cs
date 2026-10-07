using System.Buffers.Binary;
using System.Globalization;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>One sample's position and timing.</summary>
public readonly record struct SampleInfo(long Offset, int Size, long Dts, uint Duration, int CompositionOffset, bool IsSync);

/// <summary>Expands the compact <c>stbl</c> tables into per-sample information.</summary>
public static class SampleTable
{
    /// <summary>Total sample count and byte size without expanding every sample.</summary>
    public static (int Count, long Bytes) Summary(Box stbl)
    {
        var stsz = stbl.Find("stsz");
        if (stsz is not null)
        {
            var p = stsz.Payload.AsSpan();
            var fixedSize = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
            if (fixedSize != 0)
                return (count, (long)fixedSize * count);
            long total = 0;
            for (var i = 0; i < count && 12 + i * 4 + 4 <= p.Length; i++)
                total += BinaryPrimitives.ReadUInt32BigEndian(p[(12 + i * 4)..]);
            return (count, total);
        }

        var stz2 = stbl.Find("stz2");
        if (stz2 is not null)
        {
            var sizes = Stz2Sizes(stz2);
            return (sizes.Length, sizes.Sum(s => (long)s));
        }

        return (0, 0);
    }

    public static SampleInfo[] Expand(Box stbl)
    {
        var sizes = SampleSizes(stbl);
        var count = sizes.Length;
        var offsets = ChunkOffsets(stbl);
        var result = new SampleInfo[count];

        // Offsets from stsc + chunk offsets.
        var stsc = stbl.Find("stsc")?.Payload ?? throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_MissingBox, "stsc"));
        var entries = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(4));
        var sampleOffsets = new long[count];
        var sample = 0;
        for (var e = 0; e < entries && sample < count; e++)
        {
            var baseIx = 8 + e * 12;
            var firstChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(baseIx)) - 1;
            var perChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(baseIx + 4));
            var lastChunk = e + 1 < entries ? (int)BinaryPrimitives.ReadUInt32BigEndian(stsc.AsSpan(baseIx + 12)) - 1 : offsets.Length;
            for (var chunk = firstChunk; chunk < lastChunk && chunk < offsets.Length && sample < count; chunk++)
            {
                var pos = offsets[chunk];
                for (var s = 0; s < perChunk && sample < count; s++)
                {
                    sampleOffsets[sample] = pos;
                    pos += sizes[sample];
                    sample++;
                }
            }
        }

        // Decoding times.
        var durations = new uint[count];
        var stts = stbl.Find("stts")?.Payload;
        if (stts is not null)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(4));
            var i = 0;
            for (var e = 0; e < n && i < count; e++)
            {
                var c = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(8 + e * 8));
                var d = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12 + e * 8));
                for (var k = 0u; k < c && i < count; k++)
                    durations[i++] = d;
            }
        }

        var cto = new int[count];
        var ctts = stbl.Find("ctts")?.Payload;
        if (ctts is not null)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(ctts.AsSpan(4));
            var i = 0;
            for (var e = 0; e < n && i < count; e++)
            {
                var c = BinaryPrimitives.ReadUInt32BigEndian(ctts.AsSpan(8 + e * 8));
                var o = BinaryPrimitives.ReadInt32BigEndian(ctts.AsSpan(12 + e * 8));
                for (var k = 0u; k < c && i < count; k++)
                    cto[i++] = o;
            }
        }

        HashSet<int>? sync = null;
        var stss = stbl.Find("stss")?.Payload;
        if (stss is not null)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(4));
            sync = new HashSet<int>(n);
            for (var e = 0; e < n; e++)
                sync.Add((int)BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(8 + e * 4)) - 1);
        }

        long dts = 0;
        for (var i = 0; i < count; i++)
        {
            result[i] = new SampleInfo(sampleOffsets[i], sizes[i], dts, durations[i], cto[i], sync?.Contains(i) ?? true);
            dts += durations[i];
        }

        return result;
    }

    public static int[] SampleSizes(Box stbl)
    {
        var stsz = stbl.Find("stsz");
        if (stsz is not null)
        {
            var p = stsz.Payload.AsSpan();
            var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
            var sizes = new int[count];
            for (var i = 0; i < count; i++)
                sizes[i] = fixedSize != 0 ? fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(p[(12 + i * 4)..]);
            return sizes;
        }

        var stz2 = stbl.Find("stz2");
        return stz2 is not null ? Stz2Sizes(stz2) : [];
    }

    private static int[] Stz2Sizes(Box stz2)
    {
        var p = stz2.Payload.AsSpan();
        var fieldSize = p[7];
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        var sizes = new int[count];
        for (var i = 0; i < count; i++)
        {
            sizes[i] = fieldSize switch
            {
                4 => (p[12 + i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF,
                8 => p[12 + i],
                16 => BinaryPrimitives.ReadUInt16BigEndian(p[(12 + i * 2)..]),
                _ => throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_InvalidStz2FieldSize, fieldSize)),
            };
        }

        return sizes;
    }

    public static long[] ChunkOffsets(Box stbl)
    {
        if (stbl.Find("stco") is { } stco)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(stco.Payload.AsSpan(4));
            var r = new long[n];
            for (var i = 0; i < n; i++)
                r[i] = BinaryPrimitives.ReadUInt32BigEndian(stco.Payload.AsSpan(8 + i * 4));
            return r;
        }

        if (stbl.Find("co64") is { } co64)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(co64.Payload.AsSpan(4));
            var r = new long[n];
            for (var i = 0; i < n; i++)
                r[i] = (long)BinaryPrimitives.ReadUInt64BigEndian(co64.Payload.AsSpan(8 + i * 8));
            return r;
        }

        return [];
    }

    /// <summary>Rewrites the chunk offset table with mapped offsets, choosing stco or co64 as needed.</summary>
    public static void RemapChunkOffsets(Box stbl, Func<long, long> map, bool force64)
    {
        var offsets = ChunkOffsets(stbl);
        if (offsets.Length == 0 && stbl.Find("stco") is null && stbl.Find("co64") is null)
            return;

        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = map(offsets[i]);

        stbl.Children!.RemoveAll(c => c.Type is "stco" or "co64");
        stbl.Children.Add(BuildChunkOffsets(offsets, force64));
    }

    public static Box BuildChunkOffsets(IReadOnlyList<long> offsets, bool force64)
    {
        var use64 = force64 || offsets.Any(o => o > uint.MaxValue);
        var b = new PayloadBuilder().FullBox(0, 0).U32((uint)offsets.Count);
        foreach (var o in offsets)
        {
            if (use64)
                b.U64((ulong)o);
            else
                b.U32((uint)o);
        }

        return new Box(use64 ? "co64" : "stco", b.ToArray());
    }
}
