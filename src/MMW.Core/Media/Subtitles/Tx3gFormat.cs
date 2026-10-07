using System.Buffers.Binary;
using System.Text;
using MMW.Core.Resources;

namespace MMW.Core.Media.Subtitles;

/// <summary>
/// 3GPP timed text (3GPP TS 26.245, MP4 'tx3g'), with everything it can express: the sample description's display
/// flags (vertical text, continuous karaoke), justification, background colour, text box, default style and font
/// table; per sample the text, style records ('styl': font, size, colour, bold/italic/underline per range), karaoke
/// ('krok' with 'hclr'), highlight ('hlit'), text box ('tbox', for positioned text), blinking ('blnk'), text wrap
/// ('twrp') and Apple's forced flag ('frcd').
/// </summary>
public static class Tx3gFormat
{
    public const uint ScrollIn = 0x00000020;
    public const uint ScrollOut = 0x00000040;
    public const uint ContinuousKaraoke = 0x00000800;
    public const uint VerticalText = 0x00020000;
    public const uint FillTextRegion = 0x00040000;

    /// <summary>The fonts and defaults of a sample description, needed to read and write its samples.</summary>
    public sealed class Description
    {
        public uint DisplayFlags { get; set; }

        /// <summary>Horizontal justification: 0 left, 1 centre, -1 right.</summary>
        public sbyte Horizontal { get; set; } = 1;

        /// <summary>Vertical justification: 0 top, 1 centre, -1 bottom.</summary>
        public sbyte Vertical { get; set; } = -1;

        public SubtitleColor Background { get; set; } = new(0, 0, 0, 0);

        /// <summary>Default text box: top, left, bottom, right.</summary>
        public (short Top, short Left, short Bottom, short Right) Box { get; set; }

        public ushort DefaultFont { get; set; } = 1;

        public byte DefaultFace { get; set; }

        public byte DefaultSize { get; set; } = 18;

        public SubtitleColor DefaultColor { get; set; } = SubtitleColor.White;

        public Dictionary<ushort, string> Fonts { get; } = [];
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// A sample description (the payload after the 8-byte SampleEntry header) as a script on a
    /// <paramref name="width"/>×<paramref name="height"/> canvas (the track size) with its Default style.
    /// </summary>
    public static (SubtitleScript Script, Description Description) ReadDescription(ReadOnlySpan<byte> entry, int width, int height)
    {
        var d = new Description();
        if (entry.Length >= 30)
        {
            d.DisplayFlags = BinaryPrimitives.ReadUInt32BigEndian(entry);
            d.Horizontal = (sbyte)entry[4];
            d.Vertical = (sbyte)entry[5];
            d.Background = new SubtitleColor(entry[6], entry[7], entry[8], entry[9]);
            d.Box = (BinaryPrimitives.ReadInt16BigEndian(entry[10..]), BinaryPrimitives.ReadInt16BigEndian(entry[12..]),
                BinaryPrimitives.ReadInt16BigEndian(entry[14..]), BinaryPrimitives.ReadInt16BigEndian(entry[16..]));
            d.DefaultFont = BinaryPrimitives.ReadUInt16BigEndian(entry[22..]);
            d.DefaultFace = entry[24];
            d.DefaultSize = entry[25];
            d.DefaultColor = new SubtitleColor(entry[26], entry[27], entry[28], entry[29]);
            ForEachBox(entry[30..], (type, payload) =>
            {
                if (type != "ftab" || payload.Length < 2)
                    return;
                var count = BinaryPrimitives.ReadUInt16BigEndian(payload);
                var at = 2;
                for (var i = 0; i < count && at + 3 <= payload.Length; i++)
                {
                    var id = BinaryPrimitives.ReadUInt16BigEndian(payload[at..]);
                    var length = payload[at + 2];
                    if (at + 3 + length > payload.Length)
                        break;
                    d.Fonts[id] = Encoding.UTF8.GetString(payload.Slice(at + 3, length));
                    at += 3 + length;
                }
            });
        }

        if (width <= 0 || height <= 0)
            (width, height) = d.Box.Right > d.Box.Left && d.Box.Bottom > d.Box.Top ? (d.Box.Right, d.Box.Bottom) : (1920, 1080);
        if (d.Box.Right <= d.Box.Left || d.Box.Bottom <= d.Box.Top)
            d.Box = (0, 0, (short)Math.Min(height, short.MaxValue), (short)Math.Min(width, short.MaxValue));
        var script = new SubtitleScript { Width = width, Height = height };
        var column = d.Horizontal switch
        {
            0 => 1,
            < 0 => 3,
            _ => 2,
        };
        var row = d.Vertical switch
        {
            0 => 3,
            1 => 2,
            _ => 1,
        };
        script.Styles.Add(new SubtitleStyleSheet
        {
            Style = new SubtitleStyle
            {
                Font = FontName(d, d.DefaultFont),
                Size = d.DefaultSize,
                Primary = d.DefaultColor,
                Secondary = d.DefaultColor,
                Outline = SubtitleColor.Black,
                Back = d.Background,
                Bold = (d.DefaultFace & 1) != 0,
                Italic = (d.DefaultFace & 2) != 0,
                Underline = (d.DefaultFace & 4) != 0,
                Strikeout = false,
                Blink = false,
            },
            Alignment = (row - 1) * 3 + column,
            MarginL = Math.Max(0, (int)d.Box.Left),
            MarginR = Math.Max(0, width - d.Box.Right),
            MarginV = Math.Max(0, row == 3 ? d.Box.Top : height - d.Box.Bottom),
            BorderStyle = d.Background.A > 0 ? 3 : 1,
            Vertical = (d.DisplayFlags & VerticalText) != 0,
        });
        return (script, d);
    }

