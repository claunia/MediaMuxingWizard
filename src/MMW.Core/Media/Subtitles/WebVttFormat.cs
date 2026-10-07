using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMW.Core.Media.Subtitles;

/// <summary>
/// WebVTT: the header's STYLE blocks (::cue and ::cue(.class) rules: colour, background, font, size, weight, style,
/// decoration), cue text (&lt;b&gt;, &lt;i&gt;, &lt;u&gt;, &lt;c.class&gt;, &lt;v&gt;, timestamps for karaoke, entities) and cue
/// settings (vertical, line, position, align). Classes carry what WebVTT has no tag for, so exact colours, fonts and
/// strike-through survive.
/// </summary>
public static partial class WebVttFormat
{
    /// <summary>Canvas WebVTT percentages are mapped onto.</summary>
    public const int CanvasWidth = 1920;

    public const int CanvasHeight = 1080;

    /// <summary>A parsed WebVTT header: the script (Default style from ::cue) and the styles of its classes.</summary>
    public sealed record Header(SubtitleScript Script, IReadOnlyDictionary<string, SubtitleStyle> Classes);

    // ------------------------------------------------------------------ reading

    /// <summary>The ::cue rules of a WebVTT header (the text before the first cue).</summary>
    public static Header ReadHeader(string? header)
    {
        var script = new SubtitleScript { Width = CanvasWidth, Height = CanvasHeight };
        var defaults = SubtitleStyleSheet.DefaultStyle(CanvasHeight * 0.05) with { Back = new SubtitleColor(0, 0, 0, 204) };
        var classes = new Dictionary<string, SubtitleStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (Match rule in RuleRegex().Matches(header ?? string.Empty))
        {
            var style = Declarations(rule.Groups[2].Value);
            var selector = rule.Groups[1].Value.Trim();
            if (selector.Length == 0)
            {
                defaults = style.Over(defaults);
            }
            else if (selector.StartsWith('.'))
            {
                foreach (var name in selector.Split('.', StringSplitOptions.RemoveEmptyEntries))
                    classes[name] = style.Over(classes.GetValueOrDefault(name));
            }
        }

        script.Styles.Add(new SubtitleStyleSheet { Style = defaults, MarginV = (int)(CanvasHeight * 0.05), MarginL = (int)(CanvasWidth * 0.05), MarginR = (int)(CanvasWidth * 0.05), BorderStyle = 3 });
        return new Header(script, classes);
    }

    private static SubtitleStyle Declarations(string css)
    {
        var s = new SubtitleStyle();
        foreach (var declaration in css.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim().Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            switch (name)
            {
                case "color":
                    s = s with { Primary = SubtitleColor.Parse(value) ?? s.Primary };
                    break;
                case "background-color" or "background":
                    s = s with { Back = SubtitleColor.Parse(value.Split(' ')[0]) ?? s.Back };
                    break;
                case "font-family":
                    s = s with { Font = value.Split(',')[0].Trim().Trim('"', '\'') };
                    break;
                case "font-size" when Size(value) is { } size:
                    s = s with { Size = size };
                    break;
                case "font-weight":
                    s = s with { Bold = value is "bold" or "bolder" || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 600 };
                    break;
                case "font-style":
                    s = s with { Italic = value is "italic" or "oblique" };
                    break;
                case "text-decoration" or "text-decoration-line":
                    s = s with { Underline = value.Contains("underline", StringComparison.Ordinal), Strikeout = value.Contains("line-through", StringComparison.Ordinal) };
                    break;
                case "outline-color" or "text-shadow" when SubtitleColor.Parse(value.Split(' ').LastOrDefault()) is { } outline:
                    s = s with { Outline = outline };
                    break;
            }
        }

        return s;
    }

    private static double? Size(string value)
    {
        double Number(string t) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (value.EndsWith("px", StringComparison.Ordinal))
            return Number(value[..^2]) is > 0 and var px ? px * CanvasHeight / 720.0 : null; // pixels of a 720p rendering
        if (value.EndsWith("em", StringComparison.Ordinal))
            return Number(value[..^2]) is > 0 and var em ? em * CanvasHeight * 0.05 : null;
        if (value.EndsWith('%'))
            return Number(value[..^1]) is > 0 and var pc ? pc / 100 * CanvasHeight * 0.05 : null;
        if (value.EndsWith("vh", StringComparison.Ordinal))
            return Number(value[..^2]) is > 0 and var vh ? vh / 100 * CanvasHeight : null;
        return null;
    }

