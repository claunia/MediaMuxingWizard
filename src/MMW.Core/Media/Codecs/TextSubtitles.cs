using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MMW.Core.Media.Codecs;

/// <summary>Character styles shared by tx3g, SubRip, ASS and WebVTT.</summary>
[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
}

/// <summary>A styled range of <see cref="StyledText.Text"/> (UTF-16 indexes, end exclusive).</summary>
public readonly record struct StyleRun(int Start, int End, TextStyle Style);

/// <summary>Subtitle text with character styles, the common form between subtitle formats.</summary>
public sealed record StyledText(string Text, IReadOnlyList<StyleRun> Runs)
{
    public static StyledText Empty { get; } = new(string.Empty, []);

    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// The cue is a forced subtitle (shown even when subtitles are off): the tx3g 'frcd' sample modifier, or the
    /// forced flag of a bitmap subtitle that was recognised by OCR.
    /// </summary>
    public bool Forced { get; init; }

    /// <summary>Joins several cues shown at the same time, one per line (forced when any of them is).</summary>
    public static StyledText Join(IEnumerable<StyledText> parts)
    {
        var sb = new StringBuilder();
        var runs = new List<StyleRun>();
        var forced = false;
        foreach (var p in parts.Where(p => !p.IsEmpty))
        {
            forced |= p.Forced;
            if (sb.Length > 0)
                sb.Append('\n');
            var offset = sb.Length;
            sb.Append(p.Text);
            runs.AddRange(p.Runs.Select(r => new StyleRun(r.Start + offset, r.End + offset, r.Style)));
        }

        return new StyledText(sb.ToString(), runs) { Forced = forced };
    }
}

