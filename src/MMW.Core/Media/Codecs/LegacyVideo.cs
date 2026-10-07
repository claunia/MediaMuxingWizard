using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MMW.Core.Model;

namespace MMW.Core.Media.Codecs;

/// <summary>What the headers of an older video bitstream say: profile and level (with notable coding tools) and colour.</summary>
public sealed record LegacyVideoInfo(string ProfileLevel, ColorInfo Color);

/// <summary>MPEG-1 / MPEG-2 video (ISO/IEC 11172-2, 13818-2) sequence header and extensions.</summary>
public static class Mpeg12Video
{
    /// <summary>
    /// Profile and level ("Main@Main", "4:2:2@High") from the sequence extension and the colour of the sequence display
    /// extension, in a sequence header and what follows it; null when there is no sequence header.
    /// </summary>
    public static LegacyVideoInfo? Describe(ReadOnlySpan<byte> data)
    {
        var sequence = false;
        var profile = string.Empty;
        var color = ColorInfo.Unspecified;
        var chroma = 1;
        var progressive = true;
        for (var i = 0; i + 7 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                continue;
            var code = data[i + 3];
            if (code == 0xB3)
            {
                sequence = true;
            }
            else if (code == 0xB5 && sequence)
            {
                switch (data[i + 4] >> 4)
                {
                    case 1: // sequence extension
                        profile = ProfileLevel(((data[i + 4] & 0x0F) << 4) | (data[i + 5] >> 4));
                        progressive = ((data[i + 5] >> 3) & 1) != 0;
                        chroma = (data[i + 5] >> 1) & 3;
                        break;
                    case 2 when (data[i + 4] & 1) != 0: // sequence display extension with a colour description
                        color = new ColorInfo(data[i + 5], data[i + 6], data[i + 7]);
                        break;
                }
            }
            else if (code is 0x00 or 0xB8 && sequence)
            {
                break; // the first GOP or picture: the sequence's extensions are behind
            }

            i += 3;
        }

        if (!sequence)
            return null;
        var parts = new List<string>();
        if (profile.Length > 0)
            parts.Add(profile);
        if (chroma is 2 or 3 && !profile.StartsWith("4:2:2", StringComparison.Ordinal))
            parts.Add(chroma == 2 ? "4:2:2" : "4:4:4");
        if (!progressive)
            parts.Add("interlaced");
        return new LegacyVideoInfo(string.Join(", ", parts), color);
    }

    /// <summary>profile_and_level_indication as "Profile@Level".</summary>
    public static string ProfileLevel(int indication)
    {
        if ((indication & 0x80) != 0)
            return indication switch
            {
                0x82 => "4:2:2@High",
                0x85 => "4:2:2@Main",
                0x8A => "Multi-view@High",
                0x8B => "Multi-view@High-1440",
                0x8D => "Multi-view@Main",
                0x8E => "Multi-view@Low",
                _ => string.Empty,
            };
        var profile = ((indication >> 4) & 7) switch
        {
            1 => "High",
            2 => "Spatial",
            3 => "SNR",
            4 => "Main",
            5 => "Simple",
            _ => null,
        };
        var level = (indication & 0x0F) switch
        {
            4 => "High",
            6 => "High-1440",
            8 => "Main",
            10 => "Low",
            _ => null,
        };
        return profile is null || level is null ? string.Empty : $"{profile}@{level}";
    }
}

