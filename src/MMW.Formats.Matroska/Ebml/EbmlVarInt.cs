using System.Globalization;
using System.Numerics;
using MMW.Formats.Matroska.Resources;

namespace MMW.Formats.Matroska.Ebml;

/// <summary>Encoding and decoding of EBML variable-length integers (element IDs and data sizes).</summary>
internal static class EbmlVarInt
{
    /// <summary>Sentinel returned for an "unknown" data size (all value bits set).</summary>
    public const ulong UnknownSize = ulong.MaxValue;

    /// <summary>Largest ID length accepted (EBMLMaxIDLength default).</summary>
    public const int MaxIdLength = 4;

    /// <summary>Largest size length accepted (EBMLMaxSizeLength default).</summary>
    public const int MaxSizeLength = 8;

    /// <summary>Number of bytes of a VINT given its first byte; 0 when the byte is 0 (invalid).</summary>
    public static int LengthFromFirstByte(byte first) => first == 0 ? 0 : BitOperations.LeadingZeroCount((uint)first) - 23;

    /// <summary>Largest value that can be stored in a size VINT of <paramref name="length"/> bytes (the all-ones value is reserved).</summary>
    public static ulong MaxSizeValue(int length) => (1UL << (7 * length)) - 2;

    /// <summary>Decodes a data size. Returns false if the bytes are not a valid VINT or are truncated.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.IsEmpty)
            return false;

        var len = LengthFromFirstByte(data[0]);
        if (len == 0 || len > MaxSizeLength || data.Length < len)
            return false;

        var v = (ulong)(data[0] & (0xFF >> len));
        for (var i = 1; i < len; i++)
            v = (v << 8) | data[i];

        length = len;
        value = v == (1UL << (7 * len)) - 1 ? UnknownSize : v;
        return true;
    }

    /// <summary>Decodes an element ID (marker bits kept, as IDs are conventionally written). Returns false when invalid.</summary>
    public static bool TryReadId(ReadOnlySpan<byte> data, out ulong id, out int length)
    {
        id = 0;
        length = 0;
        if (data.IsEmpty)
            return false;

        var len = LengthFromFirstByte(data[0]);
        if (len == 0 || len > MaxIdLength || data.Length < len)
            return false;

        ulong v = 0;
        for (var i = 0; i < len; i++)
            v = (v << 8) | data[i];

        // An ID whose value bits are all ones is reserved.
        var valueBits = v & ((1UL << (7 * len)) - 1);
        if (valueBits == (1UL << (7 * len)) - 1)
            return false;

        id = v;
        length = len;
        return true;
    }

    /// <summary>Smallest number of bytes needed to store <paramref name="value"/> as a data size.</summary>
    public static int SizeLength(ulong value)
    {
        for (var len = 1; len <= MaxSizeLength; len++)
        {
            if (value <= MaxSizeValue(len))
                return len;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, Strings.Error_ValueTooLargeForSize);
    }

    /// <summary>Number of bytes of an encoded element ID.</summary>
    public static int IdLength(ulong id) => id switch
    {
        <= 0xFF => 1,
        <= 0xFFFF => 2,
        <= 0xFFFFFF => 3,
        <= 0xFFFFFFFF => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, Strings.Error_IdTooLong),
    };

    /// <summary>Writes an element ID (with its marker bits) and returns the number of bytes written.</summary>
    public static int WriteId(Span<byte> destination, ulong id)
    {
        var len = IdLength(id);
        for (var i = len - 1; i >= 0; i--)
        {
            destination[i] = (byte)id;
            id >>= 8;
        }

        return len;
    }

    /// <summary>Writes a data size using exactly <paramref name="length"/> bytes (0 = minimal).</summary>
    /// <returns>The number of bytes written.</returns>
    public static int WriteSize(Span<byte> destination, ulong value, int length = 0)
    {
        if (length == 0)
            length = SizeLength(value);
        if (length is < 1 or > MaxSizeLength)
            throw new ArgumentOutOfRangeException(nameof(length), length, Strings.Error_SizeLength);
        if (value > MaxSizeValue(length))
            throw new ArgumentOutOfRangeException(nameof(value), value, string.Format(CultureInfo.CurrentCulture, Strings.Error_ValueDoesNotFitSize, length));

        var v = value;
        for (var i = length - 1; i >= 0; i--)
        {
            destination[i] = (byte)v;
            v >>= 8;
        }

        destination[0] |= (byte)(0x80 >> (length - 1));
        return length;
    }

    /// <summary>Writes the "unknown size" marker (all value bits set) using <paramref name="length"/> bytes.</summary>
    public static int WriteUnknownSize(Span<byte> destination, int length)
    {
        if (length is < 1 or > MaxSizeLength)
            throw new ArgumentOutOfRangeException(nameof(length), length, Strings.Error_SizeLength);
        destination[..length].Fill(0xFF);
        destination[0] = (byte)(0xFF >> (length - 1));
        return length;
    }

    /// <summary>Encodes a data size as a new array.</summary>
    public static byte[] EncodeSize(ulong value, int length = 0)
    {
        var buf = new byte[length == 0 ? SizeLength(value) : length];
        WriteSize(buf, value, buf.Length);
        return buf;
    }

    /// <summary>Encodes an element ID as a new array.</summary>
    public static byte[] EncodeId(ulong id)
    {
        var buf = new byte[IdLength(id)];
        WriteId(buf, id);
        return buf;
    }
}
