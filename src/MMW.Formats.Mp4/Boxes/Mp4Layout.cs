using System.Buffers.Binary;
using System.Globalization;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>A top-level box of the file: its position, and its parsed tree when it is small enough to load.</summary>
public sealed record TopLevelBox(string Type, long Offset, long Size, int HeaderSize, Box? Loaded)
{
    public long End => Offset + Size;

    public bool IsPadding => Type is "free" or "skip" or "wide";
}

/// <summary>Top-level structure of an MP4 file.</summary>
public sealed class Mp4Layout
{
    /// <summary>Top-level boxes other than these are referenced by position only (never loaded).</summary>
    private static readonly HashSet<string> s_loaded = ["ftyp", "moov", "uuid", "pdin", "styp"];

    private Mp4Layout(List<TopLevelBox> boxes, long fileLength)
    {
        Boxes = boxes;
        FileLength = fileLength;
    }

    public IReadOnlyList<TopLevelBox> Boxes { get; }

    public long FileLength { get; }

    public TopLevelBox Moov => Boxes.FirstOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoBox, "moov"));

    public Box? Ftyp => Boxes.FirstOrDefault(b => b.Type == "ftyp")?.Loaded;

    /// <summary>True when only padding follows the moov box.</summary>
    public bool MoovIsLast
    {
        get
        {
            var index = Boxes.ToList().FindIndex(b => b.Type == "moov");
            return Boxes.Skip(index + 1).All(b => b.IsPadding);
        }
    }

    public static Mp4Layout Read(Stream stream)
    {
        var boxes = new List<TopLevelBox>();
        var length = stream.Length;
        long pos = 0;
        Span<byte> header = stackalloc byte[16];
        while (pos + 8 <= length)
        {
            stream.Position = pos;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Box.Latin1.GetString(header.Slice(4, 4));
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - pos;
            }

            if (size < headerSize)
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_TopLevelBoxInvalidSize, type, pos, size));

            if (pos + size > length)
            {
                // Truncated trailing box (often an interrupted mdat): keep what exists.
                size = length - pos;
            }

            Box? loaded = null;
            if (s_loaded.Contains(type))
            {
                if (size > 512L * 1024 * 1024)
                    throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_BoxTooLarge, type, size));
                var buffer = new byte[size];
                stream.Position = pos;
                stream.ReadExactly(buffer);
                loaded = BoxParser.ParseSingle(buffer);
            }

            boxes.Add(new TopLevelBox(type, pos, size, headerSize, loaded));
            pos += size;
        }

        if (!boxes.Any(b => b.Type == "moov"))
            throw new InvalidDataException(Strings.Error_NotMp4);

        return new Mp4Layout(boxes, length);
    }

    public static Mp4Layout Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }
}