/// <summary>MPEG-4 Part 2 video (ISO/IEC 14496-2): visual object sequence, visual object and video object layer headers.</summary>
public static partial class Mpeg4Part2
{
    /// <summary>
    /// Profile and level ("Advanced Simple@L5"), the coding tools that matter for playback (quarter-pel, global motion
    /// compensation, interlace), the encoder named in the user data (DivX, Xvid …) and the colour of the visual object
    /// header, from configuration headers or a frame that carries them; null when there is no video object layer.
    /// </summary>
    public static LegacyVideoInfo? Describe(ReadOnlySpan<byte> data)
    {
        var profile = string.Empty;
        var color = ColorInfo.Unspecified;
        var encoder = string.Empty;
        List<string>? tools = null;
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                continue;
            var code = data[i + 3];
            var payload = data[(i + 4)..];
            switch (code)
            {
                case 0xB0:
                    profile = ProfileLevel(payload[0]);
                    break;
                case 0xB5:
                    color = VisualObjectColor(payload);
                    break;
                case 0xB2 when encoder.Length == 0:
                    encoder = Encoder(payload);
                    break;
                case >= 0x20 and <= 0x2F when tools is null:
                    tools = LayerTools(payload);
                    break;
                case 0xB6:
                    i = data.Length; // a VOP: the headers are behind
                    continue;
            }

            i += 3;
        }

