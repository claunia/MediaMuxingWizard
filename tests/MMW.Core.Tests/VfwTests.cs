using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Tests;

/// <summary>Video for Windows / ACM / RealMedia descriptions as Matroska stores them in its compatibility modes.</summary>
public sealed class VfwTests
{
    /// <summary>The header mkvmerge writes for WMV2 from AVI (40 bytes, 24 bits, the size of a 24-bit frame, then the codec's extra data).</summary>
    [Fact]
    public void Bitmap_info_header_is_written_as_mkvmerge_does()
    {
        var bih = Vfw.BitmapInfoHeader(1280, 720, "WMV2", [0xE8, 0xC3, 0xB4, 0x80]);
        Assert.Equal("2800000000050000d002000001001800574d5632", Convert.ToHexString(bih.AsSpan(0, 20)).ToLowerInvariant());
        Assert.Equal(1280 * 720 * 3, BitConverter.ToInt32(bih, 20));
        Assert.Equal(44, bih.Length);
        var parsed = Vfw.ParseBitmapInfoHeader(bih);
        Assert.NotNull(parsed);
        Assert.Equal((1280, 720, "WMV2"), (parsed.Value.Width, parsed.Value.Height, parsed.Value.FourCc));
        Assert.Equal([0xE8, 0xC3, 0xB4, 0x80], parsed.Value.Extra);
    }

    [Fact]
    public void Wave_format_ex_round_trips()
    {
        var wfx = Vfw.WaveFormatEx(0x161, 2, 48000, 128000, 5945, 16, [0, 0, 0, 0, 1, 0]);
        Assert.Equal("6101020080bb0000803e0000", Convert.ToHexString(wfx.AsSpan(0, 12)).ToLowerInvariant());
        var parsed = Vfw.ParseWaveFormatEx(wfx);
        Assert.NotNull(parsed);
        Assert.Equal((0x161, 2, 48000, 128000L, 5945, 16), (parsed.Value.Tag, parsed.Value.Channels, parsed.Value.SampleRate, parsed.Value.BitRate, parsed.Value.BlockAlign, parsed.Value.BitsPerSample));
        Assert.Equal(6, parsed.Value.Extra.Length);
    }

    /// <summary>The 'VIDO' data of a RealVideo 1 stream, as the corpus RealMedia file and mkvmerge's CodecPrivate hold it.</summary>
    [Fact]
    public void Real_video_type_data_is_parsed()
    {
        var vido = Convert.FromHexString("000000225649444f5256313002d00240001d00000000001d00000000000810000000");
        Assert.Equal("RV10", Vfw.RealVideoFourCc(vido));
        var parsed = Vfw.ParseRealVideo(vido);
        Assert.NotNull(parsed);
        Assert.Equal((720, 576), (parsed.Value.Width, parsed.Value.Height));
        Assert.Equal(Convert.FromHexString("0000000810000000"), parsed.Value.Extra);
        var rebuilt = Vfw.RealVideo("RV10", 720, 576, 29, parsed.Value.Extra);
        Assert.Equal(vido.AsSpan(0, 16).ToArray(), rebuilt.AsSpan(0, 16).ToArray());
        Assert.Equal(vido.AsSpan(22).ToArray(), rebuilt.AsSpan(22).ToArray());
    }

    [Theory]
    [InlineData("MP43", "MS-MPEG4 v3")]
    [InlineData("WMV2", "WMV 8")]
    [InlineData("WVC1", "VC-1")]
    [InlineData("dvsd", "DV")]
    [InlineData("ABCD", "ABCD")]
    public void Vfw_video_is_named_from_its_fourcc(string fourCc, string name) =>
        Assert.Equal(name, new CodecConfig { Codec = CodecType.VfwVideo, Kind = TrackKind.Video, Extradata = Vfw.BitmapInfoHeader(16, 16, fourCc) }.FormatName);

    [Theory]
    [InlineData(0x161, "WMA 2")]
    [InlineData(0x162, "WMA Pro")]
    [InlineData(0x11, "IMA ADPCM")]
    [InlineData(0x1234, "ACM 0x1234")]
    public void Acm_audio_is_named_from_its_tag(int tag, string name) =>
        Assert.Equal(name, new CodecConfig { Codec = CodecType.AcmAudio, Kind = TrackKind.Audio, Extradata = Vfw.WaveFormatEx(tag, 2, 44100, 0, 0, 16) }.FormatName);
}
