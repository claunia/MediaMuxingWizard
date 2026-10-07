using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MMW.Core.Resources;

namespace MMW.Core.Chapters;

/// <summary>Imports and exports chapter lists as text (mp4chaps and OGG/Matroska simple formats, CSV titles).</summary>
public static partial class ChapterTextFormat
{
    [GeneratedRegex(@"^CHAPTER(?<n>\d+)\s*=\s*(?<time>[\d:.,]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OggTime();

    [GeneratedRegex(@"^CHAPTER(?<n>\d+)NAME\s*=\s*(?<name>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OggName();

    [GeneratedRegex(@"^(?<time>\d+:\d{1,2}:\d{1,2}(?:[.,]\d+)?|\d+:\d{1,2}(?:[.,]\d+)?)\s*(?:[-–]\s*)?(?<name>.*)$")]
    private static partial Regex Mp4Chaps();

    /// <summary>Parses chapters in either supported format; missing titles become "Chapter N".</summary>
    public static List<Chapter> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim().TrimStart('﻿')).Where(l => l.Length > 0).ToList();

        var times = new SortedDictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            if (OggTime().Match(line) is { Success: true } t && ChapterTime.TryParse(t.Groups["time"].Value, out var ts))
                times[int.Parse(t.Groups["n"].Value, CultureInfo.InvariantCulture)] = ts;
            else if (OggName().Match(line) is { Success: true } n)
                names[int.Parse(n.Groups["n"].Value, CultureInfo.InvariantCulture)] = n.Groups["name"].Value.Trim();
        }

        var result = new List<Chapter>();
        if (times.Count > 0)
        {
            foreach (var (n, time) in times)
                result.Add(new Chapter(time, names.GetValueOrDefault(n) is { Length: > 0 } name ? name : string.Format(CultureInfo.CurrentCulture, Strings.Label_ChapterNumber, result.Count + 1)));
        }
        else
        {
            foreach (var line in lines)
            {
                if (Mp4Chaps().Match(line) is { Success: true } m && ChapterTime.TryParse(m.Groups["time"].Value, out var ts))
                {
                    var name = m.Groups["name"].Value.Trim();
                    result.Add(new Chapter(ts, name.Length > 0 ? name : string.Format(CultureInfo.CurrentCulture, Strings.Label_ChapterNumber, result.Count + 1)));
                }
            }
        }

        return result.OrderBy(c => c.Start).ToList();
    }

    /// <summary>Parses CSV "index,title" rows (used to rename existing chapters).</summary>
    public static List<string> ParseTitlesCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var titles = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            var title = comma >= 0 && int.TryParse(line[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? line[(comma + 1)..] : line;
            titles.Add(title.Trim().Trim('"'));
        }

        return titles;
    }

    /// <summary>Writes the OGG/Matroska simple chapter format.</summary>
    public static string ToOgg(IEnumerable<Chapter> chapters)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var c in chapters.OrderBy(c => c.Start))
        {
            sb.Append(CultureInfo.InvariantCulture, $"CHAPTER{i:00}={ChapterTime.Format(c.Start)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"CHAPTER{i:00}NAME={c.Title}\n");
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Writes the mp4chaps format ("hh:mm:ss.fff Title").</summary>
    public static string ToMp4Chaps(IEnumerable<Chapter> chapters) =>
        string.Concat(chapters.OrderBy(c => c.Start).Select(c => $"{ChapterTime.Format(c.Start)} {c.Title}\n"));
}
