using System.Text;
using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Formats.MpegTs;

/// <summary>
/// A teletext subtitle page (EN 300 472 in DVB; ETS 300 706 teletext) decoded to text cues. Each page broadcast is
/// complete when the next header of its magazine arrives; its text (the boxed characters of rows 1–23, with the
/// national character subset of the page) is shown from its header until the page changes or is erased.
/// </summary>
internal sealed class TeletextStream : TsStream
{
    private static readonly byte[] s_reverse = BuildReverse();
    private static readonly sbyte[] s_unham = BuildUnham();

    // National option subsets (ETS 300 706 table 36) for C12–C14 (C12 = bit 0): characters replacing
    // 0x23 0x24 0x40 0x5B 0x5C 0x5D 0x5E 0x5F 0x60 0x7B 0x7C 0x7D 0x7E of the Latin G0 set.
    private static readonly string[] s_national =
    [
        "£$@←½→↑#—¼‖¾÷", // English
        "éïàëêùî#èâôûç", // French
        "#¤ÉÄÖÅÜ_éäöåü", // Swedish/Finnish/Hungarian
        "#ůčťžýířéáěúš", // Czech/Slovak
        "#$§ÄÖÜ^_°äöüß", // German
        "ç$¡áéíóú¿üñèà", // Portuguese/Spanish
        "£$é°ç→↑#ùàòèì", // Italian
        "#$@[\\]^_`{|}~", // (unused: plain ASCII)
    ];

    private static readonly byte[] s_nationalPositions = [0x23, 0x24, 0x40, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x7B, 0x7C, 0x7D, 0x7E];

    private readonly int _magazine; // 1–8
    private readonly int _page; // 0x00–0xFF (e.g. 0x88)
    private readonly string _language;
    private readonly bool _hearingImpaired;
    private readonly string?[] _rows = new string?[24];
    private bool _receiving;
    private int _charset;
    private long _headerTime;
    private (long Start, string Text)? _shown;
    private long _lastTime;
    private bool _seen;

    public TeletextStream(TsStreamInfo info, int magazine, int page, string language, bool hearingImpaired) : base(info)
    {
        _magazine = magazine;
        _page = page;
        _language = language;
        _hearingImpaired = hearingImpaired;
    }

    /// <summary>Page number as viewers know it ("888").</summary>
    public int PageNumber => _magazine * 100 + (_page >> 4) * 10 + (_page & 0x0F);

    /// <summary>Teletext subtitle pages announced by a PMT entry's teletext descriptor (type 2, or 5 for the hard of hearing).</summary>
    public static IEnumerable<(int Magazine, int Page, string Language, bool HearingImpaired)> SubtitlePages(TsStreamInfo info)
    {
        if (info.Descriptor(0x56) is not { } d)
            yield break;
        for (var i = 0; i + 5 <= d.Length; i += 5)
        {
            var type = d[i + 3] >> 3;
            if (type is not (2 or 5))
                continue;
            var magazine = d[i + 3] & 7;
            yield return (magazine == 0 ? 8 : magazine, d[i + 4], Encoding.ASCII.GetString(d, i, 3), type == 5);
        }
    }

    public override uint Timescale => 90000;

    public override bool Ready => _seen;

    public override void OnPes(Pes pes, Queue<MediaSample> output)
    {
        var d = pes.Data;
        if (d.Length < 1 || d[0] is < 0x10 or > 0x1F) // data_identifier: EBU data
            return;
        _seen = true;
        var time = pes.Pts ?? _lastTime;
        _lastTime = time;
        for (var pos = 1; pos + 2 <= d.Length;)
        {
            var id = d[pos];
            var length = d[pos + 1];
            if (pos + 2 + length > d.Length)
                break;
            if (id is 0x02 or 0x03 && length == 44)
                OnPacket(d.AsSpan(pos + 2, 44), time, output);
            pos += 2 + length;
        }
    }

    public override void Flush(Queue<MediaSample> output)
    {
        if (_receiving)
            Commit(output);
        End(_lastTime + 90000, output); // the last cue: a second after the last packet
    }

