using System.Buffers.Binary;
using System.Text;
using MMW.Core.Chapters;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4;

/// <summary>Reads and builds QuickTime chapter text tracks and Nero <c>chpl</c> chapters.</summary>
internal static class Mp4Chapters
{
    /// <summary>Reads chapters from a text track's samples.</summary>
    public static List<Chapter> ReadTextTrack(Box trak, Stream file, uint movieTimescale)
    {
        var mdhd = trak.FindPath("mdia/mdhd") ?? throw new InvalidDataException("Chapter track without mdhd.");
        var timescale = HeaderBoxes.MdhdTimescale(mdhd);
        var stbl = trak.FindPath("mdia/minf/stbl") ?? throw new InvalidDataException("Chapter track without stbl.");
        var samples = SampleTable.Expand(stbl);
        var editOffset = EditListStart(trak, movieTimescale);

        var result = new List<Chapter>(samples.Length);
        var buffer = new byte[256];
        foreach (var s in samples)
        {
            if (s.Size < 2 || s.Offset + s.Size > file.Length)
                continue;
            if (buffer.Length < s.Size)
                buffer = new byte[s.Size];
            file.Position = s.Offset;
            file.ReadExactly(buffer, 0, s.Size);
            var len = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buffer), s.Size - 2);
            var title = DecodeText(buffer.AsSpan(2, len));
            var start = TimeSpan.FromSeconds((double)s.Dts / timescale) + editOffset;
            result.Add(new Chapter(start < TimeSpan.Zero ? TimeSpan.Zero : start, title));
        }

        return result;
    }

    /// <summary>Delay introduced by an initial empty edit (media_time == -1), in movie time.</summary>
    private static TimeSpan EditListStart(Box trak, uint movieTimescale)
    {
        var elst = trak.FindPath("edts/elst");
        if (elst is null || elst.Payload.Length < 8 || movieTimescale == 0)
            return TimeSpan.Zero;

        var p = elst.Payload.AsSpan();
        var version = p[0];
        var count = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        if (count == 0)
            return TimeSpan.Zero;

        long duration, mediaTime;
        if (version == 1 && p.Length >= 8 + 20)
        {
            duration = (long)BinaryPrimitives.ReadUInt64BigEndian(p[8..]);
            mediaTime = BinaryPrimitives.ReadInt64BigEndian(p[16..]);
        }
        else if (p.Length >= 8 + 12)
        {
            duration = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
            mediaTime = BinaryPrimitives.ReadInt32BigEndian(p[12..]);
        }
        else
        {
            return TimeSpan.Zero;
        }

        return mediaTime == -1 ? TimeSpan.FromSeconds((double)duration / movieTimescale) : TimeSpan.Zero;
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads Nero chapters (<c>moov/udta/chpl</c>); times are in 100 ns units.</summary>
    public static List<Chapter> ReadChpl(Box chpl)
    {
        var p = chpl.Payload.AsSpan();
        var result = new List<Chapter>();
        if (p.Length < 5)
            return result;
        var pos = 4;
        if (p[0] == 1)
            pos += 4;
        if (pos >= p.Length)
            return result;
        int count = p[pos++];
        for (var i = 0; i < count && pos + 9 <= p.Length; i++)
        {
            var start = (long)BinaryPrimitives.ReadUInt64BigEndian(p[pos..]);
            pos += 8;
            int len = p[pos++];
            if (pos + len > p.Length)
                break;
            result.Add(new Chapter(TimeSpan.FromTicks(start), Encoding.UTF8.GetString(p.Slice(pos, len))));
            pos += len;
        }

        return result;
    }

    public static Box BuildChpl(IReadOnlyList<Chapter> chapters)
    {
        var b = new PayloadBuilder().FullBox(1, 0).U32(0).U8(Math.Min(chapters.Count, 255));
        foreach (var c in chapters.Take(255))
        {
            var title = Encoding.UTF8.GetBytes(c.Title);
            if (title.Length > 255)
                title = TruncateUtf8(title, 255);
            b.U64((ulong)c.Start.Ticks).U8(title.Length).Bytes(title);
        }

        return new Box("chpl", b.ToArray());
    }

    private static byte[] TruncateUtf8(byte[] bytes, int max)
    {
        var len = max;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return bytes[..len];
    }

    /// <summary>Encodes one chapter title as a text sample (length-prefixed UTF-8 plus an <c>encd</c> atom).</summary>
    public static byte[] EncodeSample(string title)
    {
        var text = Encoding.UTF8.GetBytes(title);
        if (text.Length > ushort.MaxValue)
            text = TruncateUtf8(text, ushort.MaxValue);
        return new PayloadBuilder().U16(text.Length).Bytes(text).U32(12).Type("encd").U32(0x100).ToArray();
    }

    /// <summary>Chapter tracks plus the media data they reference (text samples, then JPEG preview images).</summary>
    /// <param name="Traks">The text track and, when every chapter has a thumbnail, the image track.</param>
    /// <param name="TrackIds">IDs of <paramref name="Traks"/>, for the <c>chap</c> reference.</param>
    /// <param name="Data">Sample data to store at the data offset given to <see cref="Build"/>.</param>
    public sealed record ChapterTracks(IReadOnlyList<Box> Traks, IReadOnlyList<uint> TrackIds, byte[] Data);

    /// <summary>Builds the chapter text track (and preview image track) whose samples start at <paramref name="dataOffset"/>.</summary>
    public static ChapterTracks Build(uint firstTrackId, IReadOnlyList<Chapter> chapters, TimeSpan movieDuration, uint movieTimescale,
        long dataOffset, bool force64)
    {
        var text = chapters.Select(c => EncodeSample(c.Title)).ToList();
        var textBytes = text.Sum(t => (long)t.Length);
        var traks = new List<Box>
        {
            BuildTextTrack(firstTrackId, chapters, movieDuration, movieTimescale, text.Select(t => t.Length).ToList(), dataOffset, force64),
        };
        var ids = new List<uint> { firstTrackId };
        var data = new MemoryStream();
        foreach (var t in text)
            data.Write(t);

        var images = chapters.Select(c => c.Thumbnail).ToList();
        if (images.All(i => i is { Length: > 0 }) && JpegSize(images[0]!) is { } size)
        {
            var imageId = firstTrackId + 1;
            traks.Add(BuildImageTrack(imageId, chapters, movieDuration, movieTimescale, images.Select(i => i!.Length).ToList(),
                dataOffset + textBytes, force64, size.Width, size.Height));
            ids.Add(imageId);
            foreach (var i in images)
                data.Write(i!);
        }

        return new ChapterTracks(traks, ids, data.ToArray());
    }

    /// <summary>Width and height from a JPEG's start-of-frame marker.</summary>
    public static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return null;
        var pos = 2;
        while (pos + 9 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                return null;
            var marker = jpeg[pos + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 5)..]));
            pos += 2 + length;
        }

        return null;
    }

    /// <summary>Durations (in milliseconds) of each chapter, the last one lasting until the end of the movie.</summary>
    private static List<uint> Durations(IReadOnlyList<Chapter> chapters, TimeSpan movieDuration)
    {
        var durations = new List<uint>(chapters.Count);
        var total = Math.Max((long)Math.Round(movieDuration.TotalMilliseconds), (long)chapters[^1].Start.TotalMilliseconds + 1);
        for (var i = 0; i < chapters.Count; i++)
        {
            var start = (long)chapters[i].Start.TotalMilliseconds;
            var end = i + 1 < chapters.Count ? (long)chapters[i + 1].Start.TotalMilliseconds : total;
            durations.Add((uint)Math.Max(1, end - start));
        }

        return durations;
    }

    /// <summary>Builds the disabled JPEG track that holds chapter preview images.</summary>
    private static Box BuildImageTrack(uint trackId, IReadOnlyList<Chapter> chapters, TimeSpan movieDuration, uint movieTimescale,
        IReadOnlyList<int> sampleSizes, long dataOffset, bool force64, int width, int height)
    {
        const uint timescale = 1000;
        var durations = Durations(chapters, movieDuration);
        var mediaDuration = durations.Sum(d => (long)d);
        var firstStart = (long)chapters[0].Start.TotalMilliseconds;
        var trackDuration = (ulong)(firstStart + mediaDuration) * movieTimescale / timescale;

        var tkhd = new Box("tkhd", new PayloadBuilder()
            .FullBox(0, 0)
            .U32(0).U32(0).U32(trackId).U32(0).U32((uint)Math.Min(trackDuration, uint.MaxValue))
            .Zeros(8).U16(0).U16(0).U16(0).U16(0).UnityMatrix().U32((uint)width << 16).U32((uint)height << 16).ToArray());
        var children = new List<Box> { tkhd };
        if (firstStart > 0)
        {
            var elst = new PayloadBuilder().FullBox(0, 0).U32(2)
                .U32((uint)(firstStart * movieTimescale / timescale)).I32(-1).U32(0x00010000)
                .U32((uint)(mediaDuration * movieTimescale / timescale)).I32(0).U32(0x00010000);
            children.Add(new Box("edts", null, [new Box("elst", elst.ToArray())]));
        }

        var mdhd = new Box("mdhd", new PayloadBuilder().FullBox(0, 0).U32(0).U32(0).U32(timescale)
            .U32((uint)mediaDuration).U16(HeaderBoxes.PackLanguage("und")).U16(0).ToArray());
        var hdlr = new Box("hdlr", new PayloadBuilder().FullBox(0, 0).U32(0).Type("vide").Zeros(12).Utf8("Chapter Images", nullTerminated: true).ToArray());
        var vmhd = new Box("vmhd", new PayloadBuilder().FullBox(0, 1).U16(0).U16(0).U16(0).U16(0).ToArray());
        var dinf = new Box("dinf", null,
            [new Box("dref", new PayloadBuilder().FullBox(0, 0).U32(1).ToArray(), [new Box("url ", new PayloadBuilder().FullBox(0, 1).ToArray())])]);

        var name = new byte[32];
        var label = Encoding.ASCII.GetBytes("Photo - JPEG");
        name[0] = (byte)label.Length;
        label.CopyTo(name, 1);
        var entry = new Box("jpeg", new PayloadBuilder()
            .Zeros(6).U16(1).Zeros(16).U16(width).U16(height).U32(0x00480000).U32(0x00480000).U32(0).U16(1)
            .Bytes(name).U16(0x18).U16(0xFFFF).ToArray(), []);
        var stsd = new Box("stsd", new PayloadBuilder().FullBox(0, 0).U32(1).ToArray(), [entry]);

        var stts = new PayloadBuilder().FullBox(0, 0).U32((uint)durations.Count);
        foreach (var d in durations)
            stts.U32(1).U32(d);
        var stsz = new PayloadBuilder().FullBox(0, 0).U32(0).U32((uint)sampleSizes.Count);
        foreach (var s in sampleSizes)
            stsz.U32((uint)s);
        var stsc = new PayloadBuilder().FullBox(0, 0).U32(1).U32(1).U32((uint)sampleSizes.Count).U32(1);
        var stbl = new Box("stbl", null,
        [
            stsd,
            new Box("stts", stts.ToArray()),
            new Box("stsc", stsc.ToArray()),
            new Box("stsz", stsz.ToArray()),
            SampleTable.BuildChunkOffsets([dataOffset], force64),
        ]);

        children.Add(new Box("mdia", null, [mdhd, hdlr, new Box("minf", null, [vmhd, dinf, stbl])]));
        return new Box("trak", null, children);
    }

    /// <summary>Reads chapter preview images (one JPEG sample per chapter) from an image track.</summary>
    public static List<byte[]> ReadImageTrack(Box trak, Stream file)
    {
        var stbl = trak.FindPath("mdia/minf/stbl") ?? throw new InvalidDataException("Chapter image track without stbl.");
        var result = new List<byte[]>();
        foreach (var s in SampleTable.Expand(stbl))
        {
            if (s.Size <= 0 || s.Offset + s.Size > file.Length)
            {
                result.Add([]);
                continue;
            }

            var buffer = new byte[s.Size];
            file.Position = s.Offset;
            file.ReadExactly(buffer);
            result.Add(buffer);
        }

        return result;
    }

    /// <summary>
    /// Builds a disabled QuickTime chapter text track whose samples are stored contiguously at
    /// <paramref name="dataOffset"/>.
    /// </summary>
    public static Box BuildTextTrack(uint trackId, IReadOnlyList<Chapter> chapters, TimeSpan movieDuration, uint movieTimescale,
        IReadOnlyList<int> sampleSizes, long dataOffset, bool force64)
    {
        const uint timescale = 1000;
        var durations = Durations(chapters, movieDuration);

        var mediaDuration = durations.Sum(d => (long)d);
        var firstStart = (long)chapters[0].Start.TotalMilliseconds;
        var trackDuration = (ulong)(firstStart + mediaDuration) * movieTimescale / timescale;

        var tkhd = new Box("tkhd", new PayloadBuilder()
            .FullBox(0, 0) // disabled, not in movie/preview: players must not render it as subtitles
            .U32(0).U32(0).U32(trackId).U32(0).U32((uint)Math.Min(trackDuration, uint.MaxValue))
            .Zeros(8).U16(0).U16(0).U16(0).U16(0).UnityMatrix().U32(0).U32(0).ToArray());

        var children = new List<Box> { tkhd };
        if (firstStart > 0)
        {
            // Initial empty edit so the first chapter starts at its time.
            var elst = new PayloadBuilder().FullBox(0, 0).U32(2)
                .U32((uint)(firstStart * movieTimescale / timescale)).I32(-1).U32(0x00010000)
                .U32((uint)(mediaDuration * movieTimescale / timescale)).I32(0).U32(0x00010000);
            children.Add(new Box("edts", null, [new Box("elst", elst.ToArray())]));
        }

        var mdhd = new Box("mdhd", new PayloadBuilder().FullBox(0, 0).U32(0).U32(0).U32(timescale)
            .U32((uint)mediaDuration).U16(HeaderBoxes.PackLanguage("eng")).U16(0).ToArray());
        var hdlr = new Box("hdlr", new PayloadBuilder().FullBox(0, 0).U32(0).Type("text").Zeros(12).Utf8("ChapterListHandler", nullTerminated: true).ToArray());

        var gmhd = new Box("gmhd", null,
        [
            new Box("gmin", new PayloadBuilder().FullBox(0, 0).U16(0x40).U16(0x8000).U16(0x8000).U16(0x8000).U16(0).U16(0).ToArray()),
            new Box("text", new PayloadBuilder().U16(1).U32(0).U32(0).U32(0).U32(1).U32(0).U32(0).U32(0).U32(0x4000).U16(0).ToArray()),
        ]);
        var dinf = new Box("dinf", null,
            [new Box("dref", new PayloadBuilder().FullBox(0, 0).U32(1).ToArray(), [new Box("url ", new PayloadBuilder().FullBox(0, 1).ToArray())])]);

        // Text sample description (same layout ffmpeg and mp4v2 use for chapter tracks).
        var textEntry = new Box("text", new PayloadBuilder()
            .Zeros(6).U16(1)
            .U32(1).U8(0).U8(0).U32(0) // displayFlags, justification, background
            .U16(0).U16(0).U16(0).U16(0) // default text box
            .U16(0).U16(0).U16(1).U8(0).U8(0).U32(0) // style record
            .U32(13).Type("ftab").U16(1).U16(1).U8(0)
            .ToArray());
        var stsd = new Box("stsd", new PayloadBuilder().FullBox(0, 0).U32(1).ToArray(), [textEntry]);

        var stts = new PayloadBuilder().FullBox(0, 0).U32((uint)durations.Count);
        foreach (var d in durations)
            stts.U32(1).U32(d);
        var stsz = new PayloadBuilder().FullBox(0, 0).U32(0).U32((uint)sampleSizes.Count);
        foreach (var s in sampleSizes)
            stsz.U32((uint)s);
        var stsc = new PayloadBuilder().FullBox(0, 0).U32(1).U32(1).U32((uint)sampleSizes.Count).U32(1);

        var stbl = new Box("stbl", null,
        [
            stsd,
            new Box("stts", stts.ToArray()),
            new Box("stsc", stsc.ToArray()),
            new Box("stsz", stsz.ToArray()),
            SampleTable.BuildChunkOffsets([dataOffset], force64),
        ]);

        var minf = new Box("minf", null, [gmhd, dinf, stbl]);
        var mdia = new Box("mdia", null, [mdhd, hdlr, minf]);
        children.Add(mdia);
        return new Box("trak", null, children);
    }
}