        if (tools is null)
            return null;
        List<string> parts = [.. new[] { profile }.Where(p => p.Length > 0), .. tools];
        if (encoder.Length > 0)
            parts.Add(encoder);
        return new LegacyVideoInfo(string.Join(", ", parts), color);
    }

    /// <summary>
    /// The picture size and (fixed) frame rate of the first rectangular video object layer in <paramref name="data"/>;
    /// null when there is none. The frame rate is 0 when the layer does not fix it.
    /// </summary>
    public static (int Width, int Height, double FrameRate)? LayerSize(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1 || data[i + 3] is < 0x20 or > 0x2F)
                continue;
            try
            {
                var r = new BitReader(data[(i + 4)..]);
                r.Skip(1 + 8);
                var verid = 1u;
                if (r.Flag())
                {
                    verid = r.Read(4);
                    r.Skip(3);
                }

                if (r.Read(4) == 15)
                    r.Skip(16);
                if (r.Flag())
                {
                    r.Skip(3);
                    if (r.Flag())
                        r.Skip(15 + 1 + 15 + 1 + 15 + 1 + 3 + 11 + 1 + 15 + 1);
                }

                var shape = r.Read(2);
                if (shape == 3 && verid != 1)
                    r.Skip(4);
                r.Skip(1);
                var resolution = r.Read(16);
                r.Skip(1);
                double rate = 0;
                if (r.Flag())
                {
                    var increment = r.Read(Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount(Math.Max(1, resolution - 1))));
                    rate = increment > 0 ? resolution / (double)increment : 0;
                }

                if (shape != 0)
                    return null;
                r.Skip(1);
                var width = (int)r.Read(13);
                r.Skip(1);
                var height = (int)r.Read(13);
                return (width, height, rate);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>profile_and_level_indication (Annex G) as "Profile@Level".</summary>
    public static string ProfileLevel(int indication)
    {
        var level = indication & 0x0F;
        var (name, levelName) = (indication >> 4) switch
        {
            0x0 => ("Simple", level switch
            {
                8 => "L0",
                9 => "L0b",
                4 => "L4a",
                _ => $"L{level}",
            }),
            0x1 => ("Simple Scalable", $"L{level}"),
            0x2 => ("Core", $"L{level}"),
            0x3 => ("Main", $"L{level}"),
            0x4 => ("N-bit", $"L{level}"),
            0x9 => ("Advanced Real Time Simple", $"L{level}"),
            0xA => ("Core Scalable", $"L{level}"),
            0xB => ("Advanced Coding Efficiency", $"L{level}"),
            0xC => ("Advanced Core", $"L{level}"),
            0xE when level <= 4 => ("Simple Studio", $"L{level}"),
            0xE => ("Core Studio", $"L{level - 4}"),
            0xF when level == 7 => ("Advanced Simple", "L3b"),
            0xF when level <= 5 => ("Advanced Simple", $"L{level}"),
            _ => (null, null),
        };
        return name is null || indication == 0 ? string.Empty : $"{name}@{levelName}";
    }

    private static ColorInfo VisualObjectColor(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new BitReader(payload);
            if (r.Flag())
                r.Skip(7); // visual_object_verid, visual_object_priority
            var type = r.Read(4);
            if (type is 1 or 2 && r.Flag()) // video / still texture, video_signal_type
            {
                r.Skip(4); // video_format, video_range
                if (r.Flag())
                    return new ColorInfo((int)r.Read(8), (int)r.Read(8), (int)r.Read(8));
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }

        return ColorInfo.Unspecified;
    }

    /// <summary>Quarter-pel, GMC and interlace from a video object layer header; empty when it uses none of them.</summary>
    private static List<string> LayerTools(ReadOnlySpan<byte> payload)
    {
        var tools = new List<string>();
        try
        {
            var r = new BitReader(payload);
            r.Skip(1 + 8); // random_accessible_vol, video_object_type_indication
            var verid = 1u;
            if (r.Flag())
            {
                verid = r.Read(4);
                r.Skip(3);
            }

            if (r.Read(4) == 15)
                r.Skip(16); // par_width, par_height
            if (r.Flag())
            {
                r.Skip(3); // chroma_format, low_delay
                if (r.Flag())
                    r.Skip(15 + 1 + 15 + 1 + 15 + 1 + 3 + 11 + 1 + 15 + 1); // vbv_parameters
            }

            var shape = r.Read(2);
            if (shape == 3 && verid != 1)
                r.Skip(4);
            r.Skip(1); // marker
            var resolution = r.Read(16);
            r.Skip(1);
            if (r.Flag())
                r.Skip(Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount(Math.Max(1, resolution - 1)))); // fixed_vop_time_increment
            if (shape == 2)
                return tools; // binary only: no texture coding tools
            if (shape == 0)
                r.Skip(1 + 13 + 1 + 13 + 1); // width, height and markers
            if (r.Flag())
                tools.Add("interlaced");
            r.Skip(1); // obmc_disable
            var sprite = verid == 1 ? r.Read(1) : r.Read(2);
            if (sprite == 2)
                tools.Add("GMC");
            if (sprite is 1 or 2)
            {
                if (sprite != 2)
                    r.Skip(13 + 1 + 13 + 1 + 13 + 1 + 13 + 1); // sprite size and position
                r.Skip(6 + 2 + 1); // warping points, accuracy, brightness change
                if (sprite != 2)
                    r.Skip(1); // low_latency_sprite_enable
            }

            if (verid != 1 && shape != 0)
                r.Skip(1); // sadct_disable
            if (r.Flag())
                r.Skip(8); // not_8_bit: quant_precision, bits_per_pixel
            if (shape == 3)
                r.Skip(3);
            if (r.Flag()) // quant_type (MPEG quantisation)
            {
                tools.Add("MPEG quantisation");
                for (var matrix = 0; matrix < 2; matrix++)
                {
                    if (!r.Flag())
                        continue;
                    for (var k = 0; k < 64 && (r.Read(8) != 0 || k == 0); k++)
                    {
                    }
                }
            }

            if (verid != 1 && r.Flag())
                tools.Insert(0, "QPel");
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }

        return tools;
    }

    /// <summary>The encoder of user data such as "DivX503b1393p" or "XviD0050"; empty for other user data.</summary>
    private static string Encoder(ReadOnlySpan<byte> payload)
    {
        var end = 0;
        while (end < payload.Length && end < 64 && payload[end] is >= 0x20 and < 0x7F)
            end++;
        var text = Encoding.ASCII.GetString(payload[..end]);
        if (DivX().Match(text) is { Success: true } divx)
        {
            var version = divx.Groups[1].Value;
            return string.Create(CultureInfo.InvariantCulture, $"DivX {version[0]}.{version[1..]} build {divx.Groups[2].Value}");
        }

        if (Xvid().Match(text) is { Success: true } xvid)
            return "Xvid build " + int.Parse(xvid.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return text.StartsWith("Lavc", StringComparison.Ordinal) ? text : string.Empty;
    }

    [GeneratedRegex(@"^DivX(\d{3,})(?:Build|b)(\d+)")]
    private static partial Regex DivX();

    [GeneratedRegex(@"^XviD(\d+)")]
    private static partial Regex Xvid();
}