    private static string FontName(Description d, ushort id) => d.Fonts.TryGetValue(id, out var name) && name.Length > 0 ? name : "Sans-Serif";

    /// <summary>An event from a tx3g sample shown from <paramref name="start"/> to <paramref name="end"/> ms (karaoke times in <paramref name="timescale"/>).</summary>
    public static SubtitleEvent ReadSample(ReadOnlySpan<byte> sample, Description d, SubtitleScript script, long start, long end, uint timescale)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(script);
        if (sample.Length < 2)
            return new SubtitleEvent { Start = start, End = end };
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sample), sample.Length - 2);
        var bytes = sample.Slice(2, length);
        var text = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes[2..])
            : Encoding.UTF8.GetString(bytes);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var baseStyle = script.Style(null).Style;
        var styles = new SubtitleStyle?[text.Length];
        var karaoke = new List<KaraokeSyllable>();
        (int Start, int End)? highlight = null;
        SubtitleColor? highlightColor = null;
        var e = new SubtitleEvent { Start = start, End = end };
        long Ms(long units) => timescale == 0 ? units : units * 1000 / timescale;

        ForEachBox(sample[(2 + length)..], (type, p) =>
        {
            switch (type)
            {
                case "styl" when p.Length >= 2:
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(p);
                    for (var i = 0; i < count && 2 + i * 12 + 12 <= p.Length; i++)
                    {
                        var r = p[(2 + i * 12)..];
                        var from = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(r));
                        var to = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(r[2..]));
                        var face = r[6];
                        var style = new SubtitleStyle
                        {
                            Font = FontName(d, BinaryPrimitives.ReadUInt16BigEndian(r[4..])),
                            Size = r[7],
                            Primary = new SubtitleColor(r[8], r[9], r[10], r[11]),
                            Bold = (face & 1) != 0,
                            Italic = (face & 2) != 0,
                            Underline = (face & 4) != 0,
                        }.Except(baseStyle);
                        for (var k = from; k < to && k < styles.Length; k++)
                            styles[k] = style.Over(styles[k]);
                    }

                    break;
                }

                case "hlit" when p.Length >= 4:
                    highlight = (Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(p)), Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(p[2..])));
                    break;
                case "hclr" when p.Length >= 4:
                    highlightColor = new SubtitleColor(p[0], p[1], p[2], p[3]);
                    break;
                case "krok" when p.Length >= 6:
                {
                    var time = (long)BinaryPrimitives.ReadUInt32BigEndian(p);
                    var count = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
                    var kind = (d.DisplayFlags & ContinuousKaraoke) != 0 ? KaraokeKind.Sweep : KaraokeKind.Fill;
                    for (var i = 0; i < count && 6 + i * 8 + 8 <= p.Length; i++)
                    {
                        var k = p[(6 + i * 8)..];
                        var endTime = (long)BinaryPrimitives.ReadUInt32BigEndian(k);
                        var from = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(k[4..]));
                        var to = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(k[6..]));
                        karaoke.Add(new KaraokeSyllable(from, to, Ms(time), Math.Max(0, Ms(endTime) - Ms(time)), kind));
                        time = endTime;
                    }

                    break;
                }

                case "blnk" when p.Length >= 4:
                {
                    var from = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(p));
                    var to = Utf16Index(text, BinaryPrimitives.ReadUInt16BigEndian(p[2..]));
                    for (var k = from; k < to && k < styles.Length; k++)
                        styles[k] = new SubtitleStyle { Blink = true }.Over(styles[k]);
                    break;
                }

                case "tbox" when p.Length >= 8:
                {
                    var top = BinaryPrimitives.ReadInt16BigEndian(p);
                    var left = BinaryPrimitives.ReadInt16BigEndian(p[2..]);
                    var bottom = BinaryPrimitives.ReadInt16BigEndian(p[4..]);
                    var right = BinaryPrimitives.ReadInt16BigEndian(p[6..]);
                    var alignment = script.Style(null).Alignment;
                    var column = (alignment - 1) % 3;
                    var row = (alignment - 1) / 3;
                    var x = column switch
                    {
                        0 => left,
                        2 => right,
                        _ => (left + right) / 2.0,
                    };
                    var y = row switch
                    {
                        2 => top,
                        1 => (top + bottom) / 2.0,
                        _ => bottom,
                    };
                    e = e with { Position = (x, y) };
                    break;
                }

                case "frcd":
                    e = e with { Forced = true };
                    break;
            }
        });

        if (highlight is { } h)
        {
            var color = highlightColor ?? new SubtitleColor(255, 255, 0);
            for (var k = h.Start; k < h.End && k < styles.Length; k++)
                styles[k] = new SubtitleStyle { Back = color }.Over(styles[k]);
        }

        if (karaoke.Count > 0)
        {
            // tx3g draws unsung text in its style colour and sung text in the highlight colour; ASS calls them
            // secondary and primary.
            var sung = highlightColor ?? new SubtitleColor(255, 255, 0);
            for (var k = 0; k < styles.Length; k++)
            {
                var unsung = (styles[k]?.Primary ?? baseStyle.Primary)!.Value;
                styles[k] = new SubtitleStyle { Primary = sung, Secondary = unsung }.Over(styles[k]);
            }
        }

        var runs = new List<SubtitleRun>();
        for (var k = 0; k < styles.Length;)
        {
            var s = styles[k];
            var m = k + 1;
            while (m < styles.Length && styles[m] == s)
                m++;
            if (s is { IsEmpty: false })
                runs.Add(new SubtitleRun(k, m, s));
            k = m;
        }

        return e with { Text = text, Runs = runs, Karaoke = karaoke };
    }

    private delegate void BoxVisitor(string type, ReadOnlySpan<byte> payload);

    private static void ForEachBox(ReadOnlySpan<byte> data, BoxVisitor visit)
    {
        var at = 0;
        while (at + 8 <= data.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (size < 8 || at + size > data.Length)
                break;
            visit(Encoding.ASCII.GetString(data.Slice(at + 4, 4)), data.Slice(at + 8, size - 8));
            at += size;
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// The sample description for <paramref name="script"/> on its canvas: justification from the Default style's
    /// alignment, vertical text and continuous karaoke when the script uses them, the font table of every font used.
    /// </summary>
    public static Description Describe(SubtitleScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var sheet = script.Style(null);
        var d = new Description
        {
            Horizontal = ((sheet.Alignment - 1) % 3) switch
            {
                0 => 0,
                2 => -1,
                _ => 1,
            },
            Vertical = ((sheet.Alignment - 1) / 3) switch
            {
                2 => 0,
                1 => 1,
                _ => -1,
            },
            Background = sheet.BorderStyle == 3 && sheet.Style.Back is { } back ? back : new SubtitleColor(0, 0, 0, 0),
            Box = (0, 0, (short)Math.Min(script.Height, short.MaxValue), (short)Math.Min(script.Width, short.MaxValue)),
            DefaultSize = Size(sheet.Style.Size),
            DefaultColor = sheet.Style.Primary ?? SubtitleColor.White,
            DefaultFace = Face(sheet.Style),
        };
        if (sheet.Vertical || script.Events.Count > 0 && script.Events.Count(e => e.Vertical ?? script.Style(e.StyleName).Vertical) * 2 > script.Events.Count)
            d.DisplayFlags |= VerticalText;
        if (script.Events.Any(e => e.Karaoke.Any(k => k.Kind == KaraokeKind.Sweep)))
            d.DisplayFlags |= ContinuousKaraoke;
        d.Fonts[1] = sheet.Style.Font ?? "Sans-Serif";
        foreach (var e in script.Events)
        {
            for (var i = 0; i < e.Text.Length; i++)
            {
                if (script.StyleAt(e, i).Font is { } font && !d.Fonts.ContainsValue(font))
                    d.Fonts[(ushort)(d.Fonts.Count + 1)] = font;
            }
        }

        return d;
    }

    /// <summary>The sample description payload (after the 8-byte SampleEntry header; forced bits are the muxer's).</summary>
    public static byte[] WriteDescription(Description d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var p = new byte[30];
        BinaryPrimitives.WriteUInt32BigEndian(p, d.DisplayFlags);
        p[4] = (byte)d.Horizontal;
        p[5] = (byte)d.Vertical;
        (p[6], p[7], p[8], p[9]) = (d.Background.R, d.Background.G, d.Background.B, d.Background.A);
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(10), d.Box.Top);
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(12), d.Box.Left);
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(14), d.Box.Bottom);
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(16), d.Box.Right);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), d.DefaultFont);
        p[24] = d.DefaultFace;
        p[25] = d.DefaultSize;
        (p[26], p[27], p[28], p[29]) = (d.DefaultColor.R, d.DefaultColor.G, d.DefaultColor.B, d.DefaultColor.A);
        var fonts = new List<byte>();
        foreach (var (id, name) in d.Fonts.OrderBy(f => f.Key))
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length > 255)
                bytes = bytes[..255];
            fonts.Add((byte)(id >> 8));
            fonts.Add((byte)id);
            fonts.Add((byte)bytes.Length);
            fonts.AddRange(bytes);
        }

        return [.. p, .. Box("ftab", [(byte)(d.Fonts.Count >> 8), (byte)d.Fonts.Count, .. fonts])];
    }

    /// <summary>
    /// The sample for <paramref name="e"/> (times in milliseconds, karaoke in a 1000 Hz timescale): text, style
    /// records for every range that differs from the description's defaults, karaoke with its highlight colour,
    /// blinking ranges, a text box when the event is placed elsewhere than the default, and 'frcd' when forced.
    /// </summary>
    public static byte[] WriteSample(SubtitleEvent e, SubtitleScript script, Description d)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(d);
        var text = e.Text;
        var utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > ushort.MaxValue)
            throw new InvalidDataException(Strings.Error_Tx3gTextTooLong);
        var o = new List<byte>(2 + utf8.Length + 64) { (byte)(utf8.Length >> 8), (byte)utf8.Length };
        o.AddRange(utf8);
        if (text.Length == 0)
            return [.. o];

        var karaoke = e.Karaoke.Count > 0;
        var defaults = new SubtitleStyle
        {
            Font = d.Fonts.GetValueOrDefault(d.DefaultFont),
            Size = d.DefaultSize,
            Primary = d.DefaultColor,
            Bold = (d.DefaultFace & 1) != 0,
            Italic = (d.DefaultFace & 2) != 0,
            Underline = (d.DefaultFace & 4) != 0,
        };

        // Style records: maximal ranges of equal formatting that differ from the defaults.
        var records = new List<byte>();
        var count = 0;
        var blink = new List<(int, int)>();
        for (var i = 0; i < text.Length;)
        {
            var style = script.StyleAt(e, i);
            var j = i + 1;
            while (j < text.Length && script.StyleAt(e, j) == style)
                j++;
            // Karaoke text is drawn in the unsung colour; the sung colour is the highlight.
            var color = karaoke ? style.Secondary ?? style.Primary ?? d.DefaultColor : style.Primary ?? d.DefaultColor;
            var font = FontId(d, style.Font);
            var size = Size(style.Size);
            var face = Face(style);
            if (font != d.DefaultFont || size != d.DefaultSize || face != d.DefaultFace || color != d.DefaultColor)
            {
                Append16(records, CodePoints(text, i));
                Append16(records, CodePoints(text, j));
                Append16(records, font);
                records.Add(face);
                records.Add(size);
                records.AddRange([color.R, color.G, color.B, color.A]);
                count++;
            }

            if (style.Blink == true)
                blink.Add((i, j));
            i = j;
        }

        if (count > 0)
            o.AddRange(Box("styl", [(byte)(count >> 8), (byte)count, .. records]));

        if (karaoke)
        {
            var sung = script.StyleAt(e, e.Karaoke[0].Start).Primary ?? new SubtitleColor(255, 255, 0);
            o.AddRange(Box("hclr", [sung.R, sung.G, sung.B, sung.A]));
            var k = new List<byte>();
            Append32(k, (uint)Math.Max(0, e.Karaoke[0].StartMs));
            Append16(k, e.Karaoke.Count);
            foreach (var s in e.Karaoke)
            {
                Append32(k, (uint)Math.Max(0, s.StartMs + s.DurationMs));
                Append16(k, CodePoints(text, s.Start));
                Append16(k, CodePoints(text, s.End));
            }

            o.AddRange(Box("krok", [.. k]));
        }

        foreach (var (from, to) in blink)
        {
            var b = new List<byte>();
            Append16(b, CodePoints(text, from));
            Append16(b, CodePoints(text, to));
            o.AddRange(Box("blnk", [.. b]));
        }

        if (TextBox(e, script, d) is { } box)
        {
            var b = new List<byte>();
            Append16(b, box.Top);
            Append16(b, box.Left);
            Append16(b, box.Bottom);
            Append16(b, box.Right);
            o.AddRange(Box("tbox", [.. b]));
        }

        if (e.Forced)
            o.AddRange(Box("frcd", []));
        return [.. o];
    }

    /// <summary>
    /// A text box that puts the event where it should be, with the description's justification, when its alignment
    /// or position differs from the default; null otherwise. The text's extent is estimated from its lines and size.
    /// </summary>
    private static (short Top, short Left, short Bottom, short Right)? TextBox(SubtitleEvent e, SubtitleScript script, Description d)
    {
        var sheet = script.Style(e.StyleName);
        var alignment = e.Alignment ?? sheet.Alignment;
        var defaultAlignment = script.Style(null).Alignment;
        var marginV = e.MarginV ?? sheet.MarginV;
        var marginL = e.MarginL ?? sheet.MarginL;
        var marginR = e.MarginR ?? sheet.MarginR;
        if (e.Position is null && alignment == defaultAlignment && marginV == script.Style(null).MarginV)
            return null;

        var column = (alignment - 1) % 3;
        var row = (alignment - 1) / 3;
        var size = script.StyleAt(e, 0).Size ?? d.DefaultSize;
        var lines = e.Text.Split('\n');
        var textWidth = Math.Min(script.Width, lines.Max(l => l.Length) * size * 0.55 + size);
        var textHeight = Math.Min(script.Height, lines.Length * size * 1.25 + size * 0.25);
        var (x, y) = e.Position ?? (
            column switch
            {
                0 => marginL,
                2 => script.Width - marginR,
                _ => script.Width / 2.0,
            },
            row switch
            {
                2 => marginV,
                1 => script.Height / 2.0,
                _ => script.Height - marginV,
            });

        // The text's box from the anchor, then widened to where the description's justification puts it there.
        var left = column switch
        {
            0 => x,
            2 => x - textWidth,
            _ => x - textWidth / 2,
        };
        var top = row switch
        {
            2 => y,
            1 => y - textHeight / 2,
            _ => y - textHeight,
        };
        double right = left + textWidth, bottom = top + textHeight;
        switch (d.Horizontal)
        {
            case 0:
                right = script.Width;
                break;
            case < 0:
                left = 0;
                break;
        }

        switch (d.Vertical)
        {
            case 0:
                bottom = script.Height;
                break;
            case < 0:
                top = 0;
                break;
        }

        static short Clamp(double v, int max) => (short)Math.Clamp(Math.Round(v), 0, Math.Min(max, short.MaxValue));
        return (Clamp(top, script.Height), Clamp(left, script.Width), Clamp(bottom, script.Height), Clamp(right, script.Width));
    }

    private static ushort FontId(Description d, string? font)
    {
        if (font is null)
            return d.DefaultFont;
        foreach (var (id, name) in d.Fonts)
        {
            if (name == font)
                return id;
        }

        return d.DefaultFont;
    }

    private static byte Size(double? size) => (byte)Math.Clamp(Math.Round(size ?? 18), 1, 255);

    private static byte Face(SubtitleStyle s) => (byte)((s.Bold == true ? 1 : 0) | (s.Italic == true ? 2 : 0) | (s.Underline == true ? 4 : 0));

    private static byte[] Box(string type, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(b, b.Length);
        Encoding.ASCII.GetBytes(type, b.AsSpan(4));
        payload.CopyTo(b, 8);
        return b;
    }

    private static void Append16(List<byte> o, int v)
    {
        o.Add((byte)(v >> 8));
        o.Add((byte)v);
    }

    private static void Append32(List<byte> o, uint v)
    {
        o.Add((byte)(v >> 24));
        o.Add((byte)(v >> 16));
        o.Add((byte)(v >> 8));
        o.Add((byte)v);
    }

    /// <summary>tx3g character offsets count Unicode characters, not UTF-16 units.</summary>
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
}
