using MMW.Formats.Matroska.Ebml;

namespace MMW.Formats.Matroska.Tests;

public sealed class EbmlTests
{
    [Theory]
    [InlineData(0UL, 1, new byte[] { 0x80 })]
    [InlineData(1UL, 1, new byte[] { 0x81 })]
    [InlineData(126UL, 1, new byte[] { 0xFE })]
    [InlineData(127UL, 2, new byte[] { 0x40, 0x7F })]
    [InlineData(16382UL, 2, new byte[] { 0x7F, 0xFE })]
    [InlineData(16383UL, 3, new byte[] { 0x20, 0x3F, 0xFF })]
    [InlineData(0x1234567UL, 4, new byte[] { 0x11, 0x23, 0x45, 0x67 })]
    public void Size_RoundTrips_WithMinimalLength(ulong value, int length, byte[] expected)
    {
        Assert.Equal(length, EbmlVarInt.SizeLength(value));
        var encoded = EbmlVarInt.EncodeSize(value);
        Assert.Equal(expected, encoded);
        Assert.True(EbmlVarInt.TryReadSize(encoded, out var decoded, out var len));
        Assert.Equal(value, decoded);
        Assert.Equal(length, len);
    }

    [Theory]
    [InlineData(5UL, 8)]
    [InlineData(0UL, 2)]
    [InlineData(300UL, 4)]
    public void Size_CanUseLongerEncodings(ulong value, int length)
    {
        var encoded = EbmlVarInt.EncodeSize(value, length);
        Assert.Equal(length, encoded.Length);
        Assert.True(EbmlVarInt.TryReadSize(encoded, out var decoded, out var len));
        Assert.Equal(value, decoded);
        Assert.Equal(length, len);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x7F, 0xFF })]
    [InlineData(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void Size_AllOnes_IsUnknown(byte[] data)
    {
        Assert.True(EbmlVarInt.TryReadSize(data, out var value, out var len));
        Assert.Equal(EbmlVarInt.UnknownSize, value);
        Assert.Equal(data.Length, len);

        var written = new byte[data.Length];
        EbmlVarInt.WriteUnknownSize(written, data.Length);
        Assert.Equal(data, written);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x40 })]
    [InlineData(new byte[0])]
    public void Size_InvalidOrTruncated_IsRejected(byte[] data) =>
        Assert.False(EbmlVarInt.TryReadSize(data, out _, out _));

    [Fact]
    public void Size_TooLargeForLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EbmlVarInt.EncodeSize(127, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EbmlVarInt.EncodeSize(1, 9));
    }

    [Theory]
    [InlineData(0xECUL, 1)]
    [InlineData(0x4286UL, 2)]
    [InlineData(0x2AD7B1UL, 3)]
    [InlineData(0x18538067UL, 4)]
    public void Id_RoundTrips(ulong id, int length)
    {
        var encoded = EbmlVarInt.EncodeId(id);
        Assert.Equal(length, encoded.Length);
        Assert.True(EbmlVarInt.TryReadId(encoded, out var decoded, out var len));
        Assert.Equal(id, decoded);
        Assert.Equal(length, len);
    }

    [Fact]
    public void Id_ReservedAllOnes_IsRejected() => Assert.False(EbmlVarInt.TryReadId([0xFF], out _, out _));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(16385)]
    [InlineData(16386)]
    [InlineData(1_000_000)]
    public void VoidHeader_CoversExactLength(int total)
    {
        var header = EbmlWriter.VoidHeader(total);
        Assert.Equal(0xEC, header[0]);
        Assert.True(EbmlVarInt.TryReadSize(header.AsSpan(1), out var size, out var len));
        Assert.Equal(total, 1 + len + (long)size);
    }

    [Fact]
    public void VoidHeader_RejectsOneByte() => Assert.Throws<ArgumentOutOfRangeException>(() => EbmlWriter.VoidHeader(1));

    [Fact]
    public void Writer_EncodesValueTypes_AndParserDecodesThem()
    {
        var w = new EbmlWriter();
        w.UInt(0xD7, 0);
        w.UInt(0x73C5, 0x0123456789ABCDEF);
        w.Int(0xFB, -2);
        w.Int(0xFB, 300);
        w.Float(0x4489, 1234.5);
        w.String(0x536E, "Ünïcödé");
        var children = EbmlParser.Children(w.ToArray());

        Assert.Equal(6, children.Count);
        Assert.Equal(0UL, children[0].UInt);
        Assert.Single(children[0].Data.ToArray());
        Assert.Equal(0x0123456789ABCDEFUL, children[1].UInt);
        Assert.Equal(-2L, children[2].Int);
        Assert.Single(children[2].Data.ToArray());
        Assert.Equal(300L, children[3].Int);
        Assert.Equal(1234.5, children[4].Float);
        Assert.Equal("Ünïcödé", children[5].String);
    }

    [Fact]
    public void Element_WithLongerSizeField_ParsesTheSame()
    {
        var payload = new byte[] { 1, 2, 3 };
        var shortForm = EbmlWriter.Element(0x7BA9, payload);
        var longForm = EbmlWriter.Element(0x7BA9, payload, 2);
        Assert.Equal(shortForm.Length + 1, longForm.Length);
        var child = Assert.Single(EbmlParser.Children(longForm));
        Assert.Equal(payload, child.Data.ToArray());
    }

    [Fact]
    public void Crc32_MatchesKnownValue()
    {
        // CRC-32 (IEEE) of "123456789" is 0xCBF43926.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        var withCrc = EbmlWriter.WithCrc32("123456789"u8);
        Assert.Equal(new byte[] { 0xBF, 0x84, 0x26, 0x39, 0xF4, 0xCB }, withCrc[..6]);
    }

    [Fact]
    public void Parser_StopsAtTruncatedChild()
    {
        var w = new EbmlWriter();
        w.UInt(0xD7, 1);
        var bytes = w.ToArray().Concat(new byte[] { 0x86, 0x85, 0x41 }).ToArray();
        Assert.Single(EbmlParser.Children(bytes));
    }
}
