using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MMW.Formats.Matroska.Ebml;

/// <summary>Builds EBML element payloads in memory.</summary>
internal sealed class EbmlWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    /// <summary>Writes an unsigned integer element using the minimal number of bytes (at least one).</summary>
    public void UInt(ulong id, ulong value)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, value);
        var skip = 0;
        while (skip < 7 && data[skip] == 0)
            skip++;
        Binary(id, data[skip..]);
    }

    /// <summary>Writes a signed integer element using the minimal two's-complement length.</summary>
    public void Int(ulong id, long value)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(data, value);
        var skip = 0;
        while (skip < 7 && ((data[skip] == 0 && (data[skip + 1] & 0x80) == 0) || (data[skip] == 0xFF && (data[skip + 1] & 0x80) != 0)))
            skip++;
        Binary(id, data[skip..]);
    }

    /// <summary>Writes an 8-byte float element.</summary>
    public void Float(ulong id, double value)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(data, value);
        Binary(id, data);
    }

    /// <summary>Writes a String or UTF-8 element (no terminator).</summary>
    public void String(ulong id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Binary(id, bytes);
    }

    public void Binary(ulong id, ReadOnlySpan<byte> data)
    {
        WriteHeader(id, (ulong)data.Length, 0);
        _buffer.Write(data);
    }

    /// <summary>Writes a master element whose payload is produced by <paramref name="body"/>.</summary>
    public void Master(ulong id, Action<EbmlWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var inner = new EbmlWriter();
        body(inner);
        Binary(id, inner.WrittenSpan);
    }

    /// <summary>Copies already-encoded element bytes verbatim.</summary>
    public void Raw(ReadOnlySpan<byte> encoded) => _buffer.Write(encoded);

    private void WriteHeader(ulong id, ulong size, int sizeLength)
    {
        var span = _buffer.GetSpan(12);
        var n = EbmlVarInt.WriteId(span, id);
        n += EbmlVarInt.WriteSize(span[n..], size, sizeLength);
        _buffer.Advance(n);
    }

    /// <summary>Encodes a complete element (header + payload).</summary>
    /// <param name="id">Element ID.</param>
    /// <param name="payload">Element data.</param>
    /// <param name="sizeLength">Length of the size field; 0 for the minimal length.</param>
    public static byte[] Element(ulong id, ReadOnlySpan<byte> payload, int sizeLength = 0)
    {
        var idLen = EbmlVarInt.IdLength(id);
        if (sizeLength == 0)
            sizeLength = EbmlVarInt.SizeLength((ulong)payload.Length);
        var result = new byte[idLen + sizeLength + payload.Length];
        EbmlVarInt.WriteId(result, id);
        EbmlVarInt.WriteSize(result.AsSpan(idLen), (ulong)payload.Length, sizeLength);
        payload.CopyTo(result.AsSpan(idLen + sizeLength));
        return result;
    }

    /// <summary>
    /// Builds the header of a Void element whose total length (header + data) is exactly <paramref name="totalLength"/>.
    /// </summary>
    /// <param name="totalLength">Total size of the Void element; must be at least 2.</param>
    /// <returns>The header bytes; the caller writes <c>totalLength - header.Length</c> bytes of filler after it.</returns>
    public static byte[] VoidHeader(long totalLength)
    {
        if (totalLength < 2)
            throw new ArgumentOutOfRangeException(nameof(totalLength), totalLength, "A Void element needs at least 2 bytes.");

        for (var sizeLen = 1; sizeLen <= EbmlVarInt.MaxSizeLength; sizeLen++)
        {
            var dataLen = totalLength - 1 - sizeLen;
            if (dataLen < 0)
                break;
            if ((ulong)dataLen <= EbmlVarInt.MaxSizeValue(sizeLen))
            {
                var header = new byte[1 + sizeLen];
                header[0] = (byte)MatroskaIds.VoidElement;
                EbmlVarInt.WriteSize(header.AsSpan(1), (ulong)dataLen, sizeLen);
                return header;
            }
        }

        // Only reachable for lengths where every size length leaves an unencodable remainder, which cannot happen
        // for totalLength >= 2 (a 1-byte size covers 0..126, and each extra byte extends the range contiguously).
        throw new ArgumentOutOfRangeException(nameof(totalLength), totalLength, "Cannot encode a Void element of this length.");
    }

    /// <summary>
    /// Prepends a CRC-32 element covering <paramref name="payload"/> (as the first child of a master element).
    /// </summary>
    public static byte[] WithCrc32(ReadOnlySpan<byte> payload)
    {
        var result = new byte[6 + payload.Length];
        result[0] = (byte)MatroskaIds.Crc32Element;
        result[1] = 0x84;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), Crc32.Compute(payload));
        payload.CopyTo(result.AsSpan(6));
        return result;
    }
}
