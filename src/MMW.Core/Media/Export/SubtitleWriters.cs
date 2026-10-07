using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// Base of the subtitle writers: every sample becomes a cue from its presentation time to the end of its duration (or,
/// without one, to the start of the next sample), in milliseconds on the output timeline.
/// </summary>
internal abstract class CueWriter(ExportContext context) : TrackWriter(context)
{
    private static readonly UTF8Encoding s_utf8 = new(false);

    private MediaSample? _pending;
    private byte[] _pendingData = [];

    /// <summary>Writes one cue.</summary>
    protected abstract void WriteCue(double start, double end, byte[] data);

    public override void Write(MediaSample sample)
    {
        if (_pending is not null)
            Emit(_pending, _pendingData, sample.Pts);
        _pending = sample;
        _pendingData = sample.GetData().ToArray();
    }

    public override void Finish()
    {
        if (_pending is not null)
            Emit(_pending, _pendingData, null);
        _pending = null;
        Complete();
    }

    /// <summary>Called after the last cue.</summary>
    protected virtual void Complete()
    {
    }

    private void Emit(MediaSample sample, byte[] data, long? nextPts)
    {
        var duration = sample.Duration > 0 ? sample.Duration
            : nextPts is { } next && next > sample.Pts ? next - sample.Pts
            : Config.DefaultSampleDuration > 0 ? Config.DefaultSampleDuration
            : Config.Timescale * 2L;
        var start = Math.Max(0, Context.Milliseconds(sample.Pts));
        var end = Math.Max(start, Context.Milliseconds(sample.Pts + duration));
        WriteCue(start, end, data, sample.CueSettings);
    }

    /// <summary>Writes one cue with its WebVTT cue settings (null when it has none); writers without them ignore them.</summary>
    protected virtual void WriteCue(double start, double end, byte[] data, string? cueSettings) => WriteCue(start, end, data);

    protected void WriteText(string text) => Output.Write(s_utf8.GetBytes(text));

    protected static string Utf8(byte[] data) => Encoding.UTF8.GetString(data).TrimEnd('\0');

    /// <summary>Line breaks as "\n", surrounding blank lines removed.</summary>
    protected static string Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');

    /// <summary>"hh:mm:ss{separator}mmm".</summary>
    protected static string Time(double milliseconds, char separator)
    {
        var ms = (long)Math.Round(milliseconds);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}");
    }
}

/// <summary>SubRip (.srt) from plain UTF-8 text or tx3g samples; empty cues (tx3g gaps) are skipped.</summary>
internal sealed class SrtWriter(ExportContext context) : CueWriter(context)
{
    private int _number;

    protected override void WriteCue(double start, double end, byte[] data)
    {
        var text = Config.Codec == CodecType.Tx3g ? SubtitleText.ToSrt(SubtitleText.FromTx3g(data)) : Utf8(data);
        text = Lines(text);
        if (text.Trim().Length == 0)
            return;
        _number++;
        WriteText(string.Create(CultureInfo.InvariantCulture, $"{_number}\n{Time(start, ',')} --> {Time(end, ',')}\n{text}\n\n"));
    }
}

/// <summary>WebVTT (.vtt): the track's header, then the cues.</summary>
internal sealed class WebVttWriter(ExportContext context) : CueWriter(context)
{
    public override void Start()
    {
        var header = Config.Extradata is { Length: > 0 } h ? Lines(Utf8(h)) : string.Empty;
        if (!header.StartsWith("WEBVTT", StringComparison.Ordinal))
            header = header.Length > 0 ? "WEBVTT\n\n" + header : "WEBVTT";
        WriteText(header + "\n\n");
    }

    protected override void WriteCue(double start, double end, byte[] data) => WriteCue(start, end, data, null);

    protected override void WriteCue(double start, double end, byte[] data, string? cueSettings)
    {
        var text = Lines(Utf8(data));
        if (text.Trim().Length == 0)
            return;
        var settings = string.IsNullOrWhiteSpace(cueSettings) ? string.Empty : " " + cueSettings.Trim();
        WriteText($"{Time(start, '.')} --> {Time(end, '.')}{settings}\n{text}\n\n");
    }
}

/// <summary>
/// (Advanced) SubStation Alpha (.ass/.ssa): the track's header (script info, styles), then the [Events] section with a
/// Dialogue line per block ("ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text"; SSA has Marked in place of
/// Layer), in ReadOrder, with the fields placed as the header's Format line orders them.
/// </summary>
internal sealed class AssWriter(ExportContext context) : CueWriter(context)
{
    private const string AssFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    private const string SsaFormat = "Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    private readonly List<(long Order, int Index, string Line)> _events = [];
    private string[] _format = [];

