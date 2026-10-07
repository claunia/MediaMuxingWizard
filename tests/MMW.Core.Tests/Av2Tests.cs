using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>AV2 OBUs, sequence headers, content interpretation, metadata, display order and storage records (AVM v1.0.0 output).</summary>
public sealed class Av2Tests
{
    /// <summary>avmenc v1.0.0: 176×144, 8-bit 4:2:0, Main_420_10_IP0, level 2.0, non-monotonic output order.</summary>
    private static readonly byte[] Sequence8 = Convert.FromHexString("04800a001debe3c1e6000000e6ee6809abf3bbf526e65684");

    /// <summary>The same at 10 bits, with its content interpretation OBU (BT.2100 PQ preset, limited range).</summary>
    private static readonly byte[] Sequence10 = Convert.FromHexString("04800c0077af8f07980000039bb9a026afceefd49b995a10");

    private static readonly byte[] Interpretation = Convert.FromHexString("602044");

    /// <summary>
    /// The 17 temporal units of that 8-bit stream (key frame interval 8), without the sequence header and with frame
    /// OBUs cut after their header's first bytes: temporal delimiter, then the coded frames.
    /// </summary>
    private const string Units =
        "01080910e005c600410500c7091cf800c84c88040081|0108091cf41c9d41f43e954a|0108091cf40caf3448742180|0108091cf8097e68f0182244|" +
        "0108091cf8119e68f0c83024|0108091cf4165be68c018127|0108091cf821de69002d1e00|0108091cf831fe6900180ac7|" +
        "01080910e005c20045349050091cf800c41d80518504|0108091cf41c9d4158051850|0108091cf40caf1970051b1e|0108091cf8097e33380a30a3|" +
        "0108091cf8119e334001e07e|0108091cf4169be32e00a363|0108091cf824bbc6640146c7|0108091cf831fe33100a3638|01080910f00b8400860a8200";

    private static byte[] Delimited(params byte[][] obus)
    {
        var o = new List<byte>();
        foreach (var obu in obus)
        {
            Av2.WriteLeb128(o, obu.Length);
            o.AddRange(obu);
        }

        return [.. o];
    }

    [Fact]
    public void Parses_the_sequence_header()
    {
        var s = Assert.IsType<Av2SequenceHeader>(Av2.ParseSequenceHeader(Sequence8.AsSpan(1)));
        Assert.Equal((0, 0, 0, 0, 8), (s.Profile, s.Level, s.Tier, s.ChromaFormat, s.BitDepth));
        Assert.Equal((176, 144), (s.Width, s.Height));
        Assert.False(s.MonotonicOutputOrder);
        Assert.True(s.OrderHintBits > 0);
        Assert.Equal("Main_420_10_IP0@L2.0", Av2.ProfileLevel(s));
        Assert.Equal(10, Av2.ParseSequenceHeader(Sequence10.AsSpan(1))!.BitDepth);
    }

    [Theory]
    [InlineData(0, 4, 0, "Main_420_10_IP0@L4.0")]
    [InlineData(3, 9, 1, "Main_422_10_IP1@L5.3 High tier")]
    [InlineData(4, 21, 0, "Main_444_10_IP1@L8.3")]
    [InlineData(31, 31, 0, "Configurable@max")]
    public void Names_profiles_and_levels(int profile, int level, int tier, string expected) =>
        Assert.Equal(expected, Av2.ProfileLevel(new Av2SequenceHeader { Profile = profile, Level = level, Tier = tier }));

    [Fact]
    public void Reads_the_content_interpretation_colour()
    {
        Assert.Equal(new Av2ObuHeader(Av2.ObuContentInterpretation, 0, 0, 0, 1), Av2.ParseObuHeader(Interpretation));
        var ci = Assert.IsType<Av2ContentInterpretation>(Av2.ParseContentInterpretation(Interpretation.AsSpan(1)));
        Assert.Equal(new ColorInfo(9, 16, 9, false), ci.Color); // preset 2: BT.2100 PQ

        // Explicit code points: rg(2) = 0, then primaries, transfer and matrix; full range.
        var w = new BitWriter();
        w.Write(0, 2); // scan type
        w.Write(1, 1); // colour description present
        w.Write(0, 4); // chroma position, aspect ratio, timing, …
        w.Write(0, 1);
        w.Write(0, 1); // rg(2) quotient 0
        w.Write(0, 2); // … remainder 0: explicit
        w.Write(9, 8);
        w.Write(18, 8);
        w.Write(9, 8);
        w.Write(1, 1);
        w.Write(0, 1); // obu_extension_flag
        w.Write(1, 1); // trailing bits
        Assert.Equal(new ColorInfo(9, 18, 9, true), Av2.ParseContentInterpretation(w.ToArray())!.Color);
    }

