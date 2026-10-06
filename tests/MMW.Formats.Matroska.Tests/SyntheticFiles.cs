using MMW.Formats.Matroska.Ebml;

namespace MMW.Formats.Matroska.Tests;

/// <summary>How the synthetic file indexes its elements.</summary>
public enum SeekHeadMode
{
    /// <summary>A SeekHead with exactly the entries needed and no free space after it.</summary>
    Tight,

    /// <summary>No SeekHead, but a Void before the first Cluster.</summary>
    NoneWithVoid,

    /// <summary>No SeekHead and no free space.</summary>
    None,
}

/// <summary>
/// Re-packs a real file into layouts that muxers rarely produce (unknown sizes, tight or missing SeekHead), keeping
/// the EBML header, Info, Tracks and Clusters byte for byte.
/// </summary>
internal static class SyntheticFiles
{
    public static string Build(string source, bool unknownSizes, SeekHeadMode mode)
    {
        var (_, layout) = MatroskaReader.Read(source, CancellationToken.None);
        var bytes = File.ReadAllBytes(source);
        byte[] Slice(TopLevelElement e) => bytes[(int)e.Position..(int)e.End];

        var info = Slice(layout.Elements.First(e => e.Id == MatroskaIds.Info));
        var tracks = Slice(layout.Elements.First(e => e.Id == MatroskaIds.Tracks));
        var clusters = new List<byte[]>();
        foreach (var e in layout.Elements.Where(e => e.Id == MatroskaIds.Cluster))
        {
            if (!unknownSizes)
            {
                clusters.Add(Slice(e));
                continue;
            }

            var header = new byte[12];
            EbmlVarInt.WriteId(header, MatroskaIds.Cluster);
            EbmlVarInt.WriteUnknownSize(header.AsSpan(4), 8);
            clusters.Add([.. header, .. bytes[(int)(e.Position + e.HeaderLength)..(int)e.End]]);
        }

        var body = new List<byte>();
        if (mode == SeekHeadMode.Tight)
        {
            byte[] seekHead = [];
            for (var i = 0; i < 3; i++)
            {
                var w = new EbmlWriter();
                AddSeek(w, MatroskaIds.Info, seekHead.Length);
                AddSeek(w, MatroskaIds.Tracks, seekHead.Length + info.Length);
                seekHead = EbmlWriter.Element(MatroskaIds.SeekHead, w.WrittenSpan);
            }

            body.AddRange(seekHead);
        }

        body.AddRange(info);
        body.AddRange(tracks);
        if (mode == SeekHeadMode.NoneWithVoid)
            body.AddRange([.. EbmlWriter.VoidHeader(200), .. new byte[200 - EbmlWriter.VoidHeader(200).Length]]);
        foreach (var c in clusters)
            body.AddRange(c);

        var segment = new byte[12];
        EbmlVarInt.WriteId(segment, MatroskaIds.Segment);
        if (unknownSizes)
            EbmlVarInt.WriteUnknownSize(segment.AsSpan(4), 8);
        else
            EbmlVarInt.WriteSize(segment.AsSpan(4), (ulong)body.Count, 8);

        var path = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N") + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. bytes[..(int)layout.SegmentPosition], .. segment, .. body]);
        return path;
    }

    private static void AddSeek(EbmlWriter w, ulong id, long position) => w.Master(MatroskaIds.Seek, s =>
    {
        s.Binary(MatroskaIds.SeekId, EbmlVarInt.EncodeId(id));
        s.UInt(MatroskaIds.SeekPosition, (ulong)position);
    });
}