    /// <summary>An event from WebVTT cue text and settings, shown from <paramref name="start"/> to <paramref name="end"/> ms.</summary>
    public static SubtitleEvent Parse(string cueText, string? settings, Header header, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        ArgumentNullException.ThrowIfNull(header);
        var b = new EventBuilder();
        var e = ApplySettings(new SubtitleEvent { Start = start, End = end }, settings, header.Script);
        var stack = new Stack<SubtitleStyle>();
        var text = cueText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var ruby = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<' && text.IndexOf('>', i + 1) is var close && close > i)
            {
                var tag = text[(i + 1)..close].Trim();
                i = close;
                if (tag.Length == 0)
                    continue;
                if (char.IsDigit(tag[0]))
                {
                    if (ParseTime(tag) is { } at && at >= start)
                        b.SyllableAt(at - start, KaraokeKind.Fill);
                    continue;
                }

                if (tag[0] == '/')
                {
                    var closing = tag[1..].Trim().ToLowerInvariant();
                    if (closing == "rt")
                        ruby = Math.Max(0, ruby - 1);
                    else if (closing is not "ruby" && stack.Count > 0)
                        b.SetStyle(stack.Pop());
                    continue;
                }

                var name = tag.Split([' ', '.', '\t'], 2)[0].ToLowerInvariant();
                if (name == "rt")
                {
                    ruby++;
                    continue;
                }

                if (name is "ruby")
                    continue;
                stack.Push(b.Style);
                var style = b.Style;
                switch (name)
                {
                    case "b":
                        style = style with { Bold = true };
                        break;
                    case "i":
                        style = style with { Italic = true };
                        break;
                    case "u":
                        style = style with { Underline = true };
                        break;
                    case "v" when tag.Length > 2 && e.Speaker.Length == 0:
                        e = e with { Speaker = tag[1..].Split(' ', 2).Last().Trim() };
                        break;
                }

                // Classes: .c1.c2 after the tag name (any tag may have them).
                var dot = tag.IndexOf('.');
                if (dot >= 0)
                {
                    var space = tag.IndexOf(' ', dot);
                    foreach (var cls in tag[(dot + 1)..(space > dot ? space : tag.Length)].Split('.', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (header.Classes.TryGetValue(cls, out var classStyle))
                            style = classStyle.Over(style);
                        else if (SubtitleColor.Parse(cls) is { } color)
                            style = style with { Primary = color }; // the colour classes players style by default
                        else if (cls.StartsWith("bg_", StringComparison.OrdinalIgnoreCase) && SubtitleColor.Parse(cls[3..]) is { } back)
                            style = style with { Back = back };
                    }
                }

                b.SetStyle(style);
                continue;
            }

            if (ruby > 0)
                continue;
            if (c == '&' && text.IndexOf(';', i + 1) is var semi && semi > i && semi - i <= 8)
            {
                var entity = text[(i + 1)..semi];
                var decoded = entity switch
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
                if (decoded is not null)
                {
                    b.Append(decoded);
                    i = semi;
                    continue;
                }
            }

            b.Append(c);
        }

        return b.Build(e, end - start);
    }