/// <summary>Conversions between subtitle markups.</summary>
public static class SubtitleText
{
    /// <summary>Parses SubRip text with HTML-like tags (&lt;i&gt;, &lt;b&gt;, &lt;u&gt;, &lt;font&gt;) and ASS override blocks.</summary>
    public static StyledText FromSrt(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        var builder = new RunBuilder();
        var i = 0;
        var text = markup.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    var tag = text[(i + 1)..close].Trim().ToLowerInvariant();
                    if (ApplyHtmlTag(builder, tag))
                    {
                        i = close + 1;
                        continue;
                    }
                }
            }
            else if (c == '{' && i + 1 < text.Length && text[i + 1] == '\\')
            {
                var close = text.IndexOf('}', i + 1);
                if (close > i)
                {
                    ApplyAssOverrides(builder, text[(i + 1)..close]);
                    i = close + 1;
                    continue;
                }
            }

            builder.Append(c);
            i++;
        }

        return builder.Build();
    }

    private static bool ApplyHtmlTag(RunBuilder builder, string tag)
    {
        switch (tag)
        {
            case "i":
                builder.Set(TextStyle.Italic, true);
                return true;
            case "/i":
                builder.Set(TextStyle.Italic, false);
                return true;
            case "b":
                builder.Set(TextStyle.Bold, true);
                return true;
            case "/b":
                builder.Set(TextStyle.Bold, false);
                return true;
            case "u":
                builder.Set(TextStyle.Underline, true);
                return true;
            case "/u":
                builder.Set(TextStyle.Underline, false);
                return true;
        }

        // Unsupported but recognised tags are dropped (<font …>, </font>, <c.yellow>, <v Bob>, timestamps …).
        return tag.StartsWith("font", StringComparison.Ordinal) || tag.StartsWith("/font", StringComparison.Ordinal) ||
               tag.StartsWith("c.", StringComparison.Ordinal) || tag is "c" or "/c" || tag.StartsWith('v') || tag is "/v" ||
               tag.StartsWith("lang", StringComparison.Ordinal) || tag is "/lang" or "ruby" or "/ruby" or "rt" or "/rt" ||
               (tag.Length > 0 && char.IsDigit(tag[0]));
    }

    /// <summary>Parses the Text field of an ASS/SSA event (override blocks, \N, \n, \h).</summary>
    public static StyledText FromAss(string eventText)
    {
        ArgumentNullException.ThrowIfNull(eventText);
        var builder = new RunBuilder();
        var drawing = false;
        var i = 0;
        while (i < eventText.Length)
        {
            var c = eventText[i];
            if (c == '{')
            {
                var close = eventText.IndexOf('}', i + 1);
                if (close > i)
                {
                    drawing = ApplyAssOverrides(builder, eventText[(i + 1)..close]) ?? drawing;
                    i = close + 1;
                    continue;
                }
            }

            if (c == '\\' && i + 1 < eventText.Length && eventText[i + 1] is 'N' or 'n' or 'h')
            {
                if (!drawing)
                    builder.Append(eventText[i + 1] == 'h' ? ' ' : '\n');
                i += 2;
                continue;
            }

            if (!drawing)
                builder.Append(c);
            i++;
        }

        return builder.Build();
    }

    /// <summary>Applies \i, \b, \u and \p overrides; returns the new drawing mode when \p was present.</summary>
    private static bool? ApplyAssOverrides(RunBuilder builder, string block)
    {
        bool? drawing = null;
        foreach (var raw in block.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = raw.Trim();
            if (tag.Length < 2)
                continue;
            var name = tag[0];
            var value = tag[1..];
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                continue;
            switch (name)
            {
                case 'i' when value.Length <= 1:
                    builder.Set(TextStyle.Italic, n != 0);
                    break;
                case 'b':
                    builder.Set(TextStyle.Bold, n == 1 || n >= 600);
                    break;
                case 'u' when value.Length <= 1:
                    builder.Set(TextStyle.Underline, n != 0);
                    break;
                case 'p':
                    drawing = n != 0;
                    break;
            }
        }

        return drawing;
    }

    /// <summary>Parses WebVTT cue text (tags and character references).</summary>
    public static StyledText FromWebVtt(string cueText)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        var decoded = cueText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var styled = FromSrt(decoded);
        var text = styled.Text;
        if (!text.Contains('&', StringComparison.Ordinal))
            return styled;

        // Decode entities while keeping the runs aligned.
        var builder = new RunBuilder();
        var runs = styled.Runs;
        for (var i = 0; i < text.Length; i++)
        {
            var style = runs.Where(r => i >= r.Start && i < r.End).Aggregate(TextStyle.None, (s, r) => s | r.Style);
            builder.SetAll(style);
            if (text[i] == '&')
            {
                var semi = text.IndexOf(';', i);
                if (semi > i && semi - i <= 8)
                {
                    var entity = text[(i + 1)..semi];
                    string? replacement = entity switch
                    {
                        "amp" => "&",
                        "lt" => "<",
                        "gt" => ">",
                        "nbsp" => " ",
                        "lrm" => "‎",
                        "rlm" => "‏",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => null,
                    };
                    if (replacement is not null)
                    {
                        foreach (var ch in replacement)
                            builder.Append(ch);
                        i = semi;
                        continue;
                    }
                }
            }

            builder.Append(text[i]);
        }

        return builder.Build();
    }

    /// <summary>Formats styled text as SubRip markup.</summary>
    public static string ToSrt(StyledText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Runs.Count == 0)
            return text.Text;
        var sb = new StringBuilder();
        var current = TextStyle.None;
        for (var i = 0; i <= text.Text.Length; i++)
        {
            var style = i < text.Text.Length ? StyleAt(text, i) : TextStyle.None;
            if (style != current)
            {
                // Close in reverse order, then open.
                foreach (var (flag, tag) in new[] { (TextStyle.Underline, "u"), (TextStyle.Italic, "i"), (TextStyle.Bold, "b") })
                {
                    if ((current & flag) != 0 && (style & flag) == 0)
                        sb.Append("</").Append(tag).Append('>');
                }

                foreach (var (flag, tag) in new[] { (TextStyle.Bold, "b"), (TextStyle.Italic, "i"), (TextStyle.Underline, "u") })
                {
                    if ((current & flag) == 0 && (style & flag) != 0)
                        sb.Append('<').Append(tag).Append('>');
                }

                current = style;
            }

            if (i < text.Text.Length)
                sb.Append(text.Text[i]);
        }

        return sb.ToString();
    }

    private static TextStyle StyleAt(StyledText text, int index)
    {
        var style = TextStyle.None;
        foreach (var r in text.Runs)
        {
            if (index >= r.Start && index < r.End)
                style |= r.Style;
        }

        return style;
    }

    /// <summary>Extracts the Text field of a Matroska ASS/SSA block ("ReadOrder,Layer,Style,Name,…,Effect,Text").</summary>
    public static string AssBlockText(string block, bool ssa)
    {
        ArgumentNullException.ThrowIfNull(block);

        // Matroska ASS blocks have 8 fields before Text; SSA ones have "Marked" in place of Layer, so the same count.
        _ = ssa;
        var commas = 0;
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] == ',' && ++commas == 8)
                return block[(i + 1)..];
        }

        return block;
    }

    /// <summary>
    /// Encodes styled text as a tx3g sample (length-prefixed UTF-8, a 'styl' box, and an empty 'frcd' box when
    /// <see cref="StyledText.Forced"/> is set).
    /// </summary>
    public static byte[] ToTx3g(StyledText text, int fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        var utf8 = Encoding.UTF8.GetBytes(text.Text);
        if (utf8.Length > ushort.MaxValue)
            throw new InvalidDataException("Subtitle text is too long for a tx3g sample.");
        var runs = Normalize(text);
        var styl = runs.Count == 0 ? 0 : 10 + runs.Count * 12;
        var frcd = text.Forced && !text.IsEmpty ? 8 : 0;
        var result = new byte[2 + utf8.Length + styl + frcd];
        if (frcd > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 8), 8);
            "frcd"u8.CopyTo(result.AsSpan(result.Length - 4));
        }

        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)utf8.Length);
        utf8.CopyTo(result, 2);
        if (styl > 0)
        {
            var pos = 2 + utf8.Length;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), (uint)styl);
            "styl"u8.CopyTo(result.AsSpan(pos + 4));
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 8), (ushort)runs.Count);
            pos += 10;
            foreach (var r in runs)
            {
                // tx3g character offsets count Unicode characters, not UTF-16 units.
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)CodePoints(text.Text, r.Start));
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 2), (ushort)CodePoints(text.Text, r.End));
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 4), 1);
                result[pos + 6] = (byte)r.Style;
                result[pos + 7] = (byte)Math.Clamp(fontSize, 1, 255);
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 8), 0xFFFFFFFF);
                pos += 12;
            }
        }

        return result;
    }

    /// <summary>Decodes a tx3g sample (text, 'styl' runs and the 'frcd' forced flag).</summary>
    public static StyledText FromTx3g(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2)
            return StyledText.Empty;
        var len = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sample), sample.Length - 2);
        var bytes = sample.Slice(2, len);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        else
            text = Encoding.UTF8.GetString(bytes);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        var runs = new List<StyleRun>();
        var forced = false;
        var pos = 2 + len;
        while (pos + 8 <= sample.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(sample[pos..]);
            if (size < 8 || pos + size > sample.Length)
                break;
            if (sample.Slice(pos + 4, 4).SequenceEqual("frcd"u8))
                forced = true;
            if (sample.Slice(pos + 4, 4).SequenceEqual("styl"u8) && size >= 10)
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(sample[(pos + 8)..]);
                for (var i = 0; i < count && pos + 10 + i * 12 + 12 <= pos + size; i++)
                {
                    var e = sample[(pos + 10 + i * 12)..];
                    var start = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(e));
                    var end = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(e[2..]));
                    var style = (TextStyle)(e[6] & 7);
                    if (end > start && style != TextStyle.None)
                        runs.Add(new StyleRun(start, end, style));
                }
            }

            pos += size;
        }

        return new StyledText(text, runs) { Forced = forced };
    }

    private static List<StyleRun> Normalize(StyledText text)
    {
        // Split overlapping runs into non-overlapping ones with combined styles.
        var bounds = new SortedSet<int>();
        foreach (var r in text.Runs)
        {
            bounds.Add(Math.Clamp(r.Start, 0, text.Text.Length));
            bounds.Add(Math.Clamp(r.End, 0, text.Text.Length));
        }

        var list = bounds.ToList();
        var result = new List<StyleRun>();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            var style = StyleAt(text, list[i]);
            if (style == TextStyle.None || list[i + 1] <= list[i])
                continue;
            if (result.Count > 0 && result[^1].End == list[i] && result[^1].Style == style)
                result[^1] = result[^1] with { End = list[i + 1] };
            else
                result.Add(new StyleRun(list[i], list[i + 1], style));
        }

        return result;
    }

    private static int CodePoints(string s, int utf16Index)
    {
        var count = 0;
        for (var i = 0; i < utf16Index && i < s.Length; i++)
        {
            if (!char.IsLowSurrogate(s[i]))
                count++;
        }

        return count;
    }

    private static int Utf16Index(string s, int codePoints)
    {
        var i = 0;
        var n = 0;
        while (i < s.Length && n < codePoints)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            n++;
        }

        return i;
    }

    /// <summary>Builds the payload of a tx3g sample entry after the reserved bytes and data reference index.</summary>
    /// <param name="width">Text box width (usually the video width).</param>
    /// <param name="height">Text box height.</param>
    /// <param name="fontSize">Default font size.</param>
    /// <param name="displayFlags">Display flags (forced bits included).</param>
    public static byte[] BuildTx3gEntry(int width, int height, int fontSize, uint displayFlags = 0)
    {
        var p = new byte[30];
        BinaryPrimitives.WriteUInt32BigEndian(p, displayFlags);
        p[4] = 1; // horizontal justification: centre
        p[5] = 0xFF; // vertical justification: bottom
        // background colour (RGBA) transparent: bytes 6..9 = 0
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(10), 0); // box top
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(12), 0); // left
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(14), (short)Math.Clamp(height, 0, short.MaxValue)); // bottom
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(16), (short)Math.Clamp(width, 0, short.MaxValue)); // right
        // default style record: startChar, endChar, fontID, face flags, size, colour
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(18), 0);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), 1);
        p[24] = 0;
        p[25] = (byte)Math.Clamp(fontSize, 1, 255);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(26), 0xFFFFFFFF);
        // ftab: one font, ID 1, "Sans-Serif"
        var name = "Sans-Serif"u8;
        var ftab = new byte[8 + 2 + 2 + 1 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(ftab, (uint)ftab.Length);
        "ftab"u8.CopyTo(ftab.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(ftab.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ftab.AsSpan(10), 1);
        ftab[12] = (byte)name.Length;
        name.CopyTo(ftab.AsSpan(13));
        return [.. p, .. ftab];
    }

    /// <summary>Default tx3g font size for a subtitle canvas height.</summary>
    public static int DefaultFontSize(int height) => height > 0 ? Math.Clamp((int)Math.Round(height * 0.05), 12, 255) : 18;

    private sealed class RunBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly List<StyleRun> _runs = [];
        private TextStyle _style;
        private int _runStart;

        public void Append(char c) => _text.Append(c);

        public void Set(TextStyle flag, bool on) => SetAll(on ? _style | flag : _style & ~flag);

        public void SetAll(TextStyle style)
        {
            if (style == _style)
                return;
            Close();
            _style = style;
        }

        private void Close()
        {
            if (_style != TextStyle.None && _text.Length > _runStart)
                _runs.Add(new StyleRun(_runStart, _text.Length, _style));
            _runStart = _text.Length;
        }

        public StyledText Build()
        {
            Close();
            var text = _text.ToString();

            // Trim trailing/leading newlines and spaces while keeping runs inside the text.
            var start = 0;
            while (start < text.Length && text[start] is '\n' or ' ')
                start++;
            var end = text.Length;
            while (end > start && text[end - 1] is '\n' or ' ')
                end--;
            var trimmed = text[start..end];
            var runs = _runs
                .Select(r => new StyleRun(Math.Clamp(r.Start - start, 0, trimmed.Length), Math.Clamp(r.End - start, 0, trimmed.Length), r.Style))
                .Where(r => r.End > r.Start)
                .ToList();
            return new StyledText(trimmed, runs);
        }
    }
}

