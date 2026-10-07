using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Formats.Elementary;

/// <summary>A parsed text subtitle file: codec description and cues in milliseconds.</summary>
internal sealed record SubtitleFile(CodecConfig Config, List<(long Start, long End, string Text, string? Settings)> Cues);

/// <summary>Parsers for SubRip, (Advanced) SubStation Alpha and WebVTT files.</summary>
internal static partial class SubtitleFiles
{
    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[,.](\d{1,3}))?")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^\s*(\S+)\s*-->\s*(\S+)")]
    private static partial Regex CueTimingRegex();

    /// <summary>Reads a text file, detecting UTF-8/UTF-16 (BOM or valid UTF-8) and falling back to Latin-1.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Parses a timestamp ("hh:mm:ss,mmm", "mm:ss.mmm", "h:mm:ss.cc") into milliseconds.</summary>
    public static long? ParseTime(string text)
    {
        var m = TimeRegex().Match(text.Trim());
        if (!m.Success)
            return null;
        var hours = m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        long fraction = 0;
        if (m.Groups[4].Success)
        {
            var f = m.Groups[4].Value;
            fraction = long.Parse(f, CultureInfo.InvariantCulture) * (f.Length switch
            {
                1 => 100,
                2 => 10,
                _ => 1,
            });
        }

        return ((hours * 60 + minutes) * 60 + seconds) * 1000 + fraction;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Parses a SubRip file.</summary>
    public static SubtitleFile ParseSrt(string text)
    {
        var cues = new List<(long, long, string, string?)>();
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var timing = CueTimingRegex().Match(lines[i]);
            if (!timing.Success || ParseTime(timing.Groups[1].Value) is not { } start || ParseTime(timing.Groups[2].Value) is not { } end)
                continue;
            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
                body.Add(lines[i].TrimEnd());

            // A following index line that was swallowed (no blank line separator) is not text.
            cues.Add((start, Math.Max(end, start), string.Join('\n', body), null));
        }

        if (cues.Count == 0)
            throw new InvalidDataException("No SubRip cues found.");
        return new SubtitleFile(Config(CodecType.TextUtf8, "srt", null), Sorted(cues));
    }

    /// <summary>
    /// Parses a WebVTT file. Cues keep their text and settings (the rest of the timing line); cue identifiers and the
    /// NOTE blocks after the first cue are dropped. The header (the "WEBVTT" block and the STYLE, REGION and NOTE
    /// blocks before the first cue, verbatim) becomes the extradata.
    /// </summary>
    public static SubtitleFile ParseWebVtt(string text)
    {
        var lines = Lines(text);
        if (lines.Length == 0 || !lines[0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal))
            throw new InvalidDataException("Not a WebVTT file.");
        lines[0] = lines[0].TrimStart('﻿');
        var header = new StringBuilder();
        var cues = new List<(long, long, string, string?)>();
        var inHeader = true;
        var i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Trim().Length == 0)
            {
                i++;
                continue;
            }

            // One block: consecutive non-blank lines.
            var first = i;
            while (i < lines.Length && lines[i].Trim().Length > 0)
                i++;
            var block = lines[first..i];
            var timingIndex = first == 0 ? -1 : Array.FindIndex(block, l => l.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                if (inHeader)
                {
                    if (header.Length > 0)
                        header.Append("\n\n");
                    header.AppendJoin('\n', block.Select(l => l.TrimEnd()));
                }

                continue;
            }

            inHeader = false;
            var timing = CueTimingRegex().Match(block[timingIndex]);
            if (!timing.Success || ParseTime(timing.Groups[1].Value) is not { } start || ParseTime(timing.Groups[2].Value) is not { } end)
                continue;
            var settings = block[timingIndex][timing.Length..].Trim();
            var body = string.Join('\n', block[(timingIndex + 1)..].Select(l => l.TrimEnd()));
            cues.Add((start, Math.Max(end, start), body, settings.Length == 0 ? null : settings));
        }

        return new SubtitleFile(Config(CodecType.WebVtt, "vtt", Encoding.UTF8.GetBytes(header.ToString())), Sorted(cues));
    }

    /// <summary>Parses an ASS/SSA file into Matroska-style blocks ("ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text").</summary>
    public static SubtitleFile ParseAss(string text)
    {
        var lines = Lines(text);
        var header = new StringBuilder();
        var cues = new List<(long, long, string, string?)>();
        string[]? format = null;
        var section = string.Empty;
        var ssa = false;
        var order = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimStart('﻿').TrimEnd();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.ToLowerInvariant();
                if (section == "[v4 styles]")
                    ssa = true;
                header.Append(line).Append('\n');
                continue;
            }

            if (section == "[events]")
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    format = line[7..].Split(',', StringSplitOptions.TrimEntries).Select(f => f.ToLowerInvariant()).ToArray();
                    header.Append(line).Append('\n');
                    continue;
                }

                if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) && format is not null)
                {
                    var fields = SplitFields(line[9..].TrimStart(), format.Length);
                    string Field(string name) => Array.IndexOf(format, name) is var ix and >= 0 && ix < fields.Length ? fields[ix] : string.Empty;
                    if (ParseTime(Field("start")) is not { } start || ParseTime(Field("end")) is not { } end)
                        continue;
                    var layer = Field("layer");
                    if (layer.Length == 0)
                        layer = Field("marked").Replace("Marked=", string.Empty, StringComparison.OrdinalIgnoreCase);
                    var block = string.Join(',', order++.ToString(CultureInfo.InvariantCulture), layer.Length == 0 ? "0" : layer, Field("style"), Field("name"),
                        Field("marginl"), Field("marginr"), Field("marginv"), Field("effect"), Field("text"));
                    cues.Add((start, Math.Max(end, start), block, null));
                    continue;
                }

                if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (section == "[script info]" && line.StartsWith("ScriptType:", StringComparison.OrdinalIgnoreCase) &&
                     !line.Contains("v4.00+", StringComparison.OrdinalIgnoreCase))
            {
                ssa = true;
            }

            if (line.Length > 0 || header.Length > 0)
                header.Append(line).Append('\n');
        }

        if (format is null)
            throw new InvalidDataException("No [Events] Format line found.");
        var codec = ssa ? CodecType.Ssa : CodecType.Ass;
        return new SubtitleFile(Config(codec, ssa ? "ssa" : "ass", Encoding.UTF8.GetBytes(header.ToString().TrimEnd('\n') + "\n")), Sorted(cues));
    }

    private static string[] SplitFields(string value, int count)
    {
        var result = new string[count];
        var pos = 0;
        for (var i = 0; i < count; i++)
        {
            if (i == count - 1)
            {
                result[i] = value[pos..];
                break;
            }

            var comma = value.IndexOf(',', pos);
            if (comma < 0)
            {
                result[i] = value[pos..].Trim();
                for (var j = i + 1; j < count; j++)
                    result[j] = string.Empty;
                break;
            }

            result[i] = value[pos..comma].Trim();
            pos = comma + 1;
        }

        return result;
    }

    private static List<(long, long, string, string?)> Sorted(List<(long Start, long End, string Text, string? Settings)> cues) =>
        cues.Select((c, i) => (c, i)).OrderBy(x => x.c.Start).ThenBy(x => x.i).Select(x => x.c).ToList();

    private static CodecConfig Config(CodecType codec, string id, byte[]? extradata) => new()
    {
        Codec = codec,
        Kind = TrackKind.Subtitle,
        SourceCodecId = id,
        Extradata = extradata,
        Timescale = 1000,
    };
}

/// <summary>Delivers the cues of a parsed subtitle file as samples (millisecond timescale).</summary>
internal sealed class SubtitleCueParser : IElementaryParser
{
    private readonly List<(long Start, long End, string Text, string? Settings)> _cues;
    private int _next;

    public SubtitleCueParser(List<(long Start, long End, string Text, string? Settings)> cues) => _cues = cues;

    public MediaSample? Next()
    {
        if (_next >= _cues.Count)
            return null;
        var (start, end, text, settings) = _cues[_next++];
        return new MediaSample { Dts = start, Duration = Math.Max(1, end - start), IsSync = true, Data = Encoding.UTF8.GetBytes(text), CueSettings = settings };
    }

    public void Dispose()
    {
    }
}
