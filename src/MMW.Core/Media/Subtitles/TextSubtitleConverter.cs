using System.Text;
using MMW.Core.Model;

namespace MMW.Core.Media.Subtitles;

/// <summary>
/// Converts a text subtitle track (SubRip, ASS, SSA, WebVTT, tx3g) to another text format through
/// <see cref="SubtitleScript"/>, keeping what the target can hold: styles, colours, fonts, sizes, positions,
/// alignment, vertical text, karaoke and forced cues. What it cannot hold is approximated (tx3g positions become text
/// boxes, SubRip drops positions and karaoke). The whole track is read when the converter is created (the target's
/// header lists its styles and fonts); samples are in milliseconds.
/// </summary>
public sealed class TextSubtitleConverter : ISampleSource
{
    private readonly ISampleSource _source;
    private readonly List<MediaSample> _samples;
    private int _next;

    /// <param name="source">The track, positioned at its first sample (it is rewound afterwards).</param>
    /// <param name="target">SubRip (<see cref="CodecType.TextUtf8"/>), ASS, SSA, WebVTT or tx3g.</param>
    /// <param name="canvasWidth">Video width the subtitles are shown on (0: keep the source's coordinate space).</param>
    /// <param name="canvasHeight">Video height.</param>
    public TextSubtitleConverter(ISampleSource source, CodecType target, int canvasWidth = 0, int canvasHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CodecNames.IsText(source.Config.Codec) || !CodecNames.IsText(target))
            throw new NotSupportedException($"{source.Config.FormatName} cannot be converted to {CodecNames.Display(target)}.");
        _source = source;
        var script = Read(source, canvasWidth, canvasHeight);
        (Config, _samples) = Write(script, source.Config, target, canvasWidth, canvasHeight);
        MediaStart = (long)Math.Round(source.MediaStart * 1000.0 / Math.Max(1u, source.Config.Timescale));
    }

    /// <summary>The text format an import action converts to; null when it is not a text conversion.</summary>
    public static CodecType? Target(ImportAction action) => action switch
    {
        ImportAction.ConvertToTx3g => CodecType.Tx3g,
        ImportAction.ConvertToSrt => CodecType.TextUtf8,
        ImportAction.ConvertToAss => CodecType.Ass,
        ImportAction.ConvertToSsa => CodecType.Ssa,
        ImportAction.ConvertToWebVtt => CodecType.WebVtt,
        _ => null,
    };

    /// <summary>The import action converting text subtitles to <paramref name="codec"/>.</summary>
    public static ImportAction Action(CodecType codec) => codec switch
    {
        CodecType.Tx3g => ImportAction.ConvertToTx3g,
        CodecType.TextUtf8 => ImportAction.ConvertToSrt,
        CodecType.Ass => ImportAction.ConvertToAss,
        CodecType.Ssa => ImportAction.ConvertToSsa,
        CodecType.WebVtt => ImportAction.ConvertToWebVtt,
        _ => ImportAction.Passthrough,
    };

    /// <summary>True when <paramref name="action"/> converts the text subtitle track <paramref name="config"/> to another format.</summary>
    public static bool Converts(CodecConfig config, ImportAction action)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == TrackKind.Subtitle && CodecNames.IsText(config.Codec) && Target(action) is { } target && target != config.Codec;
    }

    /// <summary>The configuration a conversion of <paramref name="input"/> to <paramref name="target"/> produces (for support checks).</summary>
    public static CodecConfig PredictOutput(CodecConfig input, CodecType target)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CodecConfig
        {
            Codec = target,
            Kind = TrackKind.Subtitle,
            SourceCodecId = SourceId(target),
            Timescale = 1000,
            Language = input.Language,
            Name = input.Name,
            SubtitleWidth = input.SubtitleWidth,
            SubtitleHeight = input.SubtitleHeight,
        };
    }

    private static string SourceId(CodecType codec) => codec switch
    {
        CodecType.Tx3g => "tx3g",
        CodecType.Ass => "S_TEXT/ASS",
        CodecType.Ssa => "S_TEXT/SSA",
        CodecType.WebVtt => "D_WEBVTT/SUBTITLES",
        _ => "S_TEXT/UTF8",
    };

    // ------------------------------------------------------------------ reading

    /// <summary>The whole track as a script (times in milliseconds of the source's timeline).</summary>
    public static SubtitleScript Read(ISampleSource source, int canvasWidth = 0, int canvasHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        var config = source.Config;
        var timescale = Math.Max(1u, config.Timescale);
        long Ms(long ticks) => (long)Math.Round(ticks * 1000.0 / timescale);
        var header = config.Extradata is { Length: > 0 } x ? Encoding.UTF8.GetString(x) : null;
        SubtitleScript script;
        WebVttFormat.Header? vtt = null;
        Tx3gFormat.Description? tx3g = null;
        switch (config.Codec)
        {
            case CodecType.Ass or CodecType.Ssa:
                script = AssFormat.ReadHeader(header);
                break;
            case CodecType.WebVtt:
                vtt = WebVttFormat.ReadHeader(header);
                script = vtt.Script;
                break;
            case CodecType.Tx3g:
                (script, tx3g) = Tx3gFormat.ReadDescription(config.Extradata, config.SubtitleWidth, config.SubtitleHeight);
                break;
            default:
            {
                // SubRip has no canvas: its text is laid out on the video's (or a 1080p one).
                var w = canvasWidth > 0 ? canvasWidth : 1920;
                var h = canvasHeight > 0 ? canvasHeight : 1080;
                script = new SubtitleScript { Width = w, Height = h };
                script.Styles.Add(new SubtitleStyleSheet
                {
                    Style = SubtitleStyleSheet.DefaultStyle(Math.Round(h * 0.055)),
                    MarginL = (int)(w * 0.03),
                    MarginR = (int)(w * 0.03),
                    MarginV = (int)(h * 0.04),
                    OutlineWidth = Math.Round(h / 360.0, 1),
                    Shadow = Math.Round(h / 540.0, 1),
                });
                break;
            }
        }

        source.Reset();
        try
        {
            var pending = new List<(MediaSample Sample, byte[] Data)>();
            while (source.ReadNext() is { } sample)
                pending.Add((sample, sample.GetData().ToArray()));
            for (var i = 0; i < pending.Count; i++)
            {
                var (sample, data) = pending[i];
                var start = Ms(sample.Pts);
                var end = sample.Duration > 0 ? Ms(sample.Pts + sample.Duration)
                    : i + 1 < pending.Count ? Ms(pending[i + 1].Sample.Pts)
                    : start + 2000;
                if (end <= start)
                    end = start + 1;
                var text = config.Codec == CodecType.Tx3g ? string.Empty : Encoding.UTF8.GetString(data);
                var e = config.Codec switch
                {
                    CodecType.Ass or CodecType.Ssa => AssFormat.ParseBlock(text, script, start, end),
                    CodecType.WebVtt => WebVttFormat.Parse(text, sample.CueSettings, vtt!, start, end),
                    CodecType.Tx3g => Tx3gFormat.ReadSample(data, tx3g!, script, start, end, timescale),
                    _ => SrtFormat.Parse(text, new SubtitleEvent { Start = start, End = end }),
                };
                if (!e.IsEmpty)
                    script.Events.Add(e);
            }
        }
        finally
        {
            source.Reset();
        }

        script.Events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return script;
    }

    // ------------------------------------------------------------------ writing

    private static (CodecConfig Config, List<MediaSample> Samples) Write(SubtitleScript script, CodecConfig input, CodecType target, int canvasWidth, int canvasHeight)
    {
        var samples = new List<MediaSample>();
        byte[]? extradata = null;
        var (width, height) = (script.Width, script.Height);
        switch (target)
        {
            case CodecType.Tx3g:
            {
                if (canvasWidth > 0 && canvasHeight > 0)
                    script.Rescale(canvasWidth, canvasHeight);
                (width, height) = (script.Width, script.Height);
                var merged = Contiguous(script);
                var description = Tx3gFormat.Describe(merged);
                extradata = Tx3gFormat.WriteDescription(description);
                foreach (var e in merged.Events)
                    samples.Add(Sample(e, Tx3gFormat.WriteSample(e, merged, description)));
                break;
            }

            case CodecType.Ass or CodecType.Ssa:
            {
                var ssa = target == CodecType.Ssa;
                // ASS keeps its own coordinate space; other sources are laid out on the video.
                if (input.Codec is not (CodecType.Ass or CodecType.Ssa) && canvasWidth > 0 && canvasHeight > 0)
                    script.Rescale(canvasWidth, canvasHeight);
                (width, height) = (script.Width, script.Height);
                extradata = Encoding.UTF8.GetBytes(AssFormat.WriteHeader(script, ssa));
                for (var i = 0; i < script.Events.Count; i++)
                    samples.Add(Sample(script.Events[i], Encoding.UTF8.GetBytes(AssFormat.WriteBlock(script.Events[i], i, script, ssa))));
                break;
            }

            case CodecType.WebVtt:
            {
                script.Rescale(WebVttFormat.CanvasWidth, WebVttFormat.CanvasHeight);
                var classes = WebVttFormat.Classes(script);
                extradata = Encoding.UTF8.GetBytes(WebVttFormat.WriteHeader(script, classes));
                foreach (var e in script.Events)
                {
                    var sample = Sample(e, Encoding.UTF8.GetBytes(WebVttFormat.WriteText(e, script, classes)));
                    sample.CueSettings = WebVttFormat.WriteSettings(e, script);
                    samples.Add(sample);
                }

                (width, height) = (canvasWidth, canvasHeight);
                break;
            }

            default:
                foreach (var e in script.Events)
                    samples.Add(Sample(e, Encoding.UTF8.GetBytes(SrtFormat.Write(e, script))));
                (width, height) = (canvasWidth, canvasHeight);
                break;
        }

        var config = new CodecConfig
        {
            Codec = target,
            Kind = TrackKind.Subtitle,
            SourceCodecId = SourceId(target),
            Extradata = extradata,
            Timescale = 1000,
            Language = input.Language,
            Name = input.Name,
            SubtitleWidth = width > 0 ? width : input.SubtitleWidth,
            SubtitleHeight = height > 0 ? height : input.SubtitleHeight,
        };
        return (config, samples);
    }

    private static MediaSample Sample(SubtitleEvent e, byte[] data) => new()
    {
        Dts = e.Start,
        Duration = Math.Max(1, e.End - e.Start),
        IsSync = true,
        Data = data,
    };

    /// <summary>
    /// The events as tx3g needs them: one at a time, back to back from zero (empty events in the gaps); events shown
    /// together are stacked into one, the first one's placement and karaoke kept.
    /// </summary>
    private static SubtitleScript Contiguous(SubtitleScript script)
    {
        var result = new SubtitleScript { Width = script.Width, Height = script.Height };
        result.Styles.AddRange(script.Styles);
        var defaults = script.Style(null);
        var bounds = new SortedSet<long> { 0 };
        foreach (var e in script.Events)
        {
            bounds.Add(e.Start);
            bounds.Add(e.End);
        }

        var points = bounds.ToList();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (from, to) = (points[i], points[i + 1]);
            var active = script.Events.Where(e => e.Start <= from && e.End >= to).OrderBy(e => e.Layer).ThenBy(e => e.Start).ToList();
            SubtitleEvent piece;
            if (active.Count == 0)
            {
                piece = new SubtitleEvent();
            }
            else if (active.Count == 1)
            {
                var e = active[0];
                var shift = from - e.Start;
                piece = e with { Karaoke = e.Karaoke.Select(k => k with { StartMs = k.StartMs - shift }).ToList() };
            }
            else
            {
                // Stacked: each event's formatting is spelled out against the Default style.
                var text = new StringBuilder();
                var runs = new List<SubtitleRun>();
                foreach (var e in active)
                {
                    if (text.Length > 0)
                        text.Append('\n');
                    var offset = text.Length;
                    text.Append(e.Text);
                    for (var k = 0; k < e.Text.Length; k++)
                    {
                        var style = script.StyleAt(e, k).Except(defaults.Style);
                        if (!style.IsEmpty)
                            runs.Add(new SubtitleRun(offset + k, offset + k + 1, style));
                    }
                }

                var first = active[0];
                var shift = from - first.Start;
                piece = first with
                {
                    StyleName = defaults.Name,
                    Text = text.ToString(),
                    Runs = Merge(runs),
                    Forced = active.Any(e => e.Forced),
                    Karaoke = first.Karaoke.Select(k => k with { StartMs = k.StartMs - shift }).ToList(),
                };
            }

            result.Events.Add(piece with { Start = from, End = to });
        }

        // Nothing after the last event (it ends the track).
        while (result.Events.Count > 0 && result.Events[^1].IsEmpty)
            result.Events.RemoveAt(result.Events.Count - 1);
        return result;
    }

    private static List<SubtitleRun> Merge(List<SubtitleRun> runs)
    {
        var merged = new List<SubtitleRun>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && merged[^1].End == r.Start && merged[^1].Style == r.Style)
                merged[^1] = merged[^1] with { End = r.End };
            else
                merged.Add(r);
        }

        return merged;
    }

    // ------------------------------------------------------------------ ISampleSource

    public uint TrackId => _source.TrackId;

    public CodecConfig Config { get; }

    public TimeSpan StartOffset => _source.StartOffset;

    public long MediaStart { get; }

    public TimeSpan Duration => _source.Duration;

    public long SampleCountHint => _samples.Count;

    public MediaSample? ReadNext()
    {
        if (_next >= _samples.Count)
            return null;
        var s = _samples[_next++];
        return new MediaSample { Dts = s.Dts, Duration = s.Duration, IsSync = true, Data = s.Data, CueSettings = s.CueSettings };
    }

    public void Reset() => _next = 0;
}
