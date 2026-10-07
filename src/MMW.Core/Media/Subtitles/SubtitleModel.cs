using System.Globalization;

namespace MMW.Core.Media.Subtitles;

/// <summary>A colour with opacity (<see cref="A"/> 255 = opaque).</summary>
public readonly record struct SubtitleColor(byte R, byte G, byte B, byte A = 255)
{
    public static SubtitleColor White => new(255, 255, 255);

    public static SubtitleColor Black => new(0, 0, 0);

    /// <summary>The colour as 0xRRGGBBAA.</summary>
    public uint Rgba => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;

    public static SubtitleColor FromRgba(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    /// <summary>"#rrggbb" (CSS, SubRip).</summary>
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    /// <summary>Parses "#rgb", "#rrggbb", "#rrggbbaa", "rgb(…)"/"rgba(…)" or a CSS/HTML colour name; null when it is none.</summary>
    public static SubtitleColor? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim().Trim('"', '\'').ToLowerInvariant();
        if (t.StartsWith('#'))
        {
            var hex = t[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                return hex.Length == 6 ? FromRgba((v << 8) | 0xFF) : FromRgba(v);
            return null;
        }

        if (t.StartsWith("rgb", StringComparison.Ordinal) && t.IndexOf('(') is > 0 and var open && t.IndexOf(')') is var close && close > open)
        {
            var parts = t[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                var a = parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                    ? (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255)
                    : (byte)255;
                return new SubtitleColor(r, g, b, a);
            }

            return null;
        }

        return t switch
        {
            "white" => White,
            "black" => Black,
            "red" => new SubtitleColor(255, 0, 0),
            "lime" => new SubtitleColor(0, 255, 0),
            "green" => new SubtitleColor(0, 128, 0),
            "blue" => new SubtitleColor(0, 0, 255),
            "yellow" => new SubtitleColor(255, 255, 0),
            "cyan" or "aqua" => new SubtitleColor(0, 255, 255),
            "magenta" or "fuchsia" => new SubtitleColor(255, 0, 255),
            "gray" or "grey" => new SubtitleColor(128, 128, 128),
            "silver" => new SubtitleColor(192, 192, 192),
            "maroon" => new SubtitleColor(128, 0, 0),
            "olive" => new SubtitleColor(128, 128, 0),
            "navy" => new SubtitleColor(0, 0, 128),
            "purple" => new SubtitleColor(128, 0, 128),
            "teal" => new SubtitleColor(0, 128, 128),
            "orange" => new SubtitleColor(255, 165, 0),
            "transparent" => new SubtitleColor(0, 0, 0, 0),
            _ => null,
        };
    }
}

/// <summary>
/// Character formatting. In a run, null fields inherit from the cue's style; a style sheet's
/// <see cref="SubtitleStyleSheet.Style"/> sets every field.
/// </summary>
public sealed record SubtitleStyle
{
    public string? Font { get; init; }

    /// <summary>Font size in canvas pixels (ASS points on the PlayResY grid).</summary>
    public double? Size { get; init; }

    /// <summary>Text colour (ASS primary colour; the sung part of karaoke).</summary>
    public SubtitleColor? Primary { get; init; }

    /// <summary>Karaoke colour before a syllable is sung (ASS secondary colour).</summary>
    public SubtitleColor? Secondary { get; init; }

    public SubtitleColor? Outline { get; init; }

    /// <summary>Shadow or opaque box colour (ASS back colour, tx3g/WebVTT background).</summary>
    public SubtitleColor? Back { get; init; }

    public bool? Bold { get; init; }

    public bool? Italic { get; init; }

    public bool? Underline { get; init; }

    public bool? Strikeout { get; init; }

    /// <summary>Blinking text (tx3g 'blnk').</summary>
    public bool? Blink { get; init; }

    /// <summary>A style with every field of <paramref name="baseStyle"/> that this one leaves unset.</summary>
    public SubtitleStyle Over(SubtitleStyle? baseStyle) => baseStyle is null ? this : new SubtitleStyle
    {
        Font = Font ?? baseStyle.Font,
        Size = Size ?? baseStyle.Size,
        Primary = Primary ?? baseStyle.Primary,
        Secondary = Secondary ?? baseStyle.Secondary,
        Outline = Outline ?? baseStyle.Outline,
        Back = Back ?? baseStyle.Back,
        Bold = Bold ?? baseStyle.Bold,
        Italic = Italic ?? baseStyle.Italic,
        Underline = Underline ?? baseStyle.Underline,
        Strikeout = Strikeout ?? baseStyle.Strikeout,
        Blink = Blink ?? baseStyle.Blink,
    };

    /// <summary>The fields of this (complete) style that differ from <paramref name="baseStyle"/>.</summary>
    public SubtitleStyle Except(SubtitleStyle baseStyle) => new()
    {
        Font = Font != baseStyle.Font ? Font : null,
        Size = Size != baseStyle.Size ? Size : null,
        Primary = Primary != baseStyle.Primary ? Primary : null,
        Secondary = Secondary != baseStyle.Secondary ? Secondary : null,
        Outline = Outline != baseStyle.Outline ? Outline : null,
        Back = Back != baseStyle.Back ? Back : null,
        Bold = (Bold ?? false) != (baseStyle.Bold ?? false) ? Bold ?? false : null,
        Italic = (Italic ?? false) != (baseStyle.Italic ?? false) ? Italic ?? false : null,
        Underline = (Underline ?? false) != (baseStyle.Underline ?? false) ? Underline ?? false : null,
        Strikeout = (Strikeout ?? false) != (baseStyle.Strikeout ?? false) ? Strikeout ?? false : null,
        Blink = (Blink ?? false) != (baseStyle.Blink ?? false) ? Blink ?? false : null,
    };

    public bool IsEmpty => Font is null && Size is null && Primary is null && Secondary is null && Outline is null && Back is null &&
                           Bold is null && Italic is null && Underline is null && Strikeout is null && Blink is null;

    /// <summary>The same style with sizes scaled by <paramref name="factor"/>.</summary>
    public SubtitleStyle Scaled(double factor) => Size is { } s ? this with { Size = Math.Round(s * factor, 2) } : this;
}

/// <summary>A formatted range of a cue's text (UTF-16 indexes, end exclusive); ranges do not overlap.</summary>
public readonly record struct SubtitleRun(int Start, int End, SubtitleStyle Style);

/// <summary>How a karaoke syllable is shown being sung.</summary>
public enum KaraokeKind
{
    /// <summary>The whole syllable changes colour at its start (ASS \k).</summary>
    Fill,

    /// <summary>The colour sweeps across the syllable during it (ASS \kf, tx3g continuous karaoke).</summary>
    Sweep,

    /// <summary>The outline appears at the syllable's start (ASS \ko).</summary>
    Outline,
}

/// <summary>A karaoke syllable: text range and when it is sung, in milliseconds from the cue start.</summary>
public readonly record struct KaraokeSyllable(int Start, int End, long StartMs, long DurationMs, KaraokeKind Kind);

/// <summary>A named style with its layout (an ASS style; the defaults of a tx3g sample entry; WebVTT's ::cue rule).</summary>
public sealed record SubtitleStyleSheet
{
    public string Name { get; init; } = "Default";

    /// <summary>Every field set.</summary>
    public SubtitleStyle Style { get; init; } = DefaultStyle(20);

    /// <summary>Position on the screen as a numeric keypad digit (1 bottom left … 9 top right).</summary>
    public int Alignment { get; init; } = 2;

    public int MarginL { get; init; } = 10;

    public int MarginR { get; init; } = 10;

    public int MarginV { get; init; } = 10;

    /// <summary>ASS BorderStyle: 1 outline and shadow, 3 opaque box (in <see cref="SubtitleStyle.Back"/>).</summary>
    public int BorderStyle { get; init; } = 1;

    public double OutlineWidth { get; init; } = 2;

    public double Shadow { get; init; } = 2;

    /// <summary>Text runs top to bottom (ASS "@" fonts, tx3g vertical text, WebVTT vertical:rl).</summary>
    public bool Vertical { get; init; }

    /// <summary>A complete white-on-black-outline style of <paramref name="size"/> pixels.</summary>
    public static SubtitleStyle DefaultStyle(double size) => new()
    {
        Font = "Arial",
        Size = size,
        Primary = SubtitleColor.White,
        Secondary = new SubtitleColor(255, 0, 0),
        Outline = SubtitleColor.Black,
        Back = new SubtitleColor(0, 0, 0, 128),
        Bold = false,
        Italic = false,
        Underline = false,
        Strikeout = false,
        Blink = false,
    };
}

/// <summary>One subtitle event, in milliseconds.</summary>
public sealed record SubtitleEvent
{
    public long Start { get; init; }

    public long End { get; init; }

    /// <summary>The text ('\n' between lines).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Formatting over the style sheet's, by range.</summary>
    public IReadOnlyList<SubtitleRun> Runs { get; init; } = [];

    public string StyleName { get; init; } = "Default";

    /// <summary>Alignment of this event (null: the style's).</summary>
    public int? Alignment { get; init; }

    /// <summary>Anchor point on the canvas (pixels; the alignment says which corner/edge of the text it is), like ASS \pos.</summary>
    public (double X, double Y)? Position { get; init; }

    public int? MarginL { get; init; }

    public int? MarginR { get; init; }

    public int? MarginV { get; init; }

    /// <summary>Vertical text (null: the style's).</summary>
    public bool? Vertical { get; init; }

    public IReadOnlyList<KaraokeSyllable> Karaoke { get; init; } = [];

    /// <summary>A forced subtitle (shown even when subtitles are off).</summary>
    public bool Forced { get; init; }

    public int Layer { get; init; }

    /// <summary>Speaker (ASS Name, WebVTT voice).</summary>
    public string Speaker { get; init; } = string.Empty;

    /// <summary>ASS Effect field (kept for ASS ↔ SSA).</summary>
    public string Effect { get; init; } = string.Empty;

    public bool IsEmpty => Text.Length == 0;
}

/// <summary>A whole subtitle track in a form every text format converts to and from.</summary>
public sealed class SubtitleScript
{
    /// <summary>Coordinate space of positions, margins and sizes (ASS PlayRes, tx3g track size).</summary>
    public int Width { get; set; } = 384;

    public int Height { get; set; } = 288;

    public List<SubtitleStyleSheet> Styles { get; } = [];

    public List<SubtitleEvent> Events { get; } = [];

    /// <summary>The style sheet named <paramref name="name"/>, else the first one, else a default.</summary>
    public SubtitleStyleSheet Style(string? name) =>
        Styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ??
        Styles.FirstOrDefault(s => s.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) ??
        Styles.FirstOrDefault() ?? new SubtitleStyleSheet { Style = SubtitleStyleSheet.DefaultStyle(Height * 0.07) };

    /// <summary>Moves everything to a <paramref name="width"/>×<paramref name="height"/> canvas (positions, margins, sizes).</summary>
    public void Rescale(int width, int height)
    {
        if (width <= 0 || height <= 0 || width == Width && height == Height)
            return;
        var sx = (double)width / Width;
        var sy = (double)height / Height;
        for (var i = 0; i < Styles.Count; i++)
        {
            var s = Styles[i];
            Styles[i] = s with
            {
                Style = s.Style.Scaled(sy),
                MarginL = (int)Math.Round(s.MarginL * sx),
                MarginR = (int)Math.Round(s.MarginR * sx),
                MarginV = (int)Math.Round(s.MarginV * sy),
                OutlineWidth = Math.Round(s.OutlineWidth * sy, 2),
                Shadow = Math.Round(s.Shadow * sy, 2),
            };
        }

        for (var i = 0; i < Events.Count; i++)
        {
            var e = Events[i];
            Events[i] = e with
            {
                Runs = e.Runs.Select(r => r with { Style = r.Style.Scaled(sy) }).ToList(),
                Position = e.Position is { } p ? (Math.Round(p.X * sx, 1), Math.Round(p.Y * sy, 1)) : null,
                MarginL = e.MarginL is { } l ? (int)Math.Round(l * sx) : null,
                MarginR = e.MarginR is { } r ? (int)Math.Round(r * sx) : null,
                MarginV = e.MarginV is { } v ? (int)Math.Round(v * sy) : null,
            };
        }

        Width = width;
        Height = height;
    }

    /// <summary>The complete style of every character of <paramref name="e"/> (its style sheet, then its runs).</summary>
    public SubtitleStyle StyleAt(SubtitleEvent e, int index)
    {
        ArgumentNullException.ThrowIfNull(e);
        var baseStyle = Style(e.StyleName).Style;
        foreach (var r in e.Runs)
        {
            if (index >= r.Start && index < r.End)
                return r.Style.Over(baseStyle);
        }

        return baseStyle;
    }
}
