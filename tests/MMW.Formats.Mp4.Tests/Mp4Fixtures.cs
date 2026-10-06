using System.Text.Json;
using MMW.TestSupport;

namespace MMW.Formats.Mp4.Tests;

internal static class Mp4Fixtures
{
    private const string Srt = "1\n00:00:00,500 --> 00:00:01,500\nHello\n\n2\n00:00:02,000 --> 00:00:02,800\nWorld\n";

    private const string Chapters = ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Opening\n" +
                                    "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Middle\n" +
                                    "[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=3000\ntitle=Ending\n";

    public static void RequireTools()
    {
        if (!Fixtures.HasTool("ffmpeg") || !Fixtures.HasTool("ffprobe"))
            Assert.Skip("ffmpeg/ffprobe not installed.");
    }

    /// <summary>H.264 + 2×AAC + mov_text + chapters; moov at the end (ffmpeg default).</summary>
    public static string MoovAtEnd() => Make("mp4-moov-end.mp4", string.Empty);

    /// <summary>Same content with moov before mdat.</summary>
    public static string FastStart() => Make("mp4-faststart.mp4", "-movflags +faststart");

    private static string Make(string name, string extra)
    {
        RequireTools();
        Directory.CreateDirectory(Fixtures.GeneratedDirectory);
        var srt = Path.Combine(Fixtures.GeneratedDirectory, "subs.srt");
        var chapters = Path.Combine(Fixtures.GeneratedDirectory, "chapters.ffmeta");
        if (!File.Exists(srt))
            File.WriteAllText(srt, Srt);
        if (!File.Exists(chapters))
            File.WriteAllText(chapters, Chapters);

        return Fixtures.Get(name, "ffmpeg",
            "-y -v error -f lavfi -i testsrc=duration=3:size=320x240:rate=25 -f lavfi -i sine=f=440:d=3 " +
            $"-i {Fixtures.Quote(srt)} -i {Fixtures.Quote(chapters)} " +
            "-map 0:v -map 1:a -map 1:a -map 2:s -map_chapters 3 -c:v libx264 -preset ultrafast -c:a aac -c:s mov_text " +
            "-metadata:s:a:0 language=eng -metadata:s:a:1 language=fra -metadata:s:s:0 language=spa " +
            $"-metadata title=Fixture {extra} -f mp4 {{out}}");
    }

    /// <summary>A small, valid JPEG image.</summary>
    public static byte[] Jpeg()
    {
        RequireTools();
        return File.ReadAllBytes(Fixtures.Get("cover.jpg", "ffmpeg", "-y -v error -f lavfi -i color=c=red:s=64x64 -frames:v 1 -f mjpeg {out}"));
    }

    public static JsonElement Probe(string path)
    {
        var json = Fixtures.Run("ffprobe", $"-v error -show_format -show_streams -show_chapters -of json {Fixtures.Quote(path)}");
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>Hashes of every audio/video packet, to prove media data was not touched.</summary>
    public static string PacketHashes(string path) =>
        string.Join('\n', Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:V? -map 0:a? -c copy -f framemd5 -")
            .Split('\n').Where(l => !l.StartsWith('#')));

    /// <summary>Errors reported by ffmpeg while demuxing the whole file.</summary>
    public static string DemuxErrors(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0 -c copy -f null -")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return err.Trim();
    }

    public static string BoxOrder(string path)
    {
        using var fs = File.OpenRead(path);
        return string.Join(",", Boxes.Mp4Layout.Read(fs).Boxes.Select(b => b.Type));
    }
}