/// <summary>VC-1 (SMPTE 421M) and WMV 9 sequence headers.</summary>
public static class Vc1
{
    /// <summary>
    /// Profile and level ("Advanced@L3", "Main") and the colour of the display extension, from a Video for Windows
    /// codec's extra data: the Advanced profile sequence header (WVC1) or STRUCT_C (WMV3); null for other codecs.
    /// </summary>
    public static LegacyVideoInfo? Describe(string fourCc, ReadOnlySpan<byte> extra)
    {
        for (var i = 0; i + 4 < extra.Length; i++)
        {
            if (extra[i] == 0 && extra[i + 1] == 0 && extra[i + 2] == 1 && extra[i + 3] == 0x0F)
                return Advanced(extra[(i + 4)..]);
        }

        if (extra.Length >= 4 && fourCc.Equals("WMV3", StringComparison.OrdinalIgnoreCase))
        {
            var profile = (extra[0] >> 6) switch
            {
                0 => "Simple",
                1 => "Main",
                2 => "Complex",
                _ => string.Empty,
            };
            return new LegacyVideoInfo(profile, ColorInfo.Unspecified);
        }

        return null;
    }

    private static LegacyVideoInfo Advanced(ReadOnlySpan<byte> header)
    {
        var s = ParseSequence(header);
        var name = s.Profile == 3 ? $"Advanced@L{s.Level}" : string.Empty;
        return new LegacyVideoInfo(s.Interlaced && name.Length > 0 ? name + ", interlaced" : name, s.Color);
    }

    /// <summary>
    /// The fields of an Advanced profile sequence header (after its 00 00 01 0F start code) that describe the track:
    /// coded size, interlace, frame rate and colour of the display extension.
    /// </summary>
    public static Vc1Sequence ParseSequence(ReadOnlySpan<byte> header)
    {
        var r = new BitReader(header);
        int profile = 0, level = 0, width = 0, height = 0;
        var interlaced = false;
        double rate = 0;
        var color = ColorInfo.Unspecified;
        try
        {
            profile = (int)r.Read(2);
            level = (int)r.Read(3);
            r.Skip(2 + 3 + 5 + 1); // colordiff_format, frame/bit rate quantisers, postprocflag
            width = (int)(r.Read(12) + 1) * 2;
            height = (int)(r.Read(12) + 1) * 2;
            r.Skip(1); // pulldown
            interlaced = r.Flag();
            r.Skip(4); // tfcntrflag, finterpflag, reserved, psf
            if (r.Flag()) // display_ext
            {
                r.Skip(28); // display size
                if (r.Flag() && r.Read(4) == 15)
                    r.Skip(16);
                if (r.Flag())
                {
                    if (r.Flag())
                    {
                        rate = (r.Read(16) + 1) / 32.0; // framerateexp
                    }
                    else
                    {
                        var nr = r.Read(8);
                        var dr = r.Read(4);
                        double[] rates = [0, 24, 25, 30, 50, 60, 48, 72];
                        rate = nr is > 0 and < 8 && dr is 1 or 2 ? rates[nr] * 1000 / (dr == 1 ? 1000 : 1001) : 0;
                    }
                }

                if (r.Flag())
                    color = new ColorInfo((int)r.Read(8), (int)r.Read(8), (int)r.Read(8));
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }

        return new Vc1Sequence(profile, level, width, height, interlaced, rate, color);
    }

    /// <summary>
    /// The 'dvc1' payload (SMPTE RP 2025 VC1DecSpecStruc, as FFmpeg writes it) for an Advanced profile stream: profile,
    /// level, flags, the integer frame rate, then the sequence header and entry point with their start codes.
    /// </summary>
    public static byte[] BuildDvc1(ReadOnlySpan<byte> sequenceAndEntryPoint, Vc1Sequence sequence)
    {
        var w = new BitWriter();
        w.Write(12, 4); // profile: advanced
        w.Write((ulong)sequence.Level, 3);
        w.Write(0, 1);
        w.Write((ulong)sequence.Level, 3);
        w.Write(0, 1); // cbr
        w.Write(0, 6);
        w.Flag(!sequence.Interlaced);
        w.Flag(true); // no_multiple_seq
        w.Flag(true); // no_multiple_entry
        w.Flag(true); // no_slice_code
        w.Flag(false); // no_bframe: B frames may be present
        w.Write(0, 1);
        w.Write(sequence.FrameRate > 0 ? (ulong)Math.Round(sequence.FrameRate) : 0xFFFFFFFF, 32);
        return [.. w.ToArray(), .. sequenceAndEntryPoint];
    }

    /// <summary>A 'vc-1' sample entry with its 'dvc1', for an Advanced profile stream that comes without one.</summary>
    public static byte[] BuildEntry(ReadOnlySpan<byte> sequenceAndEntryPoint, Vc1Sequence sequence) =>
        QuickTime.VisualEntry("vc-1", sequence.Width, sequence.Height, QuickTime.Box("dvc1", BuildDvc1(sequenceAndEntryPoint, sequence)));

    /// <summary>The sequence header fields of a 'vc-1' sample entry's 'dvc1'; null when it has none.</summary>
    public static Vc1Sequence? EntrySequence(ReadOnlySpan<byte> entry)
    {
        if (QuickTime.EntryBox(entry, "dvc1") is not { Length: > 7 } dvc1)
            return null;
        var data = dvc1.AsSpan(7);
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == 0x0F)
                return ParseSequence(data[(i + 4)..]);
        }

