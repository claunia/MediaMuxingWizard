using System.Text;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Media.Conversion;

namespace MMW.Ocr.Tests;

/// <summary>Timing, grouping, forced flags and retries of the OCR sample source (fake engine, synthetic bitmaps).</summary>
public sealed class OcrSubtitleSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A white bar whose width identifies it (the fake engine reads the width back).</summary>
    private static RgbaImage Bar(int width) => RgbaImage.Blank(width + 20, 30).Fill(10, 10, width, 10, 255, 255, 255);

    /// <summary>Answers "Text {bar width}" from the width of a prepared ×3 bar image (pad 10).</summary>
    private static FakeOcrEngine WidthEngine(int confidence = 90) =>
        new((image, _) => new OcrResult($"Text {(image.Width - 20) / 3}\n", confidence));

    private static OcrSubtitleSource Source(IEnumerable<SubtitleBitmap> bitmaps, SubtitleConversionTarget target, IOcrEngine engine,
        IProgress<double>? progress = null, double duration = 10, CancellationToken? cancellationToken = null) =>
        new(7, OcrFixtures.VobSubConfig("fr"), TimeSpan.FromSeconds(duration), () => bitmaps, target, () => engine, progress, cancellationToken ?? Ct);

    private static List<MediaSample> ReadAll(OcrSubtitleSource source)
    {
        var list = new List<MediaSample>();
        while (source.ReadNext() is { } s)
            list.Add(s);
        return list;
    }

    private static StyledText Tx3g(MediaSample s) => SubtitleText.FromTx3g(s.GetData().Span);

    [Fact]
    public void Tx3g_output_is_contiguous_with_empty_gaps_and_forced_samples()
    {
        var engine = WidthEngine();
        using var source = Source(
        [
            Bar(30).ToBitmap(1, 3),
            Bar(40).ToBitmap(4, 6.5, forced: true),
        ], SubtitleConversionTarget.Tx3g, engine);

        Assert.Equal(CodecType.Tx3g, source.Config.Codec);
        Assert.Equal("tx3g", source.Config.SourceCodecId);
        Assert.Equal(1000u, source.Config.Timescale);
        Assert.Equal("fr", source.Config.Language);
        Assert.Equal(7u, source.TrackId);
        Assert.Equal(TimeSpan.Zero, source.StartOffset);

        var samples = ReadAll(source);
        Assert.Equal([0L, 1000, 3000, 4000], samples.Select(s => s.Dts));
        Assert.Equal([1000L, 2000, 1000, 2500], samples.Select(s => s.Duration));
        Assert.All(samples, s => Assert.True(s.IsSync));
        var texts = samples.Select(Tx3g).ToList();
        Assert.Equal(["", "Text 30", "", "Text 40"], texts.Select(t => t.Text));
        Assert.Equal([false, false, false, true], texts.Select(t => t.Forced));
        Assert.Equal(2, source.EventCount);
        Assert.Equal(1, source.ForcedCount);
        Assert.Equal(0, source.EmptyCount);
        Assert.Equal(90, source.MeanConfidence);
        Assert.Null(source.ReadNext());
    }

    [Fact]
    public void Srt_output_has_one_sample_per_event_and_no_gaps()
    {
        using var source = Source([Bar(30).ToBitmap(1, 3), Bar(40).ToBitmap(4, 6.5, forced: true)], SubtitleConversionTarget.Srt, WidthEngine());
        Assert.Equal(CodecType.TextUtf8, source.Config.Codec);
        Assert.Equal("S_TEXT/UTF8", source.Config.SourceCodecId);

        var samples = ReadAll(source);
        Assert.Equal([1000L, 4000], samples.Select(s => s.Dts));
        Assert.Equal([2000L, 2500], samples.Select(s => s.Duration));
        Assert.Equal(["Text 30", "Text 40"], samples.Select(s => Encoding.UTF8.GetString(s.GetData().Span)));
    }

    [Fact]
    public void Rectangles_of_one_event_are_joined_top_to_bottom()
    {
        // Two rectangles shown together (PGS can have two objects): the lower one is decoded first.
        using var source = Source(
        [
            Bar(40).ToBitmap(2, 4, y: 400),
            Bar(30).ToBitmap(2, 4, y: 50),
            Bar(50).ToBitmap(5, 6),
        ], SubtitleConversionTarget.Srt, WidthEngine());

        var samples = ReadAll(source);
        Assert.Equal(2, samples.Count);
        Assert.Equal("Text 30\nText 40", Encoding.UTF8.GetString(samples[0].GetData().Span));
        Assert.Equal(2000, samples[0].Duration);
        Assert.Equal(2, source.EventCount);
    }

    [Fact]
    public void Overlapping_events_are_merged_for_tx3g()
    {
        using var source = Source([Bar(30).ToBitmap(1, 4), Bar(40).ToBitmap(2, 3)], SubtitleConversionTarget.Tx3g, WidthEngine());
        var samples = ReadAll(source);
        Assert.Equal([0L, 1000, 2000, 3000], samples.Select(s => s.Dts));
        Assert.Equal(["", "Text 30", "Text 30\nText 40", "Text 30"], samples.Select(s => Tx3g(s).Text));
    }

    [Fact]
    public void Unreadable_events_are_dropped_and_poor_readings_retried()
    {
        // A white bar has no dark ink, so each event is tried in luminance then alpha mode. The first event reads
        // nothing (the "~" speck is cleaned away); the second reads weakly first, then better.
        var engine = new FakeOcrEngine((image, call) => call switch
        {
            0 => new OcrResult(string.Empty, 10),
            1 => new OcrResult("~", 30),
            2 => new OcrResult("Weak", 20),
            3 => new OcrResult("Better", 70),
            _ => new OcrResult("unused", 99),
        });
        using var source = Source([Bar(30).ToBitmap(1, 2), Bar(40).ToBitmap(3, 4)], SubtitleConversionTarget.Srt, engine);

        var samples = ReadAll(source);
        var sample = Assert.Single(samples);
        Assert.Equal(3000, sample.Dts);
        Assert.Equal("Better", Encoding.UTF8.GetString(sample.GetData().Span));
        Assert.Equal(4, engine.Images.Count);
        Assert.Equal(1, source.EmptyCount);
    }

    [Fact]
    public void Confident_readings_are_not_retried()
    {
        var engine = WidthEngine(confidence: OcrSubtitleSource.GoodConfidence);
        using var source = Source([Bar(30).ToBitmap(1, 2)], SubtitleConversionTarget.Srt, engine);
        ReadAll(source);
        Assert.Single(engine.Images);
        Assert.Equal(OcrLayout.SingleLine, engine.Images[0].Layout);
    }

    [Fact]
    public void Blank_bitmaps_do_not_reach_the_engine()
    {
        var engine = WidthEngine();
        using var source = Source([RgbaImage.Blank(30, 10).ToBitmap(1, 2)], SubtitleConversionTarget.Tx3g, engine);
        var samples = ReadAll(source);
        Assert.Empty(engine.Images);
        Assert.Empty(samples); // a timeline without any text has no samples
        Assert.Equal(1, source.EmptyCount);
    }

    [Fact]
    public void Reset_replays_the_track_and_dispose_releases_the_engine()
    {
        var engine = WidthEngine();
        var source = Source([Bar(30).ToBitmap(1, 2), Bar(40).ToBitmap(3, 4)], SubtitleConversionTarget.Srt, engine);
        var first = ReadAll(source).Select(s => (s.Dts, Encoding.UTF8.GetString(s.GetData().Span))).ToList();
        source.Reset();
        var second = ReadAll(source).Select(s => (s.Dts, Encoding.UTF8.GetString(s.GetData().Span))).ToList();
        Assert.Equal(first, second);
        Assert.Equal(2, source.EventCount);

        Assert.False(engine.Disposed);
        source.Dispose();
        Assert.True(engine.Disposed);
        Assert.Throws<ObjectDisposedException>(() => source.ReadNext());
    }

    [Fact]
    public void The_engine_is_created_lazily()
    {
        var created = 0;
        using var source = new OcrSubtitleSource(1, OcrFixtures.VobSubConfig(), TimeSpan.FromSeconds(5), () => [Bar(30).ToBitmap(1, 2)],
            SubtitleConversionTarget.Srt, () =>
            {
                created++;
                return WidthEngine();
            }, null, Ct);
        Assert.Equal(0, created);
        ReadAll(source);
        Assert.Equal(1, created);
    }

    [Fact]
    public void Progress_follows_the_track_and_cancellation_stops_reading()
    {
        var progress = new List<double>();
        var reporter = new SyncProgress(progress.Add);
        using (var source = Source([Bar(30).ToBitmap(1, 2), Bar(40).ToBitmap(4, 5)], SubtitleConversionTarget.Srt, WidthEngine(), reporter, duration: 10))
            ReadAll(source);
        Assert.Equal([0.2, 0.5, 1.0], progress);

        using var cts = new CancellationTokenSource();
        var engine = new FakeOcrEngine((_, _) =>
        {
            cts.Cancel();
            return new OcrResult("Text", 90);
        });
        using var cancelled = Source([Bar(30).ToBitmap(1, 2), Bar(40).ToBitmap(4, 5)], SubtitleConversionTarget.Srt, engine, cancellationToken: cts.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => ReadAll(cancelled));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public void Text_is_cleaned()
    {
        var engine = new FakeOcrEngine((_, _) => new OcrResult("  | know   what\n\n.\nl'm doing\n", 85));
        using var source = Source([Bar(30).ToBitmap(1, 2)], SubtitleConversionTarget.Srt, engine);
        Assert.Equal("I know what\nI'm doing", Encoding.UTF8.GetString(Assert.Single(ReadAll(source)).GetData().Span));
    }
}
