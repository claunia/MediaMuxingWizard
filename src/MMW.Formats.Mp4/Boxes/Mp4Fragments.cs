using System.Buffers.Binary;
using System.Globalization;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>
/// The samples of a fragmented MP4 (ISO/IEC 14496-12 §8.8): every movie fragment ('moof') adds, per track fragment
/// ('traf'), the runs ('trun') of samples that follow it, with the defaults of 'tfhd' and the movie's 'trex'.
/// </summary>
public static class Mp4Fragments
{
    private const uint BaseDataOffsetPresent = 0x000001;
    private const uint SampleDescriptionIndexPresent = 0x000002;
    private const uint DefaultDurationPresent = 0x000008;
    private const uint DefaultSizePresent = 0x000010;
    private const uint DefaultFlagsPresent = 0x000020;
    private const uint DefaultBaseIsMoof = 0x020000;

    private const uint DataOffsetPresent = 0x000001;
    private const uint FirstSampleFlagsPresent = 0x000004;
    private const uint DurationPresent = 0x000100;
    private const uint SizePresent = 0x000200;
    private const uint FlagsPresent = 0x000400;
    private const uint CompositionOffsetPresent = 0x000800;

    private readonly record struct Defaults(uint Duration, uint Size, uint Flags);

    /// <summary>
    /// The fragment samples of each track (by track ID), in file order, timed after <paramref name="startDts"/> (the
    /// end of each track's 'moov' samples) unless a 'tfdt' says otherwise. Empty when the file has no fragments.
    /// </summary>
    public static Dictionary<uint, List<SampleInfo>> Read(Stream stream, Mp4Layout layout, Box moov, IReadOnlyDictionary<uint, long> startDts)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(moov);
        ArgumentNullException.ThrowIfNull(startDts);
        var result = new Dictionary<uint, List<SampleInfo>>();
        var trex = new Dictionary<uint, Defaults>();
        foreach (var t in moov.FindPath("mvex")?.FindAll("trex") ?? [])
        {
            var p = t.Payload;
            if (p.Length >= 24)
                trex[BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4))] = new Defaults(
                    BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20)));
        }

        var nextDts = new Dictionary<uint, long>(startDts);
        foreach (var top in layout.Boxes.Where(b => b.Type == "moof"))
        {
            if (top.Size > 64L * 1024 * 1024)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_MoofTooLarge, top.Offset, top.Size));
            var buffer = new byte[top.Size];
            stream.Position = top.Offset;
            stream.ReadExactly(buffer);
            var moof = BoxParser.ParseSingle(buffer);
            var previousEnd = top.Offset; // where the data of the previous track fragment ended
            var first = true;
            foreach (var traf in moof.FindAll("traf"))
            {
                if (traf.Find("tfhd")?.Payload is not { Length: >= 8 } tfhd)
                    continue;
                var flags = BinaryPrimitives.ReadUInt32BigEndian(tfhd) & 0xFFFFFF;
                var trackId = BinaryPrimitives.ReadUInt32BigEndian(tfhd.AsSpan(4));
                var defaults = trex.GetValueOrDefault(trackId);
                var at = 8;
                long baseOffset = (flags & DefaultBaseIsMoof) != 0 || first ? top.Offset : previousEnd;
                if ((flags & BaseDataOffsetPresent) != 0)
                {
                    baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(tfhd.AsSpan(at));
                    at += 8;
                }

                if ((flags & SampleDescriptionIndexPresent) != 0)
                    at += 4;
                var duration = (flags & DefaultDurationPresent) != 0 ? Read32(tfhd, ref at) : defaults.Duration;
                var size = (flags & DefaultSizePresent) != 0 ? Read32(tfhd, ref at) : defaults.Size;
                var sampleFlags = (flags & DefaultFlagsPresent) != 0 ? Read32(tfhd, ref at) : defaults.Flags;
                first = false;

                if (traf.Find("tfdt")?.Payload is { Length: >= 8 } tfdt)
                    nextDts[trackId] = tfdt[0] == 1 && tfdt.Length >= 12 ? (long)BinaryPrimitives.ReadUInt64BigEndian(tfdt.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(tfdt.AsSpan(4));
                var dts = nextDts.GetValueOrDefault(trackId);
                if (!result.TryGetValue(trackId, out var samples))
                    result[trackId] = samples = [];

                var dataOffset = baseOffset;
                foreach (var trun in traf.FindAll("trun"))
                {
                    var p = trun.Payload;
                    if (p.Length < 8)
                        continue;
                    var version = p[0];
                    var runFlags = BinaryPrimitives.ReadUInt32BigEndian(p) & 0xFFFFFF;
                    var count = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4));
                    var q = 8;
                    if ((runFlags & DataOffsetPresent) != 0)
                        dataOffset = baseOffset + BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(Advance(ref q, 4)));
                    uint? firstFlags = (runFlags & FirstSampleFlagsPresent) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(Advance(ref q, 4))) : null;
                    for (var i = 0u; i < count; i++)
                    {
                        var d = (runFlags & DurationPresent) != 0 ? Read32(p, ref q) : duration;
                        var s = (runFlags & SizePresent) != 0 ? Read32(p, ref q) : size;
                        var f = (runFlags & FlagsPresent) != 0 ? Read32(p, ref q) : i == 0 && firstFlags is { } ff ? ff : sampleFlags;
                        var cto = 0;
                        if ((runFlags & CompositionOffsetPresent) != 0)
                        {
                            var raw = Read32(p, ref q);
                            cto = version == 0 ? (int)Math.Min(raw, int.MaxValue) : unchecked((int)raw);
                        }

                        var sync = ((f >> 16) & 1) == 0; // sample_is_non_sync_sample
                        samples.Add(new SampleInfo(dataOffset, (int)s, dts, d, cto, sync));
                        dataOffset += s;
                        dts += d;
                    }
                }

                nextDts[trackId] = dts;
                previousEnd = dataOffset;
            }
        }

        return result;
    }

    private static uint Read32(byte[] p, ref int at)
    {
        if (at + 4 > p.Length)
            throw new InvalidDataException(Strings.Error_TrackFragmentTruncated);
        var v = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at));
        at += 4;
        return v;
    }

    private static int Advance(ref int at, int count)
    {
        var start = at;
        at += count;
        return start;
    }
}
