using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MMW.Core.Model;
using MMW.Formats.Matroska;
using MMW.Formats.Mp4;
using MMW.Media.Conversion.Interop;
using MMW.Media.Remux;
using MMW.TestSupport;

namespace MMW.Media.Conversion.Tests;

/// <summary>Generated sources and ffprobe checks for the conversion tests.</summary>
internal static class ConversionFixtures
{
    public static readonly Mp4Handler Mp4 = new();
    public static readonly MatroskaHandler Mkv = new();

    static ConversionFixtures() => MediaRemux.EnsureRegistered();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Skips the test when the FFmpeg libraries cannot be loaded (or the ffmpeg tools are missing).</summary>
    public static void RequireFfmpeg()
    {
        MediaProbe.RequireFfmpeg();
        if (!FFmpegLoader.IsAvailable)
            Assert.Skip($"FFmpeg libraries not available: {FFmpegLoader.Error}");
    }

    public static string Ffmpeg(string name, string args)
    {
        RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", "-v error -y " + args + " {out}");
    }

    private const string Pan51 = "pan=5.1|c0=0.5*c0|c1=0.5*c0|c2=0.5*c0|c3=0.2*c0|c4=0.4*c0|c5=0.4*c0";

    /// <summary>4 s stereo FLAC in Matroska (48 kHz).</summary>
    public static string FlacStereoMkv() => Ffmpeg("conv-flac-stereo.mkv", "-f lavfi -i sine=f=440:d=4:sample_rate=48000 -ac 2 -c:a flac");

    /// <summary>4 s 5.1 FLAC in Matroska (48 kHz).</summary>
    public static string Flac51Mkv() => Ffmpeg("conv-flac-51.mkv", $"-f lavfi -i sine=f=440:d=4:sample_rate=48000 -af \"{Pan51}\" -c:a flac");

    /// <summary>3 s stereo Vorbis in Matroska (44.1 kHz).</summary>
    public static string VorbisMkv() => Ffmpeg("conv-vorbis.mkv", "-f lavfi -i sine=f=330:d=3:sample_rate=44100 -ac 2 -c:a vorbis -strict experimental");

    /// <summary>3 s 5.1 DTS in Matroska.</summary>
    public static string Dts51Mkv() => Ffmpeg("conv-dts-51.mkv", $"-f lavfi -i sine=f=440:d=3:sample_rate=48000 -af \"{Pan51}\" -c:a dca -strict -2");

    /// <summary>3 s 5.1 TrueHD in Matroska.</summary>
    public static string TrueHd51Mkv() => Ffmpeg("conv-truehd-51.mkv", $"-f lavfi -i sine=f=440:d=3:sample_rate=48000 -af \"{Pan51}\" -c:a truehd -strict -2");

    /// <summary>3 s stereo 24-bit PCM in Matroska at 96 kHz (resampled to 48 kHz by the AAC conversion).</summary>
    public static string Pcm96kMkv() => Ffmpeg("conv-pcm-96k.mkv", "-f lavfi -i sine=f=440:d=3:sample_rate=96000 -ac 2 -c:a pcm_s24le");

    /// <summary>3 s 5.1 AC-3 in Matroska.</summary>
    public static string Ac351Mkv() => Ffmpeg("conv-ac3-51.mkv", $"-f lavfi -i sine=f=440:d=3:sample_rate=48000 -af \"{Pan51}\" -c:a ac3");

    /// <summary>3 s stereo Opus in Matroska.</summary>
    public static string OpusMkv() => Ffmpeg("conv-opus.mkv", "-f lavfi -i sine=f=440:d=3:sample_rate=48000 -ac 2 -c:a libopus");

    /// <summary>MP4 with H.264 (640×360, 25 fps, 4 s) and AAC.</summary>
    public static string VideoMp4() =>
        Ffmpeg("conv-video.mp4", "-f lavfi -i testsrc2=duration=4:size=640x360:rate=25 -f lavfi -i sine=d=4 -c:v libx264 -preset fast -g 50 -bf 2 -c:a aac -shortest");

    /// <summary>Anamorphic MKV: 720×480 with a 32:27 pixel aspect ratio (displayed 853×480), MPEG-2.</summary>
    public static string AnamorphicMkv() =>
        Ffmpeg("conv-anamorphic.mkv", "-f lavfi -i testsrc=duration=3:size=720x480:rate=25 -vf setsar=32/27 -c:v mpeg2video -g 12");

    public static string TempPath(string extension) => MediaProbe.TempPath(extension);

    /// <summary>Errors ffmpeg reports while decoding every stream of the file.</summary>
    public static string DecodeErrors(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0 -f null -")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return err.Trim();
    }

    /// <summary>Raw ffprobe JSON of the streams (to read references, dispositions …).</summary>
    public static JsonElement ProbeStreams(string path)
    {
        var json = Fixtures.Run("ffprobe", $"-v error -show_streams -of json {Fixtures.Quote(path)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams").Clone();
    }

    /// <summary>Duration of the file's format in seconds (ffprobe).</summary>
    public static double FormatDuration(string path) =>
        double.Parse(Fixtures.Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 {Fixtures.Quote(path)}").Trim(), CultureInfo.InvariantCulture);

    public static void AssertClose(double expected, double actual, double tolerance, string what = "Duration") =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what} {actual:0.####} differs from {expected:0.####} by more than {tolerance:0.####}.");

    public static List<Track> Audio(MediaDocument doc) => doc.Tracks.Where(t => t is AudioTrack).ToList();
}
