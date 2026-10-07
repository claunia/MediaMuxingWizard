using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>LOAS / LATM AAC (MPEG-TS stream type 0x11).</summary>
public sealed class LatmTests
{
    /// <summary>A LOAS frame around an AudioMuxElement written bit by bit (header length filled in).</summary>
    private static byte[] Loas(Action<BitWriter> element)
    {
        var w = new BitWriter();
        element(w);
        var payload = w.ToArray();
        return [0x56, (byte)(0xE0 | (payload.Length >> 8)), (byte)payload.Length, .. payload];
    }

    private static void Payload(BitWriter w, byte[] unit)
    {
        w.Write((ulong)unit.Length, 8); // PayloadLengthInfo (under 255 bytes)
        foreach (var b in unit)
            w.Write(b, 8);
    }

    [Fact]
    public void Extracts_the_configuration_and_unaligned_access_units()
    {
        byte[] first = [0x21, 0x10, 0x05], second = [0xAB, 0xCD];
        var withConfig = Loas(w =>
        {
            w.Write(0, 1); // useSameStreamMux
            w.Write(0, 1); // audioMuxVersion
            w.Write(1, 1); // allStreamsSameTimeFraming
            w.Write(0, 6); // numSubFrames
            w.Write(0, 4); // numProgram
            w.Write(0, 3); // numLayer
            w.Write(2, 5); // AudioSpecificConfig: AAC LC
            w.Write(3, 4); // 48 kHz
            w.Write(2, 4); // stereo
            w.Write(0, 3); // GASpecificConfig
            w.Write(0, 3); // frameLengthType
            w.Write(0xFF, 8); // latmBufferFullness
            w.Write(0, 1); // otherDataPresent
            w.Write(0, 1); // crcCheckPresent
            Payload(w, first);
        });
        var reusing = Loas(w =>
        {
            w.Write(1, 1); // useSameStreamMux
            Payload(w, second);
        });

        Assert.Equal(withConfig.Length, LatmParser.FrameLength(withConfig));
        var parser = new LatmParser();
        Assert.Empty(parser.Parse(reusing)); // no configuration yet
        Assert.Equal([first], parser.Parse(withConfig));
        Assert.Equal([0x11, 0x90], parser.AudioSpecificConfig);
        Assert.Equal((2, 48000, 2), (parser.Config!.Value.ObjectType, parser.Config.Value.SampleRate, parser.Config.Value.Channels));
        Assert.Equal([second], parser.Parse(reusing));
        Assert.Equal(0, LatmParser.FrameLength([0xFF, 0xF1, 0x50]));
    }
}
