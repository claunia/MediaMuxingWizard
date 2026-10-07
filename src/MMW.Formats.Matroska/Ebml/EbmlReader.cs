using System.Globalization;
using Microsoft.Win32.SafeHandles;
using MMW.Formats.Matroska.Resources;

namespace MMW.Formats.Matroska.Ebml;

/// <summary>
/// Positional (stateless) reader of EBML elements in a file. Uses <see cref="RandomAccess"/> so skipping large
/// elements such as Clusters never reads their content.
/// </summary>
internal sealed class EbmlReader
{
    private readonly SafeFileHandle _handle;

    public EbmlReader(SafeFileHandle handle)
    {
        _handle = handle;
        Length = RandomAccess.GetLength(handle);
    }

    public long Length { get; }

    /// <summary>Reads the header of the element at <paramref name="position"/>.</summary>
    /// <param name="position">Absolute offset of the element.</param>
    /// <param name="limit">Offset the header must not cross (end of the parent).</param>
    /// <param name="header">The decoded header.</param>
    /// <returns>False when the bytes do not form a valid header or the header is truncated.</returns>
    public bool TryReadHeader(long position, long limit, out EbmlElementHeader header)
    {
        header = default;
        Span<byte> buf = stackalloc byte[EbmlVarInt.MaxIdLength + EbmlVarInt.MaxSizeLength];
        var available = (int)Math.Min(buf.Length, Math.Min(limit, Length) - position);
        if (available < 2)
            return false;

        var read = RandomAccess.Read(_handle, buf[..available], position);
        var span = buf[..read];
        if (!EbmlVarInt.TryReadId(span, out var id, out var idLen))
            return false;
        if (!EbmlVarInt.TryReadSize(span[idLen..], out var size, out var sizeLen))
            return false;

        header = new EbmlElementHeader(id, position, idLen + sizeLen, size, sizeLen);
        return true;
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes at <paramref name="position"/>.</summary>
    /// <exception cref="EndOfStreamException">The file ends before the requested range.</exception>
    public void ReadExactly(long position, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = RandomAccess.Read(_handle, buffer[total..], position + total);
            if (n == 0)
                throw new EndOfStreamException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnexpectedEndAt, position + total));
            total += n;
        }
    }

    /// <summary>Reads a byte range into a new array.</summary>
    public byte[] ReadBytes(long position, long count)
    {
        if (count < 0 || count > Array.MaxLength)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ElementTooLargeToLoad, count, position));
        var data = new byte[count];
        ReadExactly(position, data);
        return data;
    }

    /// <summary>Reads the data of a known-size element.</summary>
    public byte[] ReadData(in EbmlElementHeader header)
    {
        if (header.IsUnknownSize)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ElementUnknownSize, header.Id, header.Position));
        if (header.End > Length)
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ElementPastEndOfFile, header.Id, header.Position));
        return ReadBytes(header.DataPosition, (long)header.Size);
    }

    /// <summary>Enumerates the direct children of a known-size master element without loading them.</summary>
    public IEnumerable<EbmlElementHeader> Children(EbmlElementHeader parent)
    {
        var end = Math.Min(parent.End, Length);
        var pos = parent.DataPosition;
        while (pos < end)
        {
            if (!TryReadHeader(pos, end, out var child) || child.IsUnknownSize || child.End > end)
                yield break;
            yield return child;
            pos = child.End;
        }
    }
}
