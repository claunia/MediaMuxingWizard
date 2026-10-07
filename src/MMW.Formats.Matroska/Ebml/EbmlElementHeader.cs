using MMW.Formats.Matroska.Resources;

namespace MMW.Formats.Matroska.Ebml;

/// <summary>Location of one EBML element inside a file.</summary>
/// <param name="Id">Element ID (with marker bits).</param>
/// <param name="Position">Absolute offset of the first byte of the ID.</param>
/// <param name="HeaderLength">Length of the ID plus the size field.</param>
/// <param name="Size">Data size, or <see cref="EbmlVarInt.UnknownSize"/>.</param>
/// <param name="SizeLength">Length of the size field in bytes.</param>
internal readonly record struct EbmlElementHeader(ulong Id, long Position, int HeaderLength, ulong Size, int SizeLength)
{
    public bool IsUnknownSize => Size == EbmlVarInt.UnknownSize;

    public long DataPosition => Position + HeaderLength;

    /// <summary>Absolute end offset; only meaningful when the size is known.</summary>
    public long End => IsUnknownSize ? throw new InvalidOperationException(Strings.Error_UnknownSize) : DataPosition + (long)Size;
}
