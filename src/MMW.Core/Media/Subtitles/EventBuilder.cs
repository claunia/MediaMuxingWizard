using System.Text;

namespace MMW.Core.Media.Subtitles;

/// <summary>Collects an event's text while a markup is parsed: style changes become runs, karaoke tags syllables.</summary>
internal sealed class EventBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<SubtitleRun> _runs = [];
    private readonly List<KaraokeSyllable> _karaoke = [];
    private SubtitleStyle _style = new();
    private int _runStart;
    private (long Start, long Duration, KaraokeKind Kind, int TextStart)? _syllable;
    private long _karaokeTime;

    /// <summary>The formatting in effect (over the event's style sheet).</summary>
    public SubtitleStyle Style => _style;

    public int Length => _text.Length;

    public void Append(char c) => _text.Append(c);

    public void Append(string s) => _text.Append(s);

    /// <summary>Changes the formatting from here on.</summary>
    public void SetStyle(SubtitleStyle style)
    {
        if (style == _style)
            return;
        CloseRun();
        _style = style;
    }

    /// <summary>Starts a karaoke syllable of <paramref name="durationMs"/> here (ends the previous one).</summary>
    public void Syllable(long durationMs, KaraokeKind kind)
    {
        CloseSyllable();
        _syllable = (_karaokeTime, durationMs, kind, _text.Length);
        _karaokeTime += durationMs;
    }

    /// <summary>Starts a syllable sung from <paramref name="startMs"/> (WebVTT timestamps); its duration is set by the next one.</summary>
    public void SyllableAt(long startMs, KaraokeKind kind)
    {
        if (_syllable is { } open)
            _syllable = open with { Duration = Math.Max(0, startMs - open.Start) };
        else if (_text.Length > 0 && startMs > 0)
            _karaoke.Add(new KaraokeSyllable(0, _text.Length, 0, startMs, kind)); // text before the first timestamp
        CloseSyllable();
        _syllable = (startMs, 0, kind, _text.Length);
        _karaokeTime = startMs;
    }

    private void CloseRun()
    {
        if (!_style.IsEmpty && _text.Length > _runStart)
            _runs.Add(new SubtitleRun(_runStart, _text.Length, _style));
        _runStart = _text.Length;
    }

    private void CloseSyllable()
    {
        if (_syllable is { } s && (_text.Length > s.TextStart || s.Duration > 0))
            _karaoke.Add(new KaraokeSyllable(s.TextStart, _text.Length, s.Start, s.Duration, s.Kind));
        _syllable = null;
    }

    /// <summary>
    /// The event: line breaks at the ends removed (runs and syllables kept inside the text); a last open WebVTT
    /// syllable lasts until <paramref name="eventDurationMs"/>.
    /// </summary>
    public SubtitleEvent Build(SubtitleEvent template, long eventDurationMs = 0)
    {
        if (_syllable is { Duration: 0 } open && eventDurationMs > open.Start)
            _syllable = open with { Duration = eventDurationMs - open.Start };
        CloseRun();
        CloseSyllable();
        var text = _text.ToString();
        var start = 0;
        while (start < text.Length && text[start] == '\n')
            start++;
        var end = text.Length;
        while (end > start && text[end - 1] is '\n' or ' ')
            end--;
        var trimmed = text[start..end];
        int Clamp(int i) => Math.Clamp(i - start, 0, trimmed.Length);
        return template with
        {
            Text = trimmed,
            Runs = _runs.Select(r => r with { Start = Clamp(r.Start), End = Clamp(r.End) }).Where(r => r.End > r.Start).ToList(),
            Karaoke = _karaoke.Select(k => k with { Start = Clamp(k.Start), End = Clamp(k.End) }).ToList(),
        };
    }
}