    private bool Ssa => Config.Codec == CodecType.Ssa;

    public override void Start()
    {
        var header = Config.Extradata is { Length: > 0 } h ? Lines(Utf8(h)) : string.Empty;
        if (header.Length == 0)
            header = Ssa ? "[Script Info]\nScriptType: v4.00" : "[Script Info]\nScriptType: v4.00+";
        var lines = header.Split('\n').ToList();
        var events = lines.FindIndex(l => l.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase));
        if (events < 0)
        {
            lines.Add(string.Empty);
            lines.Add("[Events]");
            events = lines.Count - 1;
        }

        var formatLine = lines.Skip(events + 1).FirstOrDefault(l => l.StartsWith("Format:", StringComparison.OrdinalIgnoreCase));
        if (formatLine is null)
        {
            formatLine = Ssa ? SsaFormat : AssFormat;
            lines.Insert(events + 1, formatLine);
        }

        _format = formatLine[7..].Split(',', StringSplitOptions.TrimEntries).Select(f => f.ToLowerInvariant()).ToArray();
        WriteText(string.Join('\n', lines) + "\n");
    }

    protected override void WriteCue(double start, double end, byte[] data)
    {
        var block = Lines(Utf8(data));
        var fields = new string[9];
        var pos = 0;
        for (var i = 0; i < 8; i++)
        {
            var comma = block.IndexOf(',', pos);
            if (comma < 0)
            {
                fields[i] = string.Empty;
                continue;
            }

            fields[i] = block[pos..comma];
            pos = comma + 1;
        }

        fields[8] = block[pos..].Replace("\n", "\\N", StringComparison.Ordinal);
        var order = long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : _events.Count;
        var layer = fields[1];
        if (Ssa && layer.Length > 0 && !layer.StartsWith("Marked", StringComparison.OrdinalIgnoreCase))
            layer = "Marked=" + layer;
        var values = _format.Select(name => name switch
        {
            "layer" or "marked" => layer.Length > 0 ? layer : Ssa ? "Marked=0" : "0",
            "start" => AssTime(start),
            "end" => AssTime(end),
            "style" => fields[2],
            "name" or "actor" => fields[3],
            "marginl" => fields[4],
            "marginr" => fields[5],
            "marginv" => fields[6],
            "effect" => fields[7],
            "text" => fields[8],
            _ => string.Empty,
        });
        _events.Add((order, _events.Count, "Dialogue: " + string.Join(',', values)));
    }

    protected override void Complete()
    {
        foreach (var (_, _, line) in _events.OrderBy(e => e.Order).ThenBy(e => e.Index))
            WriteText(line + "\n");
    }

    /// <summary>"h:mm:ss.cc".</summary>
    private static string AssTime(double milliseconds)
    {
        var cs = (long)Math.Round(milliseconds / 10);
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }
}

/// <summary>
/// PGS (.sup): every segment of a display set behind a "PG" header with its presentation time (90 kHz) and a zero
/// decoding time.
/// </summary>
internal sealed class SupWriter(ExportContext context) : CueWriter(context)
{
    protected override void WriteCue(double start, double end, byte[] data)
    {
        if (data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'G')
        {
            Output.Write(data); // already in .sup form
            return;
        }

        var pts = (uint)Math.Round(start * 90);
        Span<byte> h = stackalloc byte[10];
        "PG"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h[2..], pts);
        BinaryPrimitives.WriteUInt32BigEndian(h[6..], 0);
        var pos = 0;
        while (pos + 3 <= data.Length)
        {
            var length = 3 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 1));
            if (pos + length > data.Length)
                length = data.Length - pos;
            Output.Write(h);
            Output.Write(data.AsSpan(pos, length));
            pos += length;
        }
    }
}

/// <summary>
/// VobSub (.idx + .sub, as mkvextract writes them): the SPU packets in MPEG-2 program stream packs of 2048 bytes
/// (private stream 1, sub-stream 0x20) in the .sub, the header and a timestamp / file position line per packet in the
/// .idx.
/// </summary>
internal sealed class VobSubWriter(ExportContext context) : CueWriter(context)
{
    private const int PackSize = 2048;
    private const int MuxRate = 25200; // 10.08 Mbit/s, the DVD rate

    private Stream Sub => Context.Companion!;

