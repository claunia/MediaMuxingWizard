using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>LPCM: lossless decoding of ALAC, TrueHD, MLP and DTS-HD MA, and PCM imported from Matroska, MOV and MP4.</summary>
public sealed class LosslessTests
{
    private static string Generate(string name, string codec, string layout = "5.1") =>
        Fixtures.Get(name, "ffmpeg", $"-v error -y -f lavfi -i sine=f=440:d=2:sample_rate=48000 -af aformat=channel_layouts={layout} {codec} {{out}}");

    /// <summary>MD5 of the first audio track decoded by FFmpeg as 64-bit floats (exact for integers and floats).</summary>
    private static string Pcm(string path)
    {
        var pcm = MediaProbe.TempPath(".pcm");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -f f64le {Fixtures.Quote(pcm)}");
            using var stream = File.OpenRead(pcm);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        }
        finally
        {
            MediaProbe.Delete(pcm);
        }
    }

    private static string Codec(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams a -show_entries stream=codec_name,channels -of csv=p=0 {Fixtures.Quote(path)}").Trim();

    private static async Task<(ImportableTrack Track, string Output)> ImportAsync(string source, ContainerKind target, ImportAction? action = null)
    {
        var track = Assert.Single(await TrackImporter.InspectAsync(source, target, Ct), t => t.Config.Kind == TrackKind.Audio);
        if (action is { } a)
            track.Choice = track.Choices.First(c => c.Action == a);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, [track]);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return (track, output);
    }

    private static void RequireConverter()
    {
        MediaProbe.RequireFfmpeg();
        MediaRemux.EnsureRegistered();
        if (MediaFormatRegistry.AvailableAudioConverter is null)
            Assert.Skip("FFmpeg is not available.");
    }

    [Theory]
    [InlineData("alac-5.1.m4a", "-c:a alac -sample_fmt s32p", "pcm_s24le,6")]
    [InlineData("truehd-5.1.mkv", "-c:a truehd -strict -2", "pcm_s24le,6")]
    [InlineData("mlp-stereo.mkv", "-ac 2 -c:a mlp -strict -2", "pcm_s16le,2")]
    public async Task Lossless_codecs_decode_to_the_same_pcm(string name, string codec, string expected)
    {
        RequireConverter();
        var source = Generate(name, codec);
        foreach (var target in new[] { ContainerKind.Matroska, ContainerKind.Mp4 })
        {
            var (_, output) = await ImportAsync(source, target, ImportAction.ConvertToPcm);
            try
            {
                Assert.Equal(Pcm(source), Pcm(output));
                Assert.Equal(expected, Codec(output));
            }
            finally
            {
                MediaProbe.Delete(output);
            }
        }
    }

    /// <summary>DTS-HD Master Audio is lossless and offered LPCM; DTS-HD High Resolution is lossy and is not.</summary>
    [Fact]
    public async Task Dts_master_audio_decodes_to_the_same_pcm()
    {
        RequireConverter();
        var dir = Corpus.Directory is { } d ? Path.Combine(d, "Multichannel audio") : string.Empty;
        var ma = Path.Combine(dir, "{DTS-HD MA 7.1 96KHz - Matroska} DTS Logo (Orchestra).mkv");
        var hra = Path.Combine(dir, "{DTS-HD HRA 7.1 - Matroska} DTS Logo (Orchestra).mkv");
        Corpus.Require(File.Exists(ma) ? ma : string.Empty);
        var (_, output) = await ImportAsync(ma, ContainerKind.Mp4, ImportAction.ConvertToPcm);
        try
        {
            Assert.Equal(Pcm(ma), Pcm(output));
            Assert.Equal("pcm_s24le,8", Codec(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }

        if (File.Exists(hra))
        {
            var track = Assert.Single(await TrackImporter.InspectAsync(hra, ContainerKind.Mp4, Ct), t => t.Config.Kind == TrackKind.Audio);
            Assert.DoesNotContain(track.Choices, c => c.Action == ImportAction.ConvertToPcm);
        }
    }

    /// <summary>
    /// PCM imports unchanged where the container can store it (MP4 'ipcm', QuickTime entries rewritten as it), and is
    /// re-encoded as PCM, never compressed, where it cannot: 8-bit PCM into MP4, big-endian floats into Matroska.
    /// </summary>
    [Theory]
    [InlineData("pcm-s24le.mkv", "-c:a pcm_s24le", ContainerKind.Mp4, ImportAction.Passthrough, "pcm_s24le")]
    [InlineData("pcm-s16be.mkv", "-c:a pcm_s16be", ContainerKind.Mp4, ImportAction.Passthrough, "pcm_s16be")]
    [InlineData("pcm-f64le.mkv", "-c:a pcm_f64le", ContainerKind.Mp4, ImportAction.Passthrough, "pcm_f64le")]
    [InlineData("pcm-s24be.mov", "-c:a pcm_s24be", ContainerKind.Mp4, ImportAction.Passthrough, "pcm_s24be")]
    [InlineData("pcm-s16le.mov", "-c:a pcm_s16le", ContainerKind.Matroska, ImportAction.Passthrough, "pcm_s16le")]
    [InlineData("pcm-u8.mkv", "-c:a pcm_u8", ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s16le")]
    [InlineData("pcm-u8.mov", "-c:a pcm_u8", ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s16le")]
    [InlineData("pcm-f32be.mov", "-c:a pcm_f32be", ContainerKind.Matroska, ImportAction.ConvertToPcm, "pcm_f32le")]
    public async Task Pcm_imports_without_loss(string name, string codec, ContainerKind target, ImportAction expected, string output)
    {
        RequireConverter();
        var source = Generate(name, codec, "stereo");
        var (track, path) = await ImportAsync(source, target);
        try
        {
            Assert.Equal(expected, track.Action);
            Assert.Equal(Pcm(source), Pcm(path));
            Assert.Equal($"{output},2", Codec(path));
            Assert.Equal(2.0, double.Parse(Fixtures.Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 {Fixtures.Quote(path)}"),
                System.Globalization.CultureInfo.InvariantCulture), 3);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }
}