    [Fact]
    public void Walks_short_and_group_metadata()
    {
        byte[] cll = [0x03, 0xE8, 0x01, 0x90];
        // Short: suffix/layer/cancel/persistence byte, leb128 type, payload, trailing bits.
        byte[] shortPayload = [0x00, Av2.MetadataHdrCll, .. cll, 0x80];
        var found = new List<(int, byte[])>();
        Av2.ForEachMetadata(Av2.ObuMetadataShort, shortPayload, (t, p) =>
        {
            found.Add((t, p.ToArray()));
            return false;
        });
        Assert.Equal(Av2.MetadataHdrCll, Assert.Single(found).Item1);
        Assert.Equal(cll, found[0].Item2);

        // Group: two units, the first cancelled; unit header = size(7)|cancel(1), leb128 payload size, 2 bytes of fields.
        byte[] groupPayload = [0x00, 0x01, Av2.MetadataHdrMdcv, 0x01, Av2.MetadataHdrCll, 3 << 1, 4, 0x08, 0x00, .. cll, 0x80];
        found.Clear();
        Av2.ForEachMetadata(Av2.ObuMetadataGroup, groupPayload, (t, p) =>
        {
            found.Add((t, p.ToArray()));
            return false;
        });
        Assert.Equal(Av2.MetadataHdrCll, Assert.Single(found).Item1);
        Assert.Equal(cll, found[0].Item2);
    }

    [Fact]
    public void Finds_t35_messages()
    {
        byte[] t35 = [0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01];
        var sample = Delimited([Av2.ObuMetadataShort << 2, 0x02, Av2.MetadataItutT35, .. t35, 0x80], [Av2.ObuRegularTileGroup << 2, 0x80]);
        Assert.True(Hdr10Plus.InSample(CodecType.Av2, sample, 4));
        Assert.True(Av2.ForEachT35(sample, m => m.AsSpan().SequenceEqual(t35)));
    }

    /// <summary>Hierarchical groups of 8 with a hidden alternate reference: units are displayed out of decoding order.</summary>
    [Fact]
    public void Derives_the_display_order()
    {
        var order = new Av2DisplayOrder();
        order.AddConfiguration([Sequence8]);
        var display = Units.Split('|').Select(u => order.Next(Convert.FromHexString(u))).ToArray();
        Assert.Equal([0L, 7, 3, 1, 2, 5, 4, 6, 8, 15, 11, 9, 10, 13, 12, 14, 16], display);
    }

    [Fact]
    public void Moves_configuration_out_of_samples_and_back()
    {
        var unit = Convert.FromHexString(Units.Split('|')[0]);
        var withHeader = Delimited([.. Av2.Obus(unit).Take(1).Concat([Sequence8, Interpretation]).Concat(Av2.Obus(unit).Skip(1))]);
        var configuration = new List<byte[]>();
        var sample = Av2.ToSample(withHeader, configuration);
        Assert.Equal([Sequence8, Interpretation], configuration);
        Assert.DoesNotContain(Av2.Obus(sample), o => (o[0] >> 2 & 0x1F) is Av2.ObuTemporalDelimiter or Av2.ObuSequenceHeader);
        Assert.True(Av2.IsSync(sample));
        Assert.False(Av2.IsSync(Av2.ToSample(Convert.FromHexString(Units.Split('|')[1]), [])));

        // Back to a temporal unit: delimiter, sequence header, content interpretation, then the frames.
        Assert.Equal(withHeader, Av2.ToTemporalUnit(sample, configuration, withConfiguration: true));
        Assert.Equal(unit, Av2.ToTemporalUnit(sample, configuration, withConfiguration: false));

        var av2C = Av2.BuildConfigurationBox(configuration);
        Assert.Equal([0, 1], av2C[..2]);
        Assert.Equal(configuration, Av2.ConfigurationObus(av2C));
        var (sequence, interpretation) = Av2.Describe(av2C);
        Assert.Equal((176, 144), (sequence!.Width, sequence.Height));
        Assert.Equal(16, interpretation!.Color.Transfer);
    }

    /// <summary>The Matroska CodecPrivate the reference encoder writes for that stream (V_AV2).</summary>
    [Fact]
    public void Builds_the_reference_encoders_matroska_record() =>
        Assert.Equal(Convert.FromHexString("81000b00"), Av2.BuildMatroskaRecord(Av2.ParseSequenceHeader(Sequence8.AsSpan(1))!, null));

    [Fact]
    public void Splits_packets_into_temporal_units()
    {
        var packet = Convert.FromHexString(string.Concat(Units.Split('|').Skip(1).Take(3)));
        Assert.Equal(3, Av2.SplitTemporalUnits(packet).Count);
    }
}
