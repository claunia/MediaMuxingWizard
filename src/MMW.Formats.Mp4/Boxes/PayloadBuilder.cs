using System.Buffers.Binary;
using System.Text;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>Small big-endian byte builder for box payloads.</summary>
public sealed class PayloadBuilder : IDisposable
{
    private readonly MemoryStream _ms = new();

    public PayloadBuilder U8(int v)
    {
        _ms.WriteByte((byte)v);
        return this;
    }

    public PayloadBuilder U16(int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        _ms.Write(b);
        return this;
    }

    public PayloadBuilder U24(int v) => U8(v >> 16).U8(v >> 8).U8(v);

    public PayloadBuilder U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public PayloadBuilder I32(int v) => U32(unchecked((uint)v));

    public PayloadBuilder U64(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        _ms.Write(b);
        return this;
    }

    /// <summary>Version (1 byte) and flags (3 bytes) of a FullBox.</summary>
    public PayloadBuilder FullBox(int version, int flags) => U8(version).U24(flags);

    public PayloadBuilder Bytes(ReadOnlySpan<byte> data)
    {
        _ms.Write(data);
        return this;
    }

    public PayloadBuilder Zeros(int count)
    {
        for (var i = 0; i < count; i++)
            _ms.WriteByte(0);
        return this;
    }

    public PayloadBuilder Type(string fourcc)
    {
        Span<byte> b = stackalloc byte[4];
        BoxWriter.WriteType(fourcc, b);
        _ms.Write(b);
        return this;
    }

    public PayloadBuilder Utf8(string s, bool nullTerminated = false)
    {
        _ms.Write(Encoding.UTF8.GetBytes(s));
        if (nullTerminated)
            _ms.WriteByte(0);
        return this;
    }

    /// <summary>The identity transformation matrix used by tkhd/mvhd.</summary>
    public PayloadBuilder UnityMatrix() =>
        U32(0x00010000).U32(0).U32(0).U32(0).U32(0x00010000).U32(0).U32(0).U32(0).U32(0x40000000);

    public byte[] ToArray() => _ms.ToArray();

    public void Dispose() => _ms.Dispose();
}
