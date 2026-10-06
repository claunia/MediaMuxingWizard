using MMW.Core.Model;
using MMW.Formats.Matroska;
using MMW.Formats.Mp4;
using MMW.TestSupport;

namespace MMW.Media.Remux.Tests;

/// <summary>Generated sources for remux tests (cached under tests/fixtures/generated).</summary>
internal static class RemuxFixtures
{
    public const string Srt = "1\n00:00:00,500 --> 00:00:01,500\n<i>Hello</i>\n\n2\n00:00:02,000 --> 00:00:02,800\nWorld\n\n3\n00:00:02,500 --> 00:00:03,500\nOverlap\n";

    public static readonly Mp4Handler Mp4 = new();
    public static readonly MatroskaHandler Mkv = new();

    static RemuxFixtures() => MediaRemux.EnsureRegistered();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Text(string name, string content)
    {
        Directory.CreateDirectory(Fixtures.GeneratedDirectory);
        var path = Path.Combine(Fixtures.GeneratedDirectory, name);
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
        return path;
    }

    private static string Ffmpeg(string name, string args)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", "-v error -y " + args + " {out}");
    }

    /// <summary>MKV: H.264 with B-frames (25 fps), AAC, SRT; 4 seconds.</summary>
    public static string MkvH264AacSrt()
    {
        var srt = Text("remux.srt", Srt);
        return Ffmpeg("remux-h264-aac-srt.mkv",
            "-f lavfi -i testsrc=duration=4:size=320x240:rate=25 -f lavfi -i sine=f=440:d=4 " +
            $"-i {Fixtures.Quote(srt)} -map 0:v -map 1:a -map 2:s -c:v libx264 -preset fast -bf 3 -c:a aac -c:s srt " +
            "-metadata title=MkvSource -metadata:s:a:0 language=fre -metadata:s:s:0 language=spa");
    }

    /// <summary>MKV at 23.976 fps (frame grid snapping) with AC-3 5.1 and E-AC-3 5.1.</summary>
    public static string MkvHevcAc3Eac3()
    {
        return Ffmpeg("remux-hevc-ac3-eac3.mkv",
            "-f lavfi -i testsrc=duration=3:size=320x240:rate=24000/1001 -f lavfi -i sine=f=440:d=3:sample_rate=48000 " +
            "-filter_complex \"[1:a]pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0,asplit[a1][a2]\" " +
            "-map 0:v -map [a1] -map [a2] -c:v libx265 -preset ultrafast -x265-params log-level=error:bframes=3 -c:a:0 ac3 -c:a:1 eac3");
    }

    /// <summary>MP4: H.264 + 2 AAC + mov_text + chapters (from the MP4 test fixtures recipe).</summary>
    public static string Mp4Full()
    {
        var srt = Text("remux.srt", Srt);
        var chapters = Text("remux-chapters.ffmeta", ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Two\n");
        return Ffmpeg("remux-full.mp4",
            "-f lavfi -i testsrc=duration=3:size=320x240:rate=25 -f lavfi -i sine=f=440:d=3 " +
            $"-i {Fixtures.Quote(srt)} -i {Fixtures.Quote(chapters)} -map 0:v -map 1:a -map 1:a -map 2:s -map_chapters 3 " +
            "-c:v libx264 -preset fast -bf 2 -c:a aac -c:s mov_text -metadata:s:a:0 language=eng -metadata:s:a:1 language=fra " +
            "-metadata title=Mp4Source -f mp4");
    }

    public static string Adts() => Ffmpeg("remux-import.aac", "-f lavfi -i sine=f=660:d=3 -ac 2 -c:a aac -f adts");

    public static string Ac3() => Ffmpeg("remux-import.ac3", "-f lavfi -i sine=f=550:d=3:sample_rate=48000 -ac 2 -c:a ac3 -f ac3");

    public static string RawH264() =>
        Ffmpeg("remux-import.264", "-f lavfi -i testsrc2=duration=2:size=320x240:rate=30000/1001 -c:v libx264 -preset medium -bf 3 -g 15 -bsf:v h264_mp4toannexb -f h264");

    public static string RawHevc() =>
        Ffmpeg("remux-import.265", "-f lavfi -i testsrc2=duration=2:size=320x240:rate=25 -c:v libx265 -preset fast -x265-params log-level=error:bframes=3:keyint=20 -f hevc");

    /// <summary>MD5 of every decoded video frame (validates presentation order of reordered streams).</summary>
    public static List<string> DecodedFrameHashes(string path, string map = "0:v:0")
    {
        var output = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map {map} -f framemd5 -");
        return output.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).ToList();
    }

    public static void AssertDuration(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Duration {actual:0.###} s differs from {expected:0.###} s by more than {tolerance:0.###} s.");

    public static List<Track> AvTracks(MediaDocument doc) => doc.Tracks.Where(t => t is VideoTrack or AudioTrack).ToList();
}
