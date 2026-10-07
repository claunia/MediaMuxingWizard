using MMW.Core.Resources;

namespace MMW.Core.Media.Codecs;

/// <summary>MSB-first bit reader over a byte span (with Exp-Golomb support for H.264/HEVC syntax).</summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bit;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bit = 0;
    }

    public readonly long Position => _bit;

    public readonly long BitsLeft => (long)_data.Length * 8 - _bit;

    /// <summary>Reads up to 32 bits.</summary>
    /// <exception cref="InvalidDataException">The data ends first.</exception>
    public uint Read(int count)
    {
        if (count == 0)
            return 0;
        if (count is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (BitsLeft < count)
            throw new InvalidDataException(Strings.Error_UnexpectedEndOfBitstream);
        uint v = 0;
        for (var i = 0; i < count; i++)
        {
            var b = _data[(int)(_bit >> 3)];
            v = (v << 1) | (uint)((b >> (7 - (int)(_bit & 7))) & 1);
            _bit++;
        }

        return v;
    }

    public ulong Read64(int count)
    {
        if (count <= 32)
            return Read(count);
        var high = (ulong)Read(count - 32);
        return (high << 32) | Read(32);
    }

    public bool Flag() => Read(1) != 0;

    public void Skip(long count)
    {
        if (BitsLeft < count)
            throw new InvalidDataException(Strings.Error_UnexpectedEndOfBitstream);
        _bit += count;
    }

    /// <summary>Unsigned Exp-Golomb code ue(v).</summary>
    public uint Ue()
    {
        var zeros = 0;
        while (Read(1) == 0)
        {
            if (++zeros > 31)
                throw new InvalidDataException(Strings.Error_InvalidExpGolomb);
        }

        return zeros == 0 ? 0 : (uint)((1UL << zeros) - 1 + Read(zeros));
    }

    /// <summary>Signed Exp-Golomb code se(v).</summary>
    public int Se()
    {
        var k = Ue();
        return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
    }

    public void ByteAlign() => _bit = (_bit + 7) & ~7L;
}

/// <summary>MSB-first bit writer.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitCount;

    public void Write(ulong value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            if ((_bitCount & 7) == 0)
                _bytes.Add(0);
            if (((value >> i) & 1) != 0)
                _bytes[^1] |= (byte)(0x80 >> (_bitCount & 7));
            _bitCount++;
        }
    }

    public void Flag(bool value) => Write(value ? 1UL : 0UL, 1);

    public byte[] ToArray() => [.. _bytes];
}
