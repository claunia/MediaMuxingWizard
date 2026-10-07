using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMW.Core.Media.Subtitles;

/// <summary>
/// SubRip cue text: &lt;b&gt;, &lt;i&gt;, &lt;u&gt;, &lt;s&gt;, &lt;font color face size&gt; and the {\anN} alignment players honour.
/// Positions and karaoke have no SubRip form (they are dropped when writing).
/// </summary>
public static partial class SrtFormat
{
    /// <summary>An event from SubRip cue text.</summary>
    public static SubtitleEvent Parse(string markup, SubtitleEvent template)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(template);
        var b = new EventBuilder();
        var text = markup.Replace("\r\n", "\n", StringComparison.Ordinal);
        var fonts = new Stack<SubtitleStyle>();
        var e = template;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<' && text.IndexOf('>', i + 1) is var close && close > i)
            {
                var tag = text[(i + 1)..close].Trim();
                var lower = tag.ToLowerInvariant();
                var style = b.Style;
                var handled = true;
                switch (lower)
                {
                    case "b" or "/b":
                        style = style with { Bold = lower == "b" ? true : null };
                        break;
                    case "i" or "/i":
                        style = style with { Italic = lower == "i" ? true : null };
                        break;
                    case "u" or "/u":
                        style = style with { Underline = lower == "u" ? true : null };
                        break;
                    case "s" or "/s":
                        style = style with { Strikeout = lower == "s" ? true : null };
                        break;
                    case "/font":
                        style = fonts.Count > 0 ? fonts.Pop() : style with { Font = null, Primary = null, Size = null };
                        break;
                    default:
                        if (lower.StartsWith("font", StringComparison.Ordinal))
                        {
                            fonts.Push(style);
                            foreach (Match m in AttributeRegex().Matches(tag))
                            {
                                var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                                switch (m.Groups[1].Value.ToLowerInvariant())
                                {
                                    case "color" when SubtitleColor.Parse(value) is { } color:
                                        style = style with { Primary = color };
                                        break;
                                    case "face" when value.Length > 0:
                                        style = style with { Font = value };
                                        break;
                                }
                            }
                        }
                        else
                        {
                            handled = false;
                        }

                        break;
                }

                if (handled)
                {
                    b.SetStyle(style);
                    i = close;
                    continue;
                }
            }

            if (c == '{' && i + 1 < text.Length && text[i + 1] == '\\' && text.IndexOf('}', i + 1) is var end && end > i)
            {
                // ASS-style overrides some SubRip files use: the alignment is kept, the rest is dropped.
                if (AlignmentRegex().Match(text[i..end]) is { Success: true } an)
                    e = e with { Alignment = an.Groups[1].Value[0] - '0' };
                i = end;
                continue;
            }

            b.Append(c);
        }

        return b.Build(e);
    }

    /// <summary>SubRip cue text for <paramref name="e"/>: style tags against its style sheet, {\anN} when not bottom centre.</summary>
    public static string Write(SubtitleEvent e, SubtitleScript script)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        var sheet = script.Style(e.StyleName);
        var defaults = script.Style(null).Style;
        var sb = new StringBuilder();
        var alignment = e.Alignment ?? sheet.Alignment;
        if (alignment != 2)
            sb.Append(CultureInfo.InvariantCulture, $"{{\\an{alignment}}}");
        var open = new List<string>();
        SubtitleStyle? current = null;
        for (var i = 0; i <= e.Text.Length; i++)
        {
            var style = i < e.Text.Length ? script.StyleAt(e, i) : null;
            if (style != current)
            {
                for (var k = open.Count - 1; k >= 0; k--)
                    sb.Append("</").Append(open[k]).Append('>');
                open.Clear();
                if (style is not null)
                {
                    var font = new StringBuilder();
                    if (style.Primary is { } color && color != defaults.Primary)
                        font.Append(CultureInfo.InvariantCulture, $" color=\"{color.Hex}\"");
                    if (style.Font is { } face && face != defaults.Font)
                        font.Append(" face=\"").Append(face.Replace("\"", string.Empty, StringComparison.Ordinal)).Append('"');
                    if (font.Length > 0)
                    {
                        sb.Append("<font").Append(font).Append('>');
                        open.Add("font");
                    }

                    foreach (var (on, tag) in new[] { (style.Bold, "b"), (style.Italic, "i"), (style.Underline, "u"), (style.Strikeout, "s") })
                    {
                        if (on == true)
                        {
                            sb.Append('<').Append(tag).Append('>');
                            open.Add(tag);
                        }
                    }
                }

                current = style;
            }

            if (i < e.Text.Length)
                sb.Append(e.Text[i] == ' ' ? ' ' : e.Text[i]);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"(\w+)\s*=\s*(?:""([^""]*)""|'?([^\s'>]*)'?)")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"\\an([1-9])")]
    private static partial Regex AlignmentRegex();
}
