using MMW.Core.Media;
using MMW.Media.Conversion.Interop;
using static MMW.Media.Conversion.Tests.ConversionFixtures;

namespace MMW.Media.Conversion.Tests;

/// <summary>Native library discovery and registration.</summary>
public sealed class LoaderTests
{
    [Fact]
    public void Loader_reports_the_version_and_expected_majors()
    {
        Assert.Equal(62, FFmpegLoader.ExpectedVersions["avcodec"]);
        Assert.Equal(62, FFmpegLoader.ExpectedVersions["avformat"]);
        Assert.Equal(60, FFmpegLoader.ExpectedVersions["avutil"]);
        Assert.Equal(6, FFmpegLoader.ExpectedVersions["swresample"]);
        Assert.Equal(9, FFmpegLoader.ExpectedVersions["swscale"]);
        Assert.NotEmpty(FFmpegLoader.SearchDirectories());
        if (!FFmpegLoader.IsAvailable)
        {
            Assert.NotNull(FFmpegLoader.Error);
            Assert.Skip($"FFmpeg libraries not available: {FFmpegLoader.Error}");
        }

        Assert.Null(FFmpegLoader.Error);
        Assert.StartsWith("8.", FFmpegLoader.Version, StringComparison.Ordinal);
        Assert.NotNull(FFmpegLoader.LibraryDirectory);
        TestContext.Current.SendDiagnosticMessage($"FFmpeg {FFmpegLoader.Version} from '{FFmpegLoader.LibraryDirectory}'.");
    }

    [Fact]
    public void Converter_is_registered_and_reports_codecs_it_decodes()
    {
        _ = Ct; // registers the formats and the converter
        var converter = MediaFormatRegistry.AudioConverter;
        Assert.IsType<FFmpegAudioConverterFactory>(converter);
        if (!converter.IsAvailable)
            Assert.Skip($"FFmpeg libraries not available: {converter.UnavailableReason}");
        Assert.StartsWith("FFmpeg ", converter.Name, StringComparison.Ordinal);
        foreach (var codec in new[] { CodecType.Aac, CodecType.Ac3, CodecType.Eac3, CodecType.Dts, CodecType.TrueHd, CodecType.Vorbis, CodecType.Flac, CodecType.Opus, CodecType.Mp2 })
            Assert.True(converter.CanDecode(new CodecConfig { Codec = codec, Kind = Core.Model.TrackKind.Audio }), codec.ToString());
        Assert.True(converter.CanDecode(new CodecConfig { Codec = CodecType.Pcm, Kind = Core.Model.TrackKind.Audio, BitsPerSample = 24 }));
        Assert.False(converter.CanDecode(new CodecConfig { Codec = CodecType.H264, Kind = Core.Model.TrackKind.Video }));
        Assert.False(converter.CanDecode(new CodecConfig { Codec = CodecType.Pcm, Kind = Core.Model.TrackKind.Audio, BitsPerSample = 12 }));
    }
}