    /// <summary>Alignment, position and direction from cue settings.</summary>
    private static SubtitleEvent ApplySettings(SubtitleEvent e, string? settings, SubtitleScript script)
    {
        if (string.IsNullOrWhiteSpace(settings))
            return e;
        var sheet = script.Style(null);
        int column = 2, row = 1; // row: 1 bottom, 2 middle, 3 top
        double? x = null, y = null;
        foreach (var setting in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = setting.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = setting[..colon];
            var value = setting[(colon + 1)..];
            var parts = value.Split(',');
            switch (name)
            {
                case "vertical":
                    e = e with { Vertical = value is "rl" or "lr" };
                    break;
                case "align":
                    column = value switch
                    {
                        "start" or "left" => 1,
                        "end" or "right" => 3,
                        _ => 2,
                    };
                    break;
                case "position" when Percent(parts[0]) is { } p:
                    x = p * script.Width;
                    if (parts.Length > 1)
                        column = parts[1] switch
                        {
                            "line-left" => 1,
                            "line-right" => 3,
                            _ => column,
                        };
                    break;
                case "line":
                    if (Percent(parts[0]) is { } l)
                    {
                        y = l * script.Height;
                        row = parts.Length > 1 ? parts[1] switch
                        {
                            "center" => 2,
                            "end" => 1,
                            _ => 3,
                        } : 3;
                    }
                    else if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
                    {
                        var lineHeight = (sheet.Style.Size ?? script.Height * 0.05) * 1.2;
                        row = line >= 0 ? 3 : 1;
                        e = e with { MarginV = (int)Math.Round((line >= 0 ? line : -line - 1) * lineHeight + sheet.MarginV) };
                    }

                    break;
            }
        }

        var alignment = (row - 1) * 3 + column;
        if (x is not null || y is not null)
        {
            var px = x ?? column switch
            {
                1 => sheet.MarginL,
                3 => script.Width - sheet.MarginR,
                _ => script.Width / 2.0,
            };
            var py = y ?? script.Height - sheet.MarginV;
            e = e with { Position = (Math.Round(px, 1), Math.Round(py, 1)) };
        }

        return e with { Alignment = alignment };
    }

