using System.Diagnostics;
using System.Text.Json;
using MMW.TestSupport;

namespace MMW.Formats.Matroska.Tests;

/// <summary>Generated Matroska fixtures and wrappers around mkvtoolnix/ffmpeg used to validate output.</summary>
internal static class MkvFixtures
{
    private static readonly Lock s_lock = new();

    public static void RequireTools(params string[] tools)
    {
        foreach (var tool in tools)
        {
            if (!Fixtures.HasTool(tool))
                Assert.Skip($"{tool} is not installed.");
        }
    }

    /// <summary>ffmpeg-made MKV: H.264 (or MPEG-4) video + AAC audio, 2 seconds, title "Hello", French audio.</summary>
    public static string Basic()
    {
        RequireTools("ffmpeg");
        var video = HasEncoder("libx264") ? "libx264 -preset ultrafast" : "mpeg4";
        return Fixtures.Get("mkv-basic.mkv", "ffmpeg",
            "-v error -y -f lavfi -i testsrc=duration=2:size=320x240:rate=25 -f lavfi -i sine=duration=2 " +
            $"-c:v {video} -c:a aac -metadata title=Hello -metadata:s:a:0 language=fre {{out}}");
    }

    /// <summary>
    /// mkvmerge-made MKV with named tracks, flags, an SRT subtitle, chapters, a JPEG cover, a text attachment and
    /// global tags.
    /// </summary>
    public static string Full()
    {
        RequireTools("ffmpeg", "mkvmerge");
        var basic = Basic();
        var cover = Cover();
        var srt = WriteText("subs.srt", "1\n00:00:00,000 --> 00:00:01,000\nHello\n\n2\n00:00:01,000 --> 00:00:02,000\nWorld\n");
        var notes = WriteText("notes.txt", "These are notes that must survive every save.\n");
        var chapters = WriteText("chapters.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Chapters>
              <EditionEntry>
                <ChapterAtom><ChapterTimeStart>00:00:00.000</ChapterTimeStart><ChapterDisplay><ChapterString>Intro</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom>
                <ChapterAtom><ChapterTimeStart>00:00:01.000</ChapterTimeStart><ChapterDisplay><ChapterString>Main</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom>
              </EditionEntry>
            </Chapters>
            """);
        var tags = WriteText("tags.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tags>
              <Tag>
                <Targets><TargetTypeValue>50</TargetTypeValue></Targets>
                <Simple><Name>ARTIST</Name><String>Someone</String></Simple>
                <Simple><Name>X_UNKNOWN_THING</Name><String>keep me</String></Simple>
              </Tag>
            </Tags>
            """);
        return Fixtures.Get("mkv-full.mkv", "mkvmerge",
            $"-q -o {{out}} --title \"Full fixture\" --track-name 0:Video --track-name 1:\"French audio\" --default-track-flag 1:0 {Q(basic)} " +
            $"--language 0:spa --forced-display-flag 0:1 --hearing-impaired-flag 0:1 --track-name 0:Subs {Q(srt)} " +
            $"--chapters {Q(chapters)} --global-tags {Q(tags)} " +
            $"--attachment-mime-type image/jpeg --attachment-name cover.jpg --attach-file {Q(cover)} " +
            $"--attachment-mime-type text/plain --attach-file {Q(notes)}");
    }

    /// <summary>mkvmerge-made MKV with HDR10 colour metadata on the video track.</summary>
    public static string Hdr()
    {
        RequireTools("ffmpeg", "mkvmerge");
        var basic = Basic();
        return Fixtures.Get("mkv-hdr.mkv", "mkvmerge",
            "-q -o {out} --colour-matrix-coefficients 0:9 --colour-transfer-characteristics 0:16 --colour-primaries 0:9 " +
            "--colour-range 0:1 --max-content-light 0:1000 --max-frame-light 0:400 " +
            "--chromaticity-coordinates 0:0.708,0.292,0.170,0.797,0.131,0.046 --white-colour-coordinates 0:0.3127,0.3290 " +
            $"--max-luminance 0:1000 --min-luminance 0:0.005 {Q(basic)}");
    }