/// <summary>A subtitle cue on a millisecond-free timeline (units are the caller's).</summary>
public sealed record SubtitleCue(long Start, long End, StyledText Text);

/// <summary>
/// Turns possibly overlapping cues (fed in start order) into the contiguous, non-overlapping sample sequence
/// tx3g requires: overlapping cues are merged into one sample per interval, and gaps become empty samples.
/// </summary>
public sealed class SubtitleTimeline
{
    private readonly List<SubtitleCue> _active = [];
    private long _position;
    private bool _started;

    /// <summary>Adds a cue and returns the samples that are complete before its start.</summary>
    public List<SubtitleCue> Add(SubtitleCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var output = new List<SubtitleCue>();
        if (!_started)
        {
            _started = true;
            _position = 0;
        }

        var start = Math.Max(cue.Start, _position);
        Flush(start, output);
        if (cue.End > start && !cue.Text.IsEmpty)
            _active.Add(cue with { Start = start });
        return output;
    }

    /// <summary>Emits the remaining samples.</summary>
    public List<SubtitleCue> Complete()
    {
        var output = new List<SubtitleCue>();
        Flush(long.MaxValue, output);
        return output;
    }

    private void Flush(long until, List<SubtitleCue> output)
    {
        while (_position < until)
        {
            if (_active.Count == 0)
            {
                if (until == long.MaxValue)
                    return;
                output.Add(new SubtitleCue(_position, until, StyledText.Empty));
                _position = until;
                return;
            }

            var nextEnd = Math.Min(_active.Min(c => c.End), until);
            if (nextEnd > _position)
                output.Add(new SubtitleCue(_position, nextEnd, StyledText.Join(_active.Select(c => c.Text))));
            _position = Math.Max(_position, nextEnd);
            _active.RemoveAll(c => c.End <= _position);
        }
    }
}