    private void OnPacket(ReadOnlySpan<byte> unit, long time, Queue<MediaSample> output)
    {
        // Bytes are carried bit-reversed: [0] field parity/line offset, [1] framing code, [2..3] address, [4..43] data.
        Span<byte> p = stackalloc byte[44];
        for (var i = 0; i < 44; i++)
            p[i] = s_reverse[unit[i]];
        var a0 = s_unham[p[2]];
        var a1 = s_unham[p[3]];
        if (a0 < 0 || a1 < 0)
            return;
        var address = (a1 << 4) | a0;
        var magazine = address & 7;
        if (magazine == 0)
            magazine = 8;
        var row = address >> 3;
        if (magazine != _magazine)
            return;
        var data = p[4..];

        if (row == 0)
        {
            // Page header: the page being received in this magazine is complete.
            if (_receiving)
                Commit(output);
            var units = s_unham[data[0]];
            var tens = s_unham[data[1]];
            if (units < 0 || tens < 0)
            {
                _receiving = false;
                return;
            }

            _receiving = ((tens << 4) | units) == _page;
            if (!_receiving)
                return;
            var s2 = s_unham[data[3]];
            var c11to14 = s_unham[data[7]];
            if (s2 >= 0 && (s2 & 8) != 0) // C4: erase page
                Array.Clear(_rows);
            _charset = c11to14 >= 0 ? (c11to14 >> 1) & 7 : 0;
            _headerTime = time;
            return;
        }

        if (_receiving && row is >= 1 and <= 23)
            _rows[row] = DecodeRow(data);
    }

    /// <summary>The boxed text of a row (characters between start box 0x0B and end box 0x0A), or the whole row without boxes.</summary>
    private string DecodeRow(ReadOnlySpan<byte> data)
    {
        var national = s_national[_charset];
        var boxed = false;
        foreach (var raw in data)
            boxed |= (raw & 0x7F) == 0x0B;
        var inBox = !boxed;
        var text = new StringBuilder(40);
        foreach (var raw in data)
        {
            var c = raw & 0x7F; // odd parity bit
            if (c == 0x0B)
            {
                inBox = true;
                text.Append(' ');
                continue;
            }

            if (c == 0x0A)
            {
                inBox = !boxed;
                text.Append(' ');
                continue;
            }

            if (!inBox)
                continue;
            if (c < 0x20)
            {
                text.Append(' '); // spacing attributes (colours, size …)
                continue;
            }

            var n = Array.IndexOf(s_nationalPositions, (byte)c);
            text.Append(c == 0x7F ? '■' : n >= 0 ? national[n] : (char)c);
        }

        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A received page: its text replaces what is shown, from the time of its header.</summary>
    private void Commit(Queue<MediaSample> output)
    {
        _receiving = false;
        var text = string.Join('\n', _rows.Where(r => !string.IsNullOrEmpty(r)));
        if (_shown is { } shown && shown.Text == text)
            return;
        End(_headerTime, output);
        if (text.Length > 0)
            _shown = (_headerTime, text);
    }

    private void End(long time, Queue<MediaSample> output)
    {
        if (_shown is not { } shown)
            return;
        _shown = null;
        output.Enqueue(new MediaSample
        {
            Dts = shown.Start,
            Duration = Math.Max(1, time - shown.Start),
            IsSync = true,
            Data = Encoding.UTF8.GetBytes(shown.Text),
        });
    }

    public override CodecConfig Describe() => Base(TrackKind.Subtitle) with
    {
        Codec = CodecType.TextUtf8,
        Language = MMW.Core.Languages.LanguageTable.ToBcp47(_language),
        Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Teletext {PageNumber}{(_hearingImpaired ? " (hard of hearing)" : string.Empty)}"),
    };

    private static byte[] BuildReverse()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var b = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((i & (1 << bit)) != 0)
                    b |= 0x80 >> bit;
            }

            table[i] = (byte)b;
        }

        return table;
    }

    /// <summary>Hamming 8/4 decoding (ETS 300 706 §8.2) with single-bit error correction; -1 for double errors.</summary>
    private static sbyte[] BuildUnham()
    {
        byte[] codes = [0x15, 0x02, 0x49, 0x5E, 0x64, 0x73, 0x38, 0x2F, 0xD0, 0xC7, 0x8C, 0x9B, 0xA1, 0xB6, 0xFD, 0xEA];
        var table = new sbyte[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = -1;
            for (var v = 0; v < 16; v++)
            {
                if (int.PopCount(i ^ codes[v]) <= 1)
                {
                    table[i] = (sbyte)v;
                    break;
                }
            }
        }

        return table;
    }
}