    public override void Start()
    {
        var header = Config.Extradata is { Length: > 0 } h ? Lines(Utf8(h)) : string.Empty;
        if (!header.StartsWith("# VobSub index file", StringComparison.Ordinal))
            header = "# VobSub index file, v7 (do not modify this line!)\n" + header;
        if (!header.Contains("size:", StringComparison.Ordinal) && Config.SubtitleWidth > 0)
            header += string.Create(CultureInfo.InvariantCulture, $"\nsize: {Config.SubtitleWidth}x{Config.SubtitleHeight}");
        WriteText(header + "\n\nlangidx: 0\n\n" + $"id: {LanguageCode(Config.Language)}, index: 0\n");
    }

    /// <summary>The two-letter code of a language tag ("en" for "eng" or "en-US"); "en" when unknown.</summary>
    private static string LanguageCode(string language)
    {
        if (string.IsNullOrEmpty(language) || language == "und")
            return "en";
        var primary = language.Split('-')[0];
        if (primary.Length == 2)
            return primary.ToLowerInvariant();
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.ThreeLetterISOLanguageName.Equals(primary, StringComparison.OrdinalIgnoreCase) && culture.TwoLetterISOLanguageName.Length == 2)
                return culture.TwoLetterISOLanguageName;
        }

        return "en";
    }

    protected override void WriteCue(double start, double end, byte[] data)
    {
        var position = Sub.Position;
        var ms = (long)Math.Round(start);
        WriteText(string.Create(CultureInfo.InvariantCulture,
            $"timestamp: {ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}:{ms % 1000:000}, filepos: {position:x9}\n"));
        var pts = (long)Math.Round(start * 90);
        var offset = 0;
        var first = true;
        var pack = new byte[PackSize];
        do
        {
            var pesHeader = 9 + (first ? 5 : 0) + 1; // start code, length, flags, header length, [PTS], sub-stream id
            var space = PackSize - 14 - pesHeader;
            var chunk = Math.Min(space, data.Length - offset);
            var padding = space - chunk;
            var stuffing = padding < 6 ? padding : 0;
            var paddingPacket = padding - stuffing;

            Array.Clear(pack);
            PackHeader(pack, pts);
            var p = 14;
            pack[p++] = 0;
            pack[p++] = 0;
            pack[p++] = 1;
            pack[p++] = 0xBD;
            BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(p), (ushort)(3 + (first ? 5 : 0) + stuffing + 1 + chunk));
            p += 2;
            pack[p++] = 0x81;
            pack[p++] = first ? (byte)0x80 : (byte)0;
            pack[p++] = (byte)((first ? 5 : 0) + stuffing);
            if (first)
            {
                pack[p++] = (byte)(0x21 | ((pts >> 29) & 0x0E));
                pack[p++] = (byte)(pts >> 22);
                pack[p++] = (byte)(((pts >> 14) & 0xFE) | 1);
                pack[p++] = (byte)(pts >> 7);
                pack[p++] = (byte)(((pts << 1) & 0xFE) | 1);
            }

            for (var i = 0; i < stuffing; i++)
                pack[p++] = 0xFF;
            pack[p++] = 0x20;
            data.AsSpan(offset, chunk).CopyTo(pack.AsSpan(p));
            p += chunk;
            offset += chunk;
            if (paddingPacket > 0)
            {
                pack[p++] = 0;
                pack[p++] = 0;
                pack[p++] = 1;
                pack[p++] = 0xBE;
                BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(p), (ushort)(paddingPacket - 6));
                p += 2;
                pack.AsSpan(p, paddingPacket - 6).Fill(0xFF);
            }

            Sub.Write(pack);
            first = false;
        }
        while (offset < data.Length);
    }

    /// <summary>An MPEG-2 pack header with the system clock reference at <paramref name="scr"/> (90 kHz).</summary>
    private static void PackHeader(Span<byte> b, long scr)
    {
        b[0] = 0;
        b[1] = 0;
        b[2] = 1;
        b[3] = 0xBA;
        b[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        b[5] = (byte)(scr >> 20);
        b[6] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
        b[7] = (byte)(scr >> 5);
        b[8] = (byte)(((scr << 3) & 0xF8) | 0x04);
        b[9] = 0x01;
        b[10] = MuxRate >> 14;
        b[11] = (MuxRate >> 6) & 0xFF;
        b[12] = ((MuxRate << 2) & 0xFC) | 0x03;
        b[13] = 0xF8;
    }
}
