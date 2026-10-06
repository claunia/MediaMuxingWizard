using System.Buffers.Binary;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>Serializes box trees.</summary>
public static class BoxWriter
{
    public static byte[] ToArray(Box box)
    {
        var size = box.Size;
        if (size > int.MaxValue)
            throw new InvalidOperationException($"Box '{box.Type}' is too large to build in memory.");
        var buffer = new byte[size];
        var written = Write(box, buffer);
        System.Diagnostics.Debug.Assert(written == size);
        return buffer;
    }

    public static void WriteTo(Box box, Stream stream) => stream.Write(ToArray(box));

    private static int Write(Box box, Span<byte> dest)
    {
        var size = box.Size;
        int pos;
        if (size > uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, 1);
            WriteType(box.Type, dest[4..]);
            BinaryPrimitives.WriteUInt64BigEndian(dest[8..], (ulong)size);
            pos = 16;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)size);
            WriteType(box.Type, dest[4..]);
            pos = 8;
        }

        if (box.UserType is { } ut)
        {
            ut.CopyTo(dest[pos..]);
            pos += ut.Length;
        }

        box.Payload.CopyTo(dest[pos..]);
        pos += box.Payload.Length;
        if (box.Children is not null)
        {
            foreach (var child in box.Children)
                pos += Write(child, dest[pos..]);
        }

        return pos;
    }

    public static void WriteType(string type, Span<byte> dest)
    {
        if (Box.Latin1.GetBytes(type, dest) != 4)
            throw new InvalidOperationException($"Invalid box type '{type}'.");
    }

    /// <summary>Builds an 8-byte header for a box of <paramref name="size"/> bytes (header included).</summary>
    public static byte[] Header(string type, long size)
    {
        if (size > uint.MaxValue)
        {
            var big = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(big, 1);
            WriteType(type, big.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(big.AsSpan(8), (ulong)size);
            return big;
        }

        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        WriteType(type, header.AsSpan(4));
        return header;
    }

    /// <summary>A <c>free</c> box of exactly <paramref name="size"/> bytes (≥ 8).</summary>
    public static Box Free(long size)
    {
        if (size < 8)
            throw new ArgumentOutOfRangeException(nameof(size), "A free box needs at least 8 bytes.");
        return new Box("free", new byte[size - 8]);
    }
}
