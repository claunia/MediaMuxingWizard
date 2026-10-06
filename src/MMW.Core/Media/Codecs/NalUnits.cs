using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>Helpers for H.264/HEVC NAL unit streams (Annex B byte streams and length-prefixed samples).</summary>
public static class NalUnits
{
    /// <summary>
    /// Splits an Annex B buffer into NAL units (start codes removed). Trailing zero bytes belonging to the next start
    /// code are not included.
    /// </summary>
    public static List<Range> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        var result = new List<Range>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                    result.Add(new Range(start, TrimTrailingZeros(data, start, i)));
                i += 3;
                start = i;
                continue;
            }

            i++;
        }

        if (start >= 0 && start < data.Length)
            result.Add(new Range(start, TrimTrailingZeros(data, start, data.Length)));
        return result;
    }

    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>Splits a length-prefixed sample into NAL units.</summary>
    /// <exception cref="InvalidDataException">A length runs past the end of the sample.</exception>
    public static List<Range> SplitLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize)
    {
        var result = new List<Range>();
        var pos = 0;
        while (pos + lengthSize <= data.Length)
        {
            var len = lengthSize switch
            {
                1 => data[pos],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data[pos..]),
                3 => (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2],
                _ => (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]),
            };
            pos += lengthSize;
            if (len < 0 || pos + len > data.Length)
                throw new InvalidDataException("NAL unit length runs past the end of the sample.");
            result.Add(new Range(pos, pos + len));
            pos += len;
        }

        return result;
    }

    /// <summary>Concatenates NAL units with 4-byte big-endian length prefixes.</summary>
    public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> source, IEnumerable<Range> nals)
    {
        var list = nals.ToList();
        var length = source.Length;
        var size = list.Sum(r => r.GetOffsetAndLength(length).Length + 4);
        var result = new byte[size];
        var pos = 0;
        foreach (var r in list)
        {
            var nal = source[r];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), (uint)nal.Length);
            nal.CopyTo(result.AsSpan(pos + 4));
            pos += 4 + nal.Length;
        }

        return result;
    }

    /// <summary>Removes emulation prevention bytes (00 00 03 → 00 00) to obtain the RBSP.</summary>
    public static byte[] ToRbsp(ReadOnlySpan<byte> nal)
    {
        var result = new byte[nal.Length];
        var n = 0;
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            result[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return result[..n];
    }

    /// <summary>H.264 nal_unit_type.</summary>
    public static int H264Type(ReadOnlySpan<byte> nal) => nal.Length > 0 ? nal[0] & 0x1F : -1;

    /// <summary>HEVC nal_unit_type.</summary>
    public static int HevcType(ReadOnlySpan<byte> nal) => nal.Length > 0 ? (nal[0] >> 1) & 0x3F : -1;
}
