using System.Globalization;
using System.Text.Json;

namespace MMW.TestSupport;

/// <summary>ffprobe/ffmpeg/mkvmerge based checks used by remux tests.</summary>
public static class MediaProbe
{
    public static bool HasFfmpeg => Fixtures.HasTool("ffmpeg") && Fixtures.HasTool("ffprobe");

    public static void RequireFfmpeg()
    {
        if (!HasFfmpeg)
            Xunit.Assert.Skip("ffmpeg/ffprobe not installed.");
    }

    /// <summary>One stream as reported by ffprobe.</summary>
    /// <param name="Duration">
    /// End of the stream's presentation in seconds: the largest packet presentation time plus duration (container
    /// independent; Matroska DURATION tags are written with different meanings by different muxers).
    /// </param>
    public sealed record StreamInfo(int Index, string Type, string Codec, double Duration, int Channels, string ChannelLayout, int Width, int Height, string Language);

    public static List<StreamInfo> Streams(string path, string inputOptions = "")
    {
        var json = Fixtures.Run("ffprobe", $"-v error {inputOptions} -show_streams -of json {Fixtures.Quote(path)}");
        using var doc = JsonDocument.Parse(json);
        var ends = PacketEnds(path, inputOptions);
        var result = new List<StreamInfo>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1)
                continue;
            var index = s.GetProperty("index").GetInt32();
            var language = s.TryGetProperty("tags", out var t2) && t2.TryGetProperty("language", out var lang) ? lang.GetString()! : string.Empty;
            result.Add(new StreamInfo(
                index,
                s.TryGetProperty("codec_type", out var type) ? type.GetString()! : string.Empty,
                s.TryGetProperty("codec_name", out var codec) ? codec.GetString()! : string.Empty,
                ends.GetValueOrDefault(index),
                s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
                s.TryGetProperty("channel_layout", out var cl) ? cl.GetString()! : string.Empty,
                s.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                s.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                language));
        }

        return result;
    }

    /// <summary>Largest presentation end (pts + duration) of the packets of each stream.</summary>
    private static Dictionary<int, double> PacketEnds(string path, string inputOptions)
    {
        var csv = Fixtures.Run("ffprobe", $"-v error {inputOptions} -show_entries packet=stream_index,pts_time,duration_time -of csv=p=0 {Fixtures.Quote(path)}");
        var ends = new Dictionary<int, double>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split(',');
            if (p.Length < 3 || !int.TryParse(p[0], CultureInfo.InvariantCulture, out var stream) ||
                !double.TryParse(p[1], CultureInfo.InvariantCulture, out var pts))
                continue;
            var duration = double.TryParse(p[2], CultureInfo.InvariantCulture, out var dv) ? dv : 0;
            ends[stream] = Math.Max(ends.GetValueOrDefault(stream, double.MinValue), pts + duration);
        }

        return ends;
    }

    /// <summary>
    /// MD5 of every packet of every audio/video stream, per stream index (timestamps are ignored: they legitimately
    /// differ between container time bases).
    /// </summary>
    public static Dictionary<int, List<string>> PacketHashes(string path, string maps = "-map 0:V? -map 0:a?", string inputOptions = "")
    {
        var output = Fixtures.Run("ffmpeg", $"-v error {inputOptions} -i {Fixtures.Quote(path)} {maps} -c copy -f framemd5 -");
        var result = new Dictionary<int, List<string>>();
        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 6)
                continue;
            var stream = int.Parse(parts[0], CultureInfo.InvariantCulture);
            if (!result.TryGetValue(stream, out var list))
                result[stream] = list = [];
            list.Add(parts[5]);
        }

        return result;
    }

    /// <summary>Errors ffmpeg reports while demuxing the whole file.</summary>
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

    /// <summary>mkvmerge -J identification (errors and warnings included).</summary>
    public static JsonElement MkvIdentify(string path)
    {
        var json = Fixtures.Run("mkvmerge", $"-J {Fixtures.Quote(path)}");
        var start = json.IndexOf('{', StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(start > 0 ? json[start..] : json);
        return doc.RootElement.Clone();
    }

    /// <summary>A fresh temporary path with the given extension.</summary>
    public static string TempPath(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmw-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
    }

    public static void Delete(params string[] paths)
    {
        if (Environment.GetEnvironmentVariable("MMW_KEEP_TEST_FILES") is { Length: > 0 })
            return;
        foreach (var p in paths)
        {
            if (File.Exists(p))
                File.Delete(p);
        }
    }
}
