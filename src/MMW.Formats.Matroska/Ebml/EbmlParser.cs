using System.Buffers.Binary;
using System.Text;

namespace MMW.Formats.Matroska.Ebml;

/// <summary>A child element of an in-memory master element.</summary>
/// <param name="Id">Element ID.</param>
/// <param name="Element">The complete element (header and data), for verbatim copies.</param>
/// <param name="Data">The element data.</param>
internal readonly record struct EbmlChild(ulong Id, ReadOnlyMemory<byte> Element, ReadOnlyMemory<byte> Data)
{
    public ulong UInt => EbmlParser.ReadUInt(Data.Span);

    public long Int => EbmlParser.ReadInt(Data.Span);

    public double Float => EbmlParser.ReadFloat(Data.Span);

    public string String => EbmlParser.ReadString(Data.Span);
}

/// <summary>Parses master-element payloads that were loaded into memory, and decodes EBML value types.</summary>
internal static class EbmlParser
{
    /// <summary>Epoch of Matroska date elements (2001-01-01T00:00:00 UTC).</summary>
    public static readonly DateTime MatroskaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Splits a master payload into its children. Parsing stops at the first malformed or truncated child
    /// (the rest of the payload is ignored).
    /// </summary>
    public static List<EbmlChild> Children(ReadOnlyMemory<byte> payload)
    {
        var list = new List<EbmlChild>();
        var span = payload.Span;
        var pos = 0;
        while (pos < span.Length)
        {
            if (!EbmlVarInt.TryReadId(span[pos..], out var id, out var idLen))
                break;
            if (!EbmlVarInt.TryReadSize(span[(pos + idLen)..], out var size, out var sizeLen))
                break;

            var dataStart = pos + idLen + sizeLen;
            var remaining = (ulong)(span.Length - dataStart);
            var dataLen = size == EbmlVarInt.UnknownSize ? remaining : size;
            if (dataLen > remaining)
                break;

            var end = dataStart + (int)dataLen;
            list.Add(new EbmlChild(id, payload[pos..end], payload[dataStart..end]));
            pos = end;
        }

        return list;
    }

    public static ulong ReadUInt(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8)
            throw new InvalidDataException($"Unsigned integer element of {data.Length} bytes.");
        ulong v = 0;
        foreach (var b in data)
            v = (v << 8) | b;
        return v;
    }

    public static long ReadInt(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return 0;
        if (data.Length > 8)
            throw new InvalidDataException($"Signed integer element of {data.Length} bytes.");
        long v = (sbyte)data[0];
        for (var i = 1; i < data.Length; i++)
            v = (v << 8) | data[i];
        return v;
    }

    public static double ReadFloat(ReadOnlySpan<byte> data) => data.Length switch
    {
        0 => 0,
        4 => BinaryPrimitives.ReadSingleBigEndian(data),
        8 => BinaryPrimitives.ReadDoubleBigEndian(data),
        _ => throw new InvalidDataException($"Float element of {data.Length} bytes."),
    };

    /// <summary>Decodes a String or UTF-8 element (both are decoded as UTF-8, which is a superset of the ASCII strings).</summary>
    public static string ReadString(ReadOnlySpan<byte> data)
    {
        var nul = data.IndexOf((byte)0);
        if (nul >= 0)
            data = data[..nul];
        return Encoding.UTF8.GetString(data);
    }

    public static DateTime ReadDate(ReadOnlySpan<byte> data) => MatroskaEpoch.AddTicks(ReadInt(data) / 100);

    /// <summary>First child with the given ID, if any.</summary>
    public static EbmlChild? Child(this List<EbmlChild> children, ulong id)
    {
        foreach (var c in children)
        {
            if (c.Id == id)
                return c;
        }

        return null;
    }

    public static ulong GetUInt(this List<EbmlChild> children, ulong id, ulong defaultValue) =>
        children.Child(id) is { } c ? c.UInt : defaultValue;

    public static string? GetString(this List<EbmlChild> children, ulong id) =>
        children.Child(id) is { } c ? c.String : null;

    public static double? GetFloat(this List<EbmlChild> children, ulong id) =>
        children.Child(id) is { } c ? c.Float : null;
}
