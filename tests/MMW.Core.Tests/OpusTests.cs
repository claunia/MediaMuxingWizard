using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>Opus TOC bytes (RFC 6716 §3.1): packet durations, modes, bandwidths and the stream summary.</summary>
public sealed class OpusTests
{
    [Theory]
    [InlineData(0, OpusModes.Silk, 4000, 40)] // SILK NB 10 ms
    [InlineData(11, OpusModes.Silk, 8000, 240)] // SILK WB 60 ms
    [InlineData(5, OpusModes.Silk, 6000, 80)] // SILK MB 20 ms
    [InlineData(13, OpusModes.Hybrid, 12000, 80)] // Hybrid SWB 20 ms
    [InlineData(14, OpusModes.Hybrid, 20000, 40)] // Hybrid FB 10 ms
    [InlineData(16, OpusModes.Celt, 4000, 10)] // CELT NB 2.5 ms
    [InlineData(31, OpusModes.Celt, 20000, 80)] // CELT FB 20 ms
    public void Reads_the_toc(int config, OpusModes mode, int bandwidth, int quarterMs) =>
        Assert.Equal((mode, bandwidth, quarterMs), Opus.Toc((byte)(config << 3)));

    [Theory]
    [InlineData(new byte[] { 0xFC, 0 }, 960)] // CELT FB 20 ms, one frame
    [InlineData(new byte[] { 0xFD, 0 }, 1920)] // two frames
    [InlineData(new byte[] { 0x0B, 0x03 }, 2880)] // SILK NB 20 ms, code 3: the count byte says three frames
    [InlineData(new byte[] { 0x1B, 0x03 }, 8640)] // SILK NB 60 ms, three frames
    public void Counts_packet_samples(byte[] packet, int samples) => Assert.Equal(samples, Opus.PacketSamples(packet));

    [Fact]
    public void Summarises_a_stream()
    {
        Assert.Equal("CELT, fullband, 20 ms frames", Opus.DescribeStream([new byte[] { 0xFC, 0 }, new byte[] { 0xFC, 1 }]));
        // An encoder switching from speech to music: every mode it used and the range of bandwidths and frame sizes.
        ReadOnlyMemory<byte>[] mixed = [new byte[] { 9 << 3, 0 }, new byte[] { 15 << 3, 0 }, new byte[] { 30 << 3, 0 }, new byte[] { 0xF8 }];
        Assert.Equal("SILK/Hybrid/CELT, wideband to fullband, 10–20 ms frames", Opus.DescribeStream(mixed));
        Assert.Equal(string.Empty, Opus.DescribeStream([]));
    }
}
