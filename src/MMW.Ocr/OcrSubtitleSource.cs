using System.Text;
using MMW.Core.Diagnostics;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Media.Conversion;

namespace MMW.Ocr;

/// <summary>
/// A text subtitle track recognised by OCR from a bitmap subtitle track (PGS, VobSub, DVB): bitmaps are decoded
/// (<see cref="BitmapSubtitleDecoder"/>), preprocessed (<see cref="OcrPreprocessor"/>), recognised by an
/// <see cref="IOcrEngine"/> and cleaned (<see cref="OcrTextCleaner"/>), one event at a time as the samples are read.
/// </summary>
/// <remarks>
/// <para>Output: tx3g samples (<see cref="SubtitleConversionTarget.Tx3g"/>) laid out by <see cref="SubtitleTimeline"/>
/// — contiguous, non-overlapping, with empty samples for the gaps and a 'frcd' box on forced subtitles — or SubRip
/// text samples (<see cref="SubtitleConversionTarget.Srt"/>, Matroska <c>S_TEXT/UTF8</c>), one per event, gaps left
/// empty. Times are in milliseconds on the presentation timeline (the source's start offset is included).</para>
/// <para>The rectangles of one event (same start time) become one cue, top to bottom. Events whose text cannot be
/// recognised are dropped. Each rectangle is read first with the ink polarity guessed by
/// <see cref="OcrPreprocessor.DetectInkMode"/>; a poor reading is retried with the other <see cref="InkMode"/>s and the
/// most confident reading is kept.</para>
/// <para>The engine is created on first use and owned by this source; the source is not thread-safe.</para>
/// </remarks>
public sealed class OcrSubtitleSource : ISampleSource, IDisposable
{
    /// <summary>A reading at least this confident is accepted without trying the other ink modes.</summary>
    internal const int GoodConfidence = 50;


    private readonly Func<IEnumerable<SubtitleBitmap>> _bitmaps;
    private readonly Func<IOcrEngine> _engineFactory;
    private readonly SubtitleConversionTarget _target;
    private readonly IProgress<double>? _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly Queue<SubtitleCue> _queue = new();
    private readonly int _fontSize;
    private IOcrEngine? _engine;
    private IEnumerator<SubtitleBitmap>? _enumerator;
    private SubtitleBitmap? _lookahead;
    private SubtitleTimeline _timeline = new();
    private bool _finished;
    private bool _disposed;
    private double _confidenceSum;

    /// <summary>Recognises <paramref name="source"/> (positioned at its first sample) with engines from <paramref name="engineFactory"/>.</summary>
    /// <param name="source">A PGS, VobSub or DVB subtitle track; it is read by this source but not disposed.</param>
    /// <param name="target">Text format to produce.</param>
    /// <param name="engineFactory">Creates the OCR engine (called once, on first use, on the reading thread).</param>
    /// <param name="progress">Receives the fraction of the track recognised (0–1).</param>
    /// <param name="cancellationToken">Cancels reading (checked for every bitmap).</param>
    public OcrSubtitleSource(ISampleSource source, SubtitleConversionTarget target, Func<IOcrEngine> engineFactory, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        : this(Checked(source).TrackId, source.Config, source.StartOffset + source.Duration, () => BitmapSubtitleDecoder.Decode(source, cancellationToken), target,
            engineFactory, progress, cancellationToken)
    {
        SampleCountHint = source.SampleCountHint < 0 ? -1 : target == SubtitleConversionTarget.Tx3g ? source.SampleCountHint * 2 : source.SampleCountHint;
    }

    /// <summary>Recognises the bitmaps produced by <paramref name="bitmaps"/> (called again by <see cref="Reset"/>).</summary>
    internal OcrSubtitleSource(uint trackId, CodecConfig input, TimeSpan duration, Func<IEnumerable<SubtitleBitmap>> bitmaps, SubtitleConversionTarget target,
        Func<IOcrEngine> engineFactory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(engineFactory);
        TrackId = trackId;
        Config = SubtitleConversions.PredictOutput(input, target);
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        _bitmaps = bitmaps;
        _engineFactory = engineFactory;
        _target = target;
        _progress = progress;
        _cancellationToken = cancellationToken;
        _fontSize = SubtitleText.DefaultFontSize(input.SubtitleHeight);
    }

    public uint TrackId { get; }

    public CodecConfig Config { get; }

    /// <summary>Always zero: sample times already include the source's start offset.</summary>
    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart => 0;

    public TimeSpan Duration { get; }

    public long SampleCountHint { get; } = -1;

    /// <summary>Subtitle events read so far (including the ones without recognisable text).</summary>
    public int EventCount { get; private set; }

    /// <summary>Events whose text could not be recognised (dropped).</summary>
    public int EmptyCount { get; private set; }

    /// <summary>Events recognised as forced subtitles.</summary>
    public int ForcedCount { get; private set; }

    /// <summary>Mean OCR confidence (0–100) of the recognised rectangles so far.</summary>
    public double MeanConfidence => RecognizedRectangles == 0 ? 0 : _confidenceSum / RecognizedRectangles;

    private int RecognizedRectangles { get; set; }

    public MediaSample? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_queue.Count == 0)
        {
            if (_finished)
                return null;
            var cue = NextCue();
            if (cue is null)
            {
                _finished = true;
                if (_target == SubtitleConversionTarget.Tx3g)
                    Enqueue(_timeline.Complete());
                Log();
                _progress?.Report(1.0);
                continue;
            }

            if (cue.Text.IsEmpty)
                continue;
            if (_target == SubtitleConversionTarget.Tx3g)
                Enqueue(_timeline.Add(cue));
            else
                _queue.Enqueue(cue);
        }