        return null;
    }
}

/// <summary>Fields of a VC-1 Advanced profile sequence header.</summary>
public sealed record Vc1Sequence(int Profile, int Level, int Width, int Height, bool Interlaced, double FrameRate, ColorInfo Color);

/// <summary>DV (IEC 61834, SMPTE 314M) frames: DIF header and video auxiliary packs.</summary>
public static class DvVideo
{
    private const int DifBlock = 80;

    /// <summary>
    /// The DV variant, system and sampling ("DV 625/50 4:2:0", "DVCPRO50 525/60") and the display aspect ratio of a
    /// frame; null when it does not start with a DIF header.
    /// </summary>
    public static LegacyVideoInfo? Describe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < DifBlock * 6 || frame[0] >> 5 != 0)
            return null; // section type 0: header
        var pal = (frame[3] & 0x80) != 0;
        var apt = frame[4] & 7;
        ReadOnlySpan<byte> vs = default, vsc = default;
        for (var block = 3; block < 6; block++)
        {
            for (var pack = 0; pack < 15; pack++)
            {
                var at = block * DifBlock + 3 + pack * 5;
                switch (frame[at])
                {
                    case 0x60 when vs.IsEmpty:
                        vs = frame.Slice(at, 5);
                        break;
                    case 0x61 when vsc.IsEmpty:
                        vsc = frame.Slice(at, 5);
                        break;
                }
            }
        }

        var stype = vs.IsEmpty ? 0 : vs[3] & 0x1F;
        var system = pal ? "625/50" : "525/60";
        var name = stype switch
        {
            0 when apt == 0 => $"DV {system} {(pal ? "4:2:0" : "4:1:1")}",
            0 => $"DVCPRO {system} 4:1:1",
            4 => $"DVCPRO50 {system} 4:2:2",
            0x14 or 0x15 => $"DVCPRO HD 1080{(pal ? "i50" : "i60")}",
            0x18 => $"DVCPRO HD 720p{(pal ? "50" : "60")}",
            _ => $"DV {system}",
        };
        var display = vsc.IsEmpty ? -1 : vsc[2] & 7;
        if (display == 2 || display == 7 && apt == 0)
            name += ", 16:9";
        else if (display == 0)
            name += ", 4:3";
        return new LegacyVideoInfo(name, ColorInfo.Unspecified);
    }
}