    /// <summary>ffmpeg-made WebM (VP9 + Opus).</summary>
    public static string WebM()
    {
        RequireTools("ffmpeg");
        if (!HasEncoder("libvpx-vp9") || !HasEncoder("libopus"))
            Assert.Skip("ffmpeg lacks libvpx-vp9/libopus.");
        return Fixtures.Get("webm-basic.webm", "ffmpeg",
            "-v error -y -f lavfi -i testsrc=duration=1:size=160x120:rate=10 -f lavfi -i sine=duration=1 " +
            "-c:v libvpx-vp9 -deadline realtime -b:v 100k -c:a libopus -metadata title=WebTitle {out}");
    }

    public static string Cover()
    {
        RequireTools("ffmpeg");
        return Fixtures.Get("cover.jpg", "ffmpeg", "-v error -y -f lavfi -i testsrc=size=64x64 -frames:v 1 {out}");
    }

    /// <summary>Runs <c>mkvmerge -J</c> and returns the parsed identification.</summary>
    public static JsonElement Identify(string path)
    {
        var json = Fixtures.Run("mkvmerge", $"-J {Q(path)}");

        // Some mkvmerge builds print debug lines (e.g. "skip unknown before ...") ahead of the JSON.
        var start = json.IndexOf('{', StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(start > 0 ? json[start..] : json);
        return doc.RootElement.Clone();
    }

    /// <summary>Asserts that mkvmerge identifies the file without errors or warnings.</summary>
    public static JsonElement AssertValid(string path)
    {
        var id = Identify(path);
        Assert.True(id.GetProperty("container").GetProperty("recognized").GetBoolean(), "mkvmerge does not recognise the file.");
        Assert.True(id.GetProperty("container").GetProperty("supported").GetBoolean(), "mkvmerge does not support the file.");
        Assert.Empty(id.GetProperty("errors").EnumerateArray());
        Assert.Empty(id.GetProperty("warnings").EnumerateArray());
        if (Fixtures.HasTool("mkvinfo"))
        {
            var info = RunAllowingFailure("mkvinfo", $"-v {Q(path)}");
            Assert.True(info.ExitCode == 0, "mkvinfo failed: " + info.Output);
            Assert.DoesNotContain("Error:", info.Output, StringComparison.Ordinal);
        }

        return id;
    }

    /// <summary>Per-packet MD5 of every stream (stream copy), used to prove the media data is untouched.</summary>
    public static string FrameMd5(string path)
    {
        var output = Fixtures.Run("ffmpeg", $"-v error -i {Q(path)} -map 0:V? -map 0:a? -map 0:s? -c copy -f framemd5 -");
        return string.Join('\n', output.Split('\n').Where(l => !l.StartsWith('#')));
    }

    /// <summary>Exports a part of the file with mkvextract (e.g. "tags", "chapters") and returns the XML.</summary>
    public static string Extract(string path, string what)
    {
        var output = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N") + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Fixtures.Run("mkvextract", $"{Q(path)} {what} {Q(output)}");
        try
        {
            return File.Exists(output) ? File.ReadAllText(output) : string.Empty;
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static JsonElement Track(JsonElement identification, int number) =>
        identification.GetProperty("tracks").EnumerateArray().First(t => t.GetProperty("properties").GetProperty("number").GetInt32() == number);

    public static string Q(string s) => Fixtures.Quote(s);

    public static (int ExitCode, string Output) RunAllowingFailure(string tool, string arguments)
    {
        var psi = new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static bool HasEncoder(string name) =>
        Fixtures.Run("ffmpeg", "-hide_banner -encoders").Contains(" " + name + " ", StringComparison.Ordinal);

    private static string WriteText(string name, string content)
    {
        var path = Path.Combine(Fixtures.GeneratedDirectory, name);
        lock (s_lock)
        {
            Directory.CreateDirectory(Fixtures.GeneratedDirectory);
            if (!File.Exists(path) || File.ReadAllText(path) != content)
                File.WriteAllText(path, content);
        }

        return path;
    }
}
