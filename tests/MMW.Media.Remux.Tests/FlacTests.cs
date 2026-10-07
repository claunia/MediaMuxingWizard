using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>FLAC: passthrough at any rate and depth, and the lossless conversions to LPCM (MP4 and Matroska) and ALAC (MP4).</summary>
public sealed class FlacTests
{
    /// <summary>Three seconds of a tone in Matroska FLAC.</summary>
    private static string Source(string layout, int rate, int bits)
    {
        MediaProbe.RequireFfmpeg();
        var format = bits switch
        {
            16 => "-sample_fmt s16",
            24 => "-sample_fmt s32",
            _ => "-sample_fmt s32 -bits_per_raw_sample 32 -strict experimental",
        };
        return Fixtures.Get($"flac-{layout}-{rate}-{bits}.mkv", "ffmpeg",
            $"-v error -y -f lavfi -i sine=f=440:d=3:sample_rate={rate} -af aformat=channel_layouts={layout} -c:a flac {format} {{out}}");
    }

    /// <summary>MD5 of the first audio track decoded by FFmpeg as 32-bit integers (exact for every depth).</summary>
    private static string Pcm(string path)
    {
        var pcm = MediaProbe.TempPath(".pcm");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -f s32le {Fixtures.Quote(pcm)}");
            using var stream = File.OpenRead(pcm);
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        }
        finally
        {
            MediaProbe.Delete(pcm);
        }
    }

    private static string Probe(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams a -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 {Fixtures.Quote(path)}").Trim();

    private static async Task<IReadOnlyList<ImportChoice>> ChoicesAsync(string source, ContainerKind target) =>
        Assert.Single(await TrackImporter.InspectAsync(source, target, Ct)).Choices;

    private static async Task<string> ImportAsync(string source, ContainerKind target, ImportAction action)
    {
        var track = Assert.Single(await TrackImporter.InspectAsync(source, target, Ct));
        track.Choice = track.Choices.First(c => c.Action == action);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, [track]);
        var output = MediaProbe.TempPath(target == ContainerKind.Mp4 ? ".mp4" : ".mkv");
        await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
        return output;
    }

    [Fact]
    public async Task Offers_lossless_conversions()
    {
        MediaRemux.EnsureRegistered();
        if (MediaFormatRegistry.AvailableAudioConverter is null)
            Assert.Skip("FFmpeg is not available.");
        var mp4 = await ChoicesAsync(Source("5.1", 96000, 24), ContainerKind.Mp4);
        Assert.Contains(mp4, c => c.Action == ImportAction.Passthrough);
        Assert.Contains(mp4, c => c.Action == ImportAction.ConvertToPcm && c.DisplayName == "LPCM");
        Assert.Contains(mp4, c => c.Action == ImportAction.ConvertToAlac && c.DisplayName == "ALAC");

        var mkv = await ChoicesAsync(Source("stereo", 48000, 16), ContainerKind.Matroska);
        Assert.Contains(mkv, c => c.Action == ImportAction.ConvertToPcm);
        Assert.DoesNotContain(mkv, c => c.Action == ImportAction.ConvertToAlac); // ALAC is offered for MP4 only

        // ALAC cannot hold 32-bit samples or FLAC's 7.1 layout (its 8-channel layout has front-centre pairs).
        Assert.DoesNotContain(await ChoicesAsync(Source("stereo", 48000, 32), ContainerKind.Mp4), c => c.Action == ImportAction.ConvertToAlac);
        Assert.DoesNotContain(await ChoicesAsync(Source("7.1", 48000, 16), ContainerKind.Mp4), c => c.Action == ImportAction.ConvertToAlac);
    }

    [Theory]
    [InlineData("stereo", 44100, 16, ContainerKind.Mp4, ImportAction.ConvertToAlac, "alac")]
    [InlineData("5.1", 96000, 24, ContainerKind.Mp4, ImportAction.ConvertToAlac, "alac")]
    [InlineData("mono", 192000, 24, ContainerKind.Mp4, ImportAction.ConvertToAlac, "alac")]
    [InlineData("stereo", 44100, 16, ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s16le")]
    [InlineData("5.1", 96000, 24, ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s24le")]
    [InlineData("stereo", 192000, 24, ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s24le")]
    [InlineData("7.1", 48000, 16, ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s16le")]
    [InlineData("stereo", 48000, 32, ContainerKind.Mp4, ImportAction.ConvertToPcm, "pcm_s32le")]
    [InlineData("5.1", 96000, 24, ContainerKind.Matroska, ImportAction.ConvertToPcm, "pcm_s24le")]
    [InlineData("stereo", 48000, 32, ContainerKind.Matroska, ImportAction.ConvertToPcm, "pcm_s32le")]
    public async Task Converts_without_loss(string layout, int rate, int bits, ContainerKind target, ImportAction action, string codec)
    {
        MediaRemux.EnsureRegistered();
        if (MediaFormatRegistry.AvailableAudioConverter is null)
            Assert.Skip("FFmpeg is not available.");
        var source = Source(layout, rate, bits);
        var output = await ImportAsync(source, target, action);
        string? back = null;
        try
        {
            Assert.Equal(Pcm(source), Pcm(output));
            var channels = layout switch { "mono" => 1, "stereo" => 2, "5.1" => 6, _ => 8 };
            Assert.Equal($"{codec},{rate},{channels}", Probe(output));

            // Read back: the rate above 65535 Hz ('srat') and the PCM frames survive another remux.
            back = await ImportAsync(output, ContainerKind.Matroska, ImportAction.Passthrough);
            Assert.Equal(Pcm(source), Pcm(back));
            Assert.StartsWith($"{codec},{rate},", Probe(back), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(output, back ?? string.Empty);
        }
    }

    /// <summary>High rates and depths pass through MP4 and back unchanged.</summary>
    [Theory]
    [InlineData("stereo", 192000, 24)]
    [InlineData("stereo", 352800, 24)]
    [InlineData("stereo", 48000, 32)]
    [InlineData("7.1", 48000, 16)]
    public async Task Passes_through_mp4(string layout, int rate, int bits)
    {
        MediaRemux.EnsureRegistered();
        var source = Source(layout, rate, bits);
        var output = await ImportAsync(source, ContainerKind.Mp4, ImportAction.Passthrough);
        try
        {
            Assert.Equal(Pcm(source), Pcm(output));
            Assert.StartsWith($"flac,{rate},", Probe(output), StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>FFmpeg's PCM in MP4 above 65535 Hz: version 1 'ipcm' with the rate in 'srat'.</summary>
    [Fact]
    public async Task Reads_the_rate_of_high_rate_pcm()
    {
        MediaRemux.EnsureRegistered();
        var pcm = Fixtures.Get("pcm-192k.mp4", "ffmpeg", $"-v error -y -i {Fixtures.Quote(Source("stereo", 192000, 24))} -c:a pcm_s24le {{out}}");
        var track = Assert.Single(await TrackImporter.InspectAsync(pcm, ContainerKind.Matroska, Ct));
        Assert.Equal((CodecType.Pcm, 192000, 24), (track.Config.Codec, track.Config.SampleRate, track.Config.BitsPerSample));
    }
}