    private static double? Percent(string text) =>
        text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v / 100, 0, 1) : null;

    /// <summary>A WebVTT timestamp ("hh:mm:ss.ttt" or "mm:ss.ttt") in milliseconds.</summary>
    public static long? ParseTime(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return null;
        var seconds = parts[^1].Split('.');
        if (!long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ||
            !long.TryParse(seconds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return null;
        long h = 0;
        if (parts.Length == 3 && !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
            return null;
        var ms = seconds.Length > 1 && long.TryParse(seconds[1].PadRight(3, '0')[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        return ((h * 60 + m) * 60 + s) * 1000 + ms;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// The style classes a script needs, keyed by the formatting they stand for: everything WebVTT has no tag for
    /// (colours, fonts, sizes, backgrounds, strike-through), relative to the Default style.
    /// </summary>
    public static Dictionary<SubtitleStyle, string> Classes(SubtitleScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var defaults = script.Style(null).Style;
        var classes = new Dictionary<SubtitleStyle, string>();
        foreach (var e in script.Events)
        {
            for (var i = 0; i < e.Text.Length; i++)
            {
                var key = ClassKey(script.StyleAt(e, i), defaults);
                if (!key.IsEmpty && !classes.ContainsKey(key))
                    classes[key] = "s" + (classes.Count + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        return classes;
    }

    private static SubtitleStyle ClassKey(SubtitleStyle style, SubtitleStyle defaults)
    {
        var d = style.Except(defaults);
        return new SubtitleStyle { Font = d.Font, Size = d.Size, Primary = d.Primary, Back = d.Back, Strikeout = d.Strikeout == true ? true : null };
    }

    /// <summary>The WebVTT header: "WEBVTT", then a STYLE block with the Default style (::cue) and the classes.</summary>
    public static string WriteHeader(SubtitleScript script, IReadOnlyDictionary<SubtitleStyle, string> classes)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(classes);
        var sb = new StringBuilder("WEBVTT\n\nSTYLE\n::cue {");
        sb.Append(Css(script.Style(null).Style, script.Height, includeDefaults: true)).Append(" }\n");
        foreach (var (style, name) in classes)
            sb.Append("::cue(.").Append(name).Append(") {").Append(Css(style, script.Height, includeDefaults: false)).Append(" }\n");
        return sb.ToString().TrimEnd('\n');
    }

    private static string Css(SubtitleStyle s, int canvasHeight, bool includeDefaults)
    {
        var sb = new StringBuilder();
        if (s.Font is { } font)
            sb.Append(" font-family: \"").Append(font.Replace("\"", string.Empty, StringComparison.Ordinal)).Append("\";");
        if (s.Size is { } size)
            sb.Append(CultureInfo.InvariantCulture, $" font-size: {size / canvasHeight * 100:0.##}vh;");
        if (s.Primary is { } color)
            sb.Append(" color: ").Append(Rgba(color)).Append(';');
        if (s.Back is { } back)
            sb.Append(" background-color: ").Append(Rgba(back)).Append(';');
        if (includeDefaults && s.Bold == true)
            sb.Append(" font-weight: bold;");
        if (includeDefaults && s.Italic == true)
            sb.Append(" font-style: italic;");
        if (s.Strikeout == true || includeDefaults && s.Underline == true)
            sb.Append(" text-decoration: ").Append(s.Strikeout == true ? "line-through" : "underline").Append(';');
        return sb.ToString();
    }

    private static string Rgba(SubtitleColor c) => c.A == 255
        ? c.Hex
        : string.Create(CultureInfo.InvariantCulture, $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:0.###})");

    /// <summary>The cue text of <paramref name="e"/>: tags for bold/italic/underline, classes for the rest, karaoke timestamps.</summary>
    public static string WriteText(SubtitleEvent e, SubtitleScript script, IReadOnlyDictionary<SubtitleStyle, string> classes)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(classes);
        var defaults = script.Style(null).Style;
        var sb = new StringBuilder();
        if (e.Speaker.Length > 0)
            sb.Append("<v ").Append(Escape(e.Speaker)).Append('>');
        var karaoke = e.Karaoke.Where(k => k.StartMs > 0).ToDictionary(k => k.Start, k => k.StartMs);
        var open = new List<string>();
        SubtitleStyle? current = null;
        for (var i = 0; i <= e.Text.Length; i++)
        {
            var style = i < e.Text.Length ? script.StyleAt(e, i) : null;
            var stamp = i < e.Text.Length && karaoke.TryGetValue(i, out var at) ? (long?)at : null;
            if (style != current || stamp is not null)
            {
                for (var k = open.Count - 1; k >= 0; k--)
                    sb.Append("</").Append(open[k]).Append('>');
                open.Clear();
                if (stamp is { } ms)
                    sb.Append('<').Append(FormatTime(e.Start + ms)).Append('>');
                if (style is not null)
                {
                    if (classes.TryGetValue(ClassKey(style, defaults), out var cls))
                    {
                        sb.Append("<c.").Append(cls).Append('>');
                        open.Add("c");
                    }

                    foreach (var (on, baseOn, tag) in new[] { (style.Bold, defaults.Bold, "b"), (style.Italic, defaults.Italic, "i"), (style.Underline, defaults.Underline, "u") })
                    {
                        if (on == true && baseOn != true)
                        {
                            sb.Append('<').Append(tag).Append('>');
                            open.Add(tag);
                        }
                    }
                }

                current = style;
            }

            if (i < e.Text.Length)
                sb.Append(Escape(e.Text[i].ToString()));
        }

        if (e.Speaker.Length > 0)
            sb.Append("</v>");
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace(" ", "&nbsp;", StringComparison.Ordinal);

    /// <summary>The cue settings for <paramref name="e"/>'s alignment, position and direction; null for a bottom-centred cue.</summary>
    public static string? WriteSettings(SubtitleEvent e, SubtitleScript script)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        var sheet = script.Style(e.StyleName);
        var alignment = e.Alignment ?? sheet.Alignment;
        var column = (alignment - 1) % 3;
        var row = (alignment - 1) / 3; // 0 bottom, 1 middle, 2 top
        var parts = new List<string>();
        if (e.Vertical ?? sheet.Vertical)
            parts.Add("vertical:rl");
        if (e.Position is { } p)
        {
            var anchor = column switch
            {
                0 => "line-left",
                2 => "line-right",
                _ => "center",
            };
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"position:{p.X / script.Width * 100:0.##}%,{anchor}"));
            var lineAlign = row switch
            {
                2 => "start",
                1 => "center",
                _ => "end",
            };
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"line:{p.Y / script.Height * 100:0.##}%,{lineAlign}"));
        }
        else if (row == 2)
        {
            parts.Add("line:0");
        }
        else if (row == 1)
        {
            parts.Add("line:50%,center");
        }

        if (column != 1)
            parts.Add(column == 0 ? "align:start" : "align:end");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>"hh:mm:ss.ttt".</summary>
    public static string FormatTime(long ms)
    {
        ms = Math.Max(0, ms);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    [GeneratedRegex(@"::cue(?:\(([^)]*)\))?\s*\{([^}]*)\}")]
    private static partial Regex RuleRegex();
}
