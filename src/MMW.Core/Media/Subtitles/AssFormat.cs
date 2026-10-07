using System.Globalization;
using System.Text;

namespace MMW.Core.Media.Subtitles;

/// <summary>
/// Advanced SubStation Alpha (v4.00+) and SubStation Alpha (v4.00): script headers with styles, and events in the
/// Matroska block form ("ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text"; SSA has Marked for Layer).
/// </summary>
public static class AssFormat
{
    private const string AssStyleFormat =
        "Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";

    private const string SsaStyleFormat =
        "Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding";

    // ------------------------------------------------------------------ reading

    /// <summary>The canvas (PlayResX/Y) and styles of a script header; events are added by <see cref="ParseBlock"/>.</summary>
    public static SubtitleScript ReadHeader(string? header)
    {
        var script = new SubtitleScript();
        if (string.IsNullOrEmpty(header))
        {
            script.Styles.Add(new SubtitleStyleSheet { Style = SubtitleStyleSheet.DefaultStyle(20) });
            return script;
        }

        int playX = 0, playY = 0;
        string[]? format = null;
        var section = string.Empty;
        foreach (var raw in header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.ToLowerInvariant();
                format = null;
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (section == "[script info]")
            {
                if (key.Equals("PlayResX", StringComparison.OrdinalIgnoreCase))
                    playX = Int(value);
                else if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase))
                    playY = Int(value);
            }
            else if (section.Contains("styles", StringComparison.Ordinal))
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                    format = value.Split(',', StringSplitOptions.TrimEntries);
                else if (key.Equals("Style", StringComparison.OrdinalIgnoreCase))
                    script.Styles.Add(ParseStyle(value, format ?? (section.Contains("v4+", StringComparison.Ordinal) ? AssStyleFormat : SsaStyleFormat).Split(',', StringSplitOptions.TrimEntries)));
            }
        }

        // VSFilter's rules for missing play resolutions.
        if (playX <= 0 && playY <= 0)
            (playX, playY) = (384, 288);
        else if (playX <= 0)
            playX = playY == 1024 ? 1280 : playY * 4 / 3;
        else if (playY <= 0)
            playY = playX == 1280 ? 1024 : playX * 3 / 4;
        script.Width = playX;
        script.Height = playY;
        if (script.Styles.Count == 0)
            script.Styles.Add(new SubtitleStyleSheet { Style = SubtitleStyleSheet.DefaultStyle(20) });
        return script;
    }

    private static SubtitleStyleSheet ParseStyle(string value, string[] format)
    {
        // The name may not contain commas, the last field (Encoding) neither; split exactly.
        var fields = value.Split(',', format.Length);
        string Field(string name)
        {
            var i = Array.FindIndex(format, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < fields.Length ? fields[i].Trim() : string.Empty;
        }

        var font = Field("Fontname");
        var vertical = font.StartsWith('@');
        if (vertical)
            font = font[1..];
        var ssa = format.Any(f => f.Equals("TertiaryColour", StringComparison.OrdinalIgnoreCase));
        var alpha = ssa ? Int(Field("AlphaLevel")) : 0;
        SubtitleColor Color(string name, SubtitleColor fallback) => ParseColor(Field(name), alpha) ?? fallback;
        var alignment = Int(Field("Alignment"), 2);
        return new SubtitleStyleSheet
        {
            Name = Field("Name") is { Length: > 0 } n ? n : "Default",
            Style = new SubtitleStyle
            {
                Font = font.Length > 0 ? font : "Arial",
                Size = Double(Field("Fontsize"), 20),
                Primary = Color("PrimaryColour", SubtitleColor.White),
                Secondary = Color("SecondaryColour", new SubtitleColor(255, 0, 0)),
                Outline = Color(ssa ? "TertiaryColour" : "OutlineColour", SubtitleColor.Black),
                Back = Color("BackColour", new SubtitleColor(0, 0, 0, 128)),
                Bold = Flag(Field("Bold")),
                Italic = Flag(Field("Italic")),
                Underline = Flag(Field("Underline")),
                Strikeout = Flag(Field("StrikeOut")),
                Blink = false,
            },
            Alignment = ssa ? FromLegacyAlignment(alignment) : Math.Clamp(alignment, 1, 9),
            MarginL = Int(Field("MarginL"), 10),
            MarginR = Int(Field("MarginR"), 10),
            MarginV = Int(Field("MarginV"), 10),
            BorderStyle = Int(Field("BorderStyle"), 1),
            OutlineWidth = Double(Field("Outline"), 2),
            Shadow = Double(Field("Shadow"), 2),
            Vertical = vertical,
        };
    }

    /// <summary>
    /// An event from a Matroska ASS/SSA block (the ReadOrder field first, no times) shown from <paramref name="start"/>
    /// to <paramref name="end"/> milliseconds.
    /// </summary>
    public static SubtitleEvent ParseBlock(string block, SubtitleScript script, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(script);
        var fields = block.Split(',', 9);
        if (fields.Length < 9)
            return ParseText(block, new SubtitleEvent { Start = start, End = end }, script);
        var template = new SubtitleEvent
        {
            Start = start,
            End = end,
            Layer = Int(fields[1]),
            StyleName = fields[2].Trim().TrimStart('*') is { Length: > 0 } s ? s : "Default",
            Speaker = fields[3].Trim(),
            MarginL = Int(fields[4]) is > 0 and var l ? l : null,
            MarginR = Int(fields[5]) is > 0 and var r ? r : null,
            MarginV = Int(fields[6]) is > 0 and var v ? v : null,
            Effect = fields[7].Trim(),
        };
        return ParseText(fields[8], template, script);
    }

    /// <summary>An event from the Text field of an ASS/SSA event (override blocks, \N, \n, \h).</summary>
    public static SubtitleEvent ParseText(string text, SubtitleEvent template, SubtitleScript script)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(script);
        var b = new EventBuilder();
        var state = new OverrideState(template, script);
        var drawing = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' && text.IndexOf('}', i + 1) is var close && close > i)
            {
                var block = text[(i + 1)..close];
                if (block.Contains('\\', StringComparison.Ordinal))
                {
                    drawing = state.Apply(block, b, drawing);
                    i = close;
                    continue;
                }

                i = close; // a comment
                continue;
            }

            if (drawing)
                continue;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n' or 'h')
            {
                b.Append(text[i + 1] == 'h' ? ' ' : '\n');
                i++;
                continue;
            }

            b.Append(c);
        }

        return b.Build(state.Event);
    }

    /// <summary>The effect of override tags on the event and the text after them.</summary>
    private sealed class OverrideState(SubtitleEvent template, SubtitleScript script)
    {
        public SubtitleEvent Event { get; private set; } = template;

        public bool Apply(string block, EventBuilder b, bool drawing)
        {
            var style = b.Style;
            foreach (var tag in Tags(block))
            {
                if (tag.Length == 0)
                    continue;
                if (tag.StartsWith("fn", StringComparison.Ordinal))
                {
                    var font = tag[2..].Trim();
                    if (font.StartsWith('@'))
                    {
                        Event = Event with { Vertical = true };
                        font = font[1..];
                    }

                    style = style with { Font = font.Length > 0 ? font : null };
                }
                else if (tag.StartsWith("fsc", StringComparison.Ordinal) || tag.StartsWith("fsp", StringComparison.Ordinal) || tag.StartsWith("fad", StringComparison.Ordinal) ||
                         tag.StartsWith("fr", StringComparison.Ordinal) || tag.StartsWith("fe", StringComparison.Ordinal))
                {
                    // scaling, spacing, fades, rotation, encoding: no other format has them
                }
                else if (tag.StartsWith("fs", StringComparison.Ordinal))
                {
                    if (tag.Length > 2 && tag[2] is not ('+' or '-') && Double(tag[2..], 0) is > 0 and var size)
                        style = style with { Size = size };
                    else if (tag.Length == 2)
                        style = style with { Size = null };
                }
                else if (tag.StartsWith("bord", StringComparison.Ordinal) || tag.StartsWith("blur", StringComparison.Ordinal) || tag.StartsWith("be", StringComparison.Ordinal))
                {
                }
                else if (tag[0] == 'b' && tag.Length > 1 && char.IsDigit(tag[1]))
                {
                    var weight = Int(tag[1..]);
                    style = style with { Bold = weight == 1 || weight >= 600 };
                }
                else if (tag.StartsWith("shad", StringComparison.Ordinal) || tag.StartsWith("xbord", StringComparison.Ordinal) || tag.StartsWith("ybord", StringComparison.Ordinal) ||
                         tag.StartsWith("xshad", StringComparison.Ordinal) || tag.StartsWith("yshad", StringComparison.Ordinal))
                {
                }
                else if (tag[0] is 'i' or 'u' or 's' && tag.Length == 2 && tag[1] is '0' or '1')
                {
                    var on = tag[1] == '1';
                    style = tag[0] switch
                    {
                        'i' => style with { Italic = on },
                        'u' => style with { Underline = on },
                        _ => style with { Strikeout = on },
                    };
                }
                else if (tag.StartsWith("an", StringComparison.Ordinal) && tag.Length == 3 && tag[2] is >= '1' and <= '9')
                {
                    Event = Event with { Alignment = tag[2] - '0' };
                }
                else if (tag.StartsWith("alpha", StringComparison.Ordinal))
                {
                    if (ParseAlpha(tag[5..]) is { } a)
                    {
                        var s = style.Over(script.Style(Event.StyleName).Style);
                        style = style with
                        {
                            Primary = s.Primary!.Value with { A = a }, Secondary = s.Secondary!.Value with { A = a },
                            Outline = s.Outline!.Value with { A = a }, Back = s.Back!.Value with { A = a },
                        };
                    }
                }
                else if (tag[0] == 'a' && tag.Length > 1 && char.IsDigit(tag[1]))
                {
                    Event = Event with { Alignment = FromLegacyAlignment(Int(tag[1..])) };
                }
                else if (tag.Length >= 2 && tag[0] is '1' or '2' or '3' or '4' && tag[1] is 'c' or 'a' || tag[0] == 'c' && (tag.Length == 1 || tag[1] is '&' or 'H' or 'h'))
                {
                    var which = tag[0] == 'c' ? '1' : tag[0];
                    var isAlpha = tag[0] != 'c' && tag[1] == 'a';
                    var arg = tag[(tag[0] == 'c' ? 1 : 2)..];
                    var current = style.Over(script.Style(Event.StyleName).Style);
                    SubtitleColor Pick(char w) => w switch
                    {
                        '1' => current.Primary!.Value,
                        '2' => current.Secondary!.Value,
                        '3' => current.Outline!.Value,
                        _ => current.Back!.Value,
                    };
                    SubtitleColor? value;
                    if (isAlpha)
                        value = ParseAlpha(arg) is { } a ? Pick(which) with { A = a } : null;
                    else
                        value = arg.Length == 0 ? null : ParseColor(arg, 0) is { } c ? c with { A = Pick(which).A } : null;
                    style = which switch
                    {
                        '1' => style with { Primary = value },
                        '2' => style with { Secondary = value },
                        '3' => style with { Outline = value },
                        _ => style with { Back = value },
                    };
                }
                else if (tag.StartsWith("pos(", StringComparison.Ordinal) || tag.StartsWith("move(", StringComparison.Ordinal))
                {
                    var args = Arguments(tag);
                    if (args.Length >= 2)
                        Event = Event with { Position = (Double(args[0], 0), Double(args[1], 0)) };
                }
                else if (tag.StartsWith("kf", StringComparison.Ordinal) || tag.StartsWith("ko", StringComparison.Ordinal) || tag[0] is 'k' or 'K')
                {
                    var kind = tag[0] == 'K' || tag.StartsWith("kf", StringComparison.Ordinal) ? KaraokeKind.Sweep
                        : tag.StartsWith("ko", StringComparison.Ordinal) ? KaraokeKind.Outline
                        : KaraokeKind.Fill;
                    var digits = tag.TrimStart('k', 'K', 'f', 'o');
                    b.SetStyle(style);
                    b.Syllable(Int(digits) * 10L, kind);
                }
                else if (tag[0] == 'r' && !tag.StartsWith("rnd", StringComparison.Ordinal))
                {
                    var name = tag[1..].Trim();
                    if (name.Length > 0 && script.Styles.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        style = script.Style(name).Style.Except(script.Style(Event.StyleName).Style);
                    else
                        style = new SubtitleStyle();
                }
                else if (tag[0] == 'p' && tag.Length > 1 && char.IsDigit(tag[1]))
                {
                    drawing = Int(tag[1..]) > 0;
                }
            }

            b.SetStyle(style);
            return drawing;
        }
    }

    /// <summary>The tags of an override block (split at backslashes outside parentheses).</summary>
    private static IEnumerable<string> Tags(string block)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i <= block.Length; i++)
        {
            var c = i < block.Length ? block[i] : '\\';
            if (c == '(')
                depth++;
            else if (c == ')')
                depth = Math.Max(0, depth - 1);
            if (c == '\\' && depth == 0)
            {
                if (start >= 0)
                    yield return block[start..i].Trim();
                start = i + 1;
            }
        }
    }

    private static string[] Arguments(string tag)
    {
        var open = tag.IndexOf('(');
        var close = tag.LastIndexOf(')');
        return open < 0 ? [] : tag[(open + 1)..(close > open ? close : tag.Length)].Split(',', StringSplitOptions.TrimEntries);
    }

    /// <summary>An ASS colour (&amp;HAABBGGRR&amp;, &amp;HBBGGRR&amp;, or a decimal BGR value) with SSA's <paramref name="alphaLevel"/>.</summary>
    public static SubtitleColor? ParseColor(string text, int alphaLevel)
    {
        var t = text.Trim().Trim('&').TrimEnd('&');
        uint value;
        var hasAlpha = false;
        if (t.StartsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            var hex = t[1..].TrimEnd('&');
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return null;
            hasAlpha = hex.Length > 6;
        }
        else if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            return null;
        }
        else
        {
            value = unchecked((uint)dec);
        }

        var transparency = hasAlpha ? (byte)(value >> 24) : (byte)Math.Clamp(alphaLevel, 0, 255);
        return new SubtitleColor((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(255 - transparency));
    }

    private static byte? ParseAlpha(string text)
    {
        var t = text.Trim().Trim('&');
        if (t.StartsWith("H", StringComparison.OrdinalIgnoreCase))
            t = t[1..];
        return byte.TryParse(t.TrimEnd('&'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var transparency) ? (byte)(255 - transparency) : null;
    }

    /// <summary>SSA's legacy alignment (1–3 bottom, +4 top, +8 middle) as a keypad digit.</summary>
    public static int FromLegacyAlignment(int legacy) => legacy switch
    {
        >= 1 and <= 3 => legacy,
        >= 5 and <= 7 => legacy + 2,
        >= 9 and <= 11 => legacy - 5,
        _ => 2,
    };

    /// <summary>A keypad alignment as SSA's legacy value.</summary>
    public static int ToLegacyAlignment(int alignment) => alignment switch
    {
        >= 7 => alignment - 2,
        >= 4 => alignment + 5,
        _ => alignment,
    };

    private static int Int(string text, int fallback = 0) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v :
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : fallback;

    private static double Double(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool Flag(string text) => text.Trim() is "-1" or "1" || Int(text) >= 600;

    // ------------------------------------------------------------------ writing

    /// <summary>The script header (script info, the styles, the events format line) of <paramref name="script"/>.</summary>
    public static string WriteHeader(SubtitleScript script, bool ssa)
    {
        ArgumentNullException.ThrowIfNull(script);
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n; Script generated by Media Muxing Wizard\n");
        sb.Append(ssa ? "ScriptType: v4.00\n" : "ScriptType: v4.00+\n");
        sb.Append(CultureInfo.InvariantCulture, $"PlayResX: {script.Width}\nPlayResY: {script.Height}\n");
        if (!ssa)
            sb.Append("WrapStyle: 0\nScaledBorderAndShadow: yes\nYCbCr Matrix: None\n");
        sb.Append(ssa ? "\n[V4 Styles]\nFormat: " + SsaStyleFormat + "\n" : "\n[V4+ Styles]\nFormat: " + AssStyleFormat + "\n");
        var styles = script.Styles.Count > 0 ? script.Styles : [script.Style(null)];
        foreach (var s in styles)
            sb.Append("Style: ").Append(StyleLine(s, ssa)).Append('\n');
        sb.Append("\n[Events]\n");
        sb.Append(ssa ? "Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text" : "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        return sb.ToString();
    }

    private static string StyleLine(SubtitleStyleSheet s, bool ssa)
    {
        var st = s.Style;
        var font = (s.Vertical ? "@" : string.Empty) + (st.Font ?? "Arial");
        string B(bool? v) => v == true ? "-1" : "0";
        var size = Num(st.Size ?? 20);
        if (ssa)
        {
            return string.Join(',', s.Name, font, size, SsaColor(st.Primary), SsaColor(st.Secondary), SsaColor(st.Outline), SsaColor(st.Back), B(st.Bold), B(st.Italic),
                s.BorderStyle.ToString(CultureInfo.InvariantCulture), Num(s.OutlineWidth), Num(s.Shadow), ToLegacyAlignment(s.Alignment).ToString(CultureInfo.InvariantCulture),
                s.MarginL.ToString(CultureInfo.InvariantCulture), s.MarginR.ToString(CultureInfo.InvariantCulture), s.MarginV.ToString(CultureInfo.InvariantCulture), "0", "1");
        }

        return string.Join(',', s.Name, font, size, AssColor(st.Primary, true), AssColor(st.Secondary, true), AssColor(st.Outline, true), AssColor(st.Back, true),
            B(st.Bold), B(st.Italic), B(st.Underline), B(st.Strikeout), "100", "100", "0", "0", s.BorderStyle.ToString(CultureInfo.InvariantCulture),
            Num(s.OutlineWidth), Num(s.Shadow), s.Alignment.ToString(CultureInfo.InvariantCulture), s.MarginL.ToString(CultureInfo.InvariantCulture),
            s.MarginR.ToString(CultureInfo.InvariantCulture), s.MarginV.ToString(CultureInfo.InvariantCulture), "1");
    }

    /// <summary>&amp;HAABBGGRR (style lines) or &amp;HBBGGRR&amp; (override tags).</summary>
    private static string AssColor(SubtitleColor? color, bool withAlpha)
    {
        var c = color ?? SubtitleColor.White;
        return withAlpha
            ? string.Create(CultureInfo.InvariantCulture, $"&H{255 - c.A:X2}{c.B:X2}{c.G:X2}{c.R:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"&H{c.B:X2}{c.G:X2}{c.R:X2}&");
    }

    private static string SsaColor(SubtitleColor? color)
    {
        var c = color ?? SubtitleColor.White;
        return ((c.B << 16) | (c.G << 8) | c.R).ToString(CultureInfo.InvariantCulture);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A Matroska ASS/SSA block for <paramref name="e"/> (its times are the block's).</summary>
    public static string WriteBlock(SubtitleEvent e, int readOrder, SubtitleScript script, bool ssa)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        var sheet = script.Style(e.StyleName);
        string M(int? v) => (v ?? 0).ToString(CultureInfo.InvariantCulture);
        return string.Join(',', readOrder.ToString(CultureInfo.InvariantCulture), ssa ? "0" : e.Layer.ToString(CultureInfo.InvariantCulture), sheet.Name,
            e.Speaker.Replace(',', ';'), M(e.MarginL), M(e.MarginR), M(e.MarginV), e.Effect.Replace(',', ';'), WriteText(e, script, ssa));
    }

    /// <summary>The Text field of an event: override tags for what differs from its style, karaoke, line breaks.</summary>
    public static string WriteText(SubtitleEvent e, SubtitleScript script, bool ssa)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        var sheet = script.Style(e.StyleName);
        var sb = new StringBuilder();
        var head = new StringBuilder();
        if (e.Alignment is { } a && a != sheet.Alignment || e.Position is not null && e.Alignment is not null)
            head.Append(ssa ? $"\\a{ToLegacyAlignment(e.Alignment ?? sheet.Alignment)}" : $"\\an{e.Alignment ?? sheet.Alignment}");
        if (e.Position is { } p)
            head.Append(CultureInfo.InvariantCulture, $"\\pos({Num(p.X)},{Num(p.Y)})");
        var vertical = e.Vertical == true && !sheet.Vertical;
        var current = sheet.Style;
        if (vertical)
            head.Append("\\fn@").Append(current.Font);
        if (head.Length > 0)
            sb.Append('{').Append(head).Append('}');

        var karaoke = e.Karaoke.ToDictionary(k => k.Start, k => k);
        for (var i = 0; i <= e.Text.Length; i++)
        {
            var tags = new StringBuilder();
            if (i < e.Text.Length)
            {
                var style = script.StyleAt(e, i);
                if (style != current)
                {
                    tags.Append(StyleTags(current, style, vertical, ssa));
                    current = style;
                }

                if (karaoke.TryGetValue(i, out var k))
                {
                    var tag = k.Kind switch
                    {
                        KaraokeKind.Sweep => "kf",
                        KaraokeKind.Outline => "ko",
                        _ => "k",
                    };
                    tags.Append(CultureInfo.InvariantCulture, $"\\{tag}{Math.Max(0, (k.DurationMs + 5) / 10)}");
                }
            }

            if (tags.Length > 0)
                sb.Append('{').Append(tags).Append('}');
            if (i < e.Text.Length)
            {
                var c = e.Text[i];
                sb.Append(c switch
                {
                    '\n' => "\\N",
                    ' ' => "\\h",
                    _ => c.ToString(),
                });
            }
        }

        return sb.ToString();
    }

    private static string StyleTags(SubtitleStyle from, SubtitleStyle to, bool vertical, bool ssa)
    {
        var t = new StringBuilder();
        if (to.Font != from.Font)
            t.Append("\\fn").Append(vertical ? "@" : string.Empty).Append(to.Font);
        if (to.Size != from.Size && to.Size is { } size)
            t.Append("\\fs").Append(Num(size));
        if ((to.Bold ?? false) != (from.Bold ?? false))
            t.Append(to.Bold == true ? "\\b1" : "\\b0");
        if ((to.Italic ?? false) != (from.Italic ?? false))
            t.Append(to.Italic == true ? "\\i1" : "\\i0");
        if ((to.Underline ?? false) != (from.Underline ?? false))
            t.Append(to.Underline == true ? "\\u1" : "\\u0");
        if ((to.Strikeout ?? false) != (from.Strikeout ?? false))
            t.Append(to.Strikeout == true ? "\\s1" : "\\s0");
        void Color(string tag, string alphaTag, SubtitleColor? a, SubtitleColor? b)
        {
            if (b is not { } c || a == b)
                return;
            if (a is not { } old || (old.R, old.G, old.B) != (c.R, c.G, c.B))
                t.Append('\\').Append(tag).Append(AssColor(c, false));
            if (!ssa && (a?.A ?? 255) != c.A)
                t.Append(CultureInfo.InvariantCulture, $"\\{alphaTag}&H{255 - c.A:X2}&");
        }

        Color(ssa ? "c" : "1c", "1a", from.Primary, to.Primary);
        Color("2c", "2a", from.Secondary, to.Secondary);
        Color("3c", "3a", from.Outline, to.Outline);
        Color("4c", "4a", from.Back, to.Back);
        return t.ToString();
    }
}