        var next = _queue.Dequeue();
        return new MediaSample
        {
            Dts = next.Start,
            Duration = Math.Max(1, next.End - next.Start),
            IsSync = true,
            Data = _target == SubtitleConversionTarget.Tx3g ? SubtitleText.ToTx3g(next.Text, _fontSize) : Encoding.UTF8.GetBytes(next.Text.Text),
        };
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enumerator?.Dispose();
        _enumerator = null;
        _lookahead = null;
        _queue.Clear();
        _timeline = new SubtitleTimeline();
        _finished = false;
        EventCount = EmptyCount = ForcedCount = RecognizedRectangles = 0;
        _confidenceSum = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _enumerator?.Dispose();
        _engine?.Dispose();
    }

    private static ISampleSource Checked(ISampleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source;
    }

    private void Enqueue(List<SubtitleCue> cues)
    {
        foreach (var c in cues)
            _queue.Enqueue(c);
    }

    private SubtitleBitmap? NextBitmap()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _enumerator ??= _bitmaps().GetEnumerator();
        return _enumerator.MoveNext() ? _enumerator.Current : null;
    }

    /// <summary>The next event (all rectangles sharing a start time) as a cue, or null at the end of the track.</summary>
    private SubtitleCue? NextCue()
    {
        var first = _lookahead ?? NextBitmap();
        _lookahead = null;
        if (first is null)
            return null;

        var group = new List<SubtitleBitmap> { first };
        while (NextBitmap() is { } b)
        {
            if (b.Start == first.Start)
            {
                group.Add(b);
                continue;
            }

            _lookahead = b;
            break;
        }

        EventCount++;
        var texts = new List<string>();
        foreach (var rect in group.OrderBy(r => r.Y).ThenBy(r => r.X))
        {
            var text = Recognize(rect);
            if (text.Length > 0)
                texts.Add(text);
        }

        var start = (long)Math.Round(first.Start.TotalMilliseconds);
        var end = (long)Math.Round(group.Max(r => r.End).TotalMilliseconds);
        if (end <= start)
            end = start + 1;
        var forced = group.Any(r => r.Forced);
        if (texts.Count == 0)
            EmptyCount++;
        else if (forced)
            ForcedCount++;

        if (Duration > TimeSpan.Zero)
            _progress?.Report(Math.Clamp(end / Duration.TotalMilliseconds, 0, 0.999));
        return new SubtitleCue(start, end, new StyledText(string.Join('\n', texts), []) { Forced = forced });
    }

    /// <summary>The cleaned text of one rectangle ("" when nothing was recognised).</summary>
    private string Recognize(SubtitleBitmap bitmap)
    {
        string best = string.Empty;
        var bestConfidence = -1;
        var first = OcrPreprocessor.DetectInkMode(bitmap.Rgba, bitmap.Width, bitmap.Height);
        InkMode[] modes = first == InkMode.DarkLuminance
            ? [InkMode.DarkLuminance, InkMode.Luminance, InkMode.Alpha]
            : [InkMode.Luminance, InkMode.DarkLuminance, InkMode.Alpha];
        foreach (var mode in modes)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var image = OcrPreprocessor.Prepare(bitmap.Rgba, bitmap.Width, bitmap.Height, mode);
            if (image.IsBlank)
                continue;

            _engine ??= _engineFactory();
            var result = _engine.Recognize(image, _cancellationToken);
            var text = OcrTextCleaner.Clean(result.Text);
            if (text.Length > 0 && result.Confidence > bestConfidence)
            {
                best = text;
                bestConfidence = result.Confidence;
            }

            if (bestConfidence >= GoodConfidence)
                break;
        }

        if (bestConfidence >= 0)
        {
            RecognizedRectangles++;
            _confidenceSum += bestConfidence;
        }

        return best;
    }

    private void Log() =>
        AppLog.Info(FormattableString.Invariant(
            $"OCR of track {TrackId}: {EventCount} subtitle(s), {EventCount - EmptyCount} recognised (mean confidence {MeanConfidence:0}), {EmptyCount} without text, {ForcedCount} forced."));
}
