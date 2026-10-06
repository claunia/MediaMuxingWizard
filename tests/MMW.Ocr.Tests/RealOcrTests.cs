using System.Text;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.Ocr.Interop;
using MMW.TestSupport;
using static MMW.Ocr.Tests.OcrFixtures;

namespace MMW.Ocr.Tests;

/// <summary>
/// Recognition with the real libtesseract and English model (skipped when unavailable): rendered text, synthetic
/// VobSub tracks through the FFmpeg decoder, whole remuxes, and the corpus (opt-in, <c>MMW_CORPUS</c>).
/// </summary>
public sealed class RealOcrTests
{
    private static string Normalise(string text) => string.Join(' ', text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Loader_reports_its_state_without_throwing()
    {
        // Whatever the machine has, the loader answers and never throws.
        if (TesseractLoader.IsAvailable)
        {
            Assert.Null(TesseractLoader.Error);
            Assert.True(TesseractLoader.IsSupportedVersion(TesseractLoader.Version));
            Assert.NotNull(TesseractLoader.LibraryPath);
            Assert.StartsWith("Tesseract ", SubtitleOcr.Factory.Name, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotNull(TesseractLoader.Error);
            Assert.Null(TesseractLoader.Version);
        }

        Assert.NotEmpty(TesseractLoader.SearchDirectories());
        Assert.NotEmpty(TesseractLoader.LibraryFileNames());
        Assert.True(TesseractLoader.IsSupportedVersion("5.5.0"));
        Assert.True(TesseractLoader.IsSupportedVersion("4.1.1"));
        Assert.False(TesseractLoader.IsSupportedVersion("3.05.02"));
        Assert.False(TesseractLoader.IsSupportedVersion(null));
    }

    [Fact]
    public void Missing_language_data_fails_cleanly()
    {
        if (!TesseractLoader.IsAvailable)
            Assert.Skip($"libtesseract is not available: {TesseractLoader.Error}");
        var empty = Path.Combine(Path.GetTempPath(), "mmw-ocr-tests", "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new TesseractEngine(empty, "eng"));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    [Theory]
    [InlineData("Hello, world!", 28)]
    [InlineData("The quick brown fox jumps", 20)]
    [InlineData("I don't know what you mean.", 16)]
    public void Rendered_subtitle_text_is_recognised(string text, int pointSize)
    {
        var data = RequireTesseract();
        var image = RenderText(text, pointSize);
        using var engine = new TesseractEngine(data, "eng");
        Assert.Equal("eng", engine.Languages);

        var prepared = OcrPreprocessor.Prepare(image.Pixels, image.Width, image.Height);
        Assert.Equal(1, prepared.Lines);
        var result = engine.Recognize(prepared, Ct);
        Assert.Equal(text, OcrTextCleaner.Clean(result.Text));
        Assert.InRange(result.Confidence, 60, 100);
    }

    [Fact]
    public void Two_line_subtitles_keep_their_line_break()
    {
        var data = RequireTesseract();
        var image = RenderText("Where are you going?\nHome, finally.", 24);
        using var engine = new TesseractEngine(data, "eng");
        var prepared = OcrPreprocessor.Prepare(image.Pixels, image.Width, image.Height);
        Assert.Equal(2, prepared.Lines);
        Assert.Equal(OcrLayout.Block, prepared.Layout);
        Assert.Equal("Where are you going?\nHome, finally.", OcrTextCleaner.Clean(engine.Recognize(prepared, Ct).Text));
    }

    [Fact]
    public void Dark_text_with_a_light_outline_is_recognised_through_the_retry()
    {
        var data = RequireTesseract();
        var image = RenderText("Black letters", 26, fill: "black", stroke: "white");
        var bitmap = image.ToBitmap(1, 2);
        using var source = new OcrSubtitleSource(1, VobSubConfig(), TimeSpan.FromSeconds(3), () => [bitmap], SubtitleConversionTarget.Srt,
            () => new TesseractEngine(data, "eng"), null, Ct);
        var sample = source.ReadNext();
        Assert.NotNull(sample);
        Assert.Equal("Black letters", Encoding.UTF8.GetString(sample.GetData().Span));
    }

    [Fact]
    public void Synthetic_vobsub_track_is_recognised_with_times_and_forced_flags()
    {
        var data = RequireTesseract();
        RequireFfmpeg();
        var hello = RenderText("Hello there");
        var forced = RenderText("Forced message");
        var track = new MemorySampleSource(VobSubConfig(),
        [
            Sample(1000, Spu(hello, 60, 400, 2000, forced: false)),
            Sample(5000, Spu(forced, 60, 400, 1500, forced: true)),
        ]) { Duration = TimeSpan.FromSeconds(8) };

        var factory = new OcrSubtitleConverterFactory(new TessdataManager(data, includeSystemDirectories: false));
        Assert.True(factory.CanDecode(track.Config));
        Assert.Equal(ForcedSubtitleMode.SomeSamplesForced, factory.DetectForcedMode(track, Ct));

        using var source = (OcrSubtitleSource)factory.Create(track, SubtitleConversionTarget.Tx3g, OcrOptions.Default, Ct);
        var samples = new List<MediaSample>();
        while (source.ReadNext() is { } s)
            samples.Add(s);

        var cues = samples.Select(s => (s.Dts, s.Duration, Text: SubtitleText.FromTx3g(s.GetData().Span))).ToList();
        // SPU stop times are in 1024/90 ms units: allow their rounding.
        Assert.Equal([0L, 1000, 3000, 5000], cues.Select(c => (long)(Math.Round(c.Dts / 10.0) * 10)));
        Assert.Equal([1000L, 2000, 2000, 1500], cues.Select(c => (long)(Math.Round((c.Dts + c.Duration) / 10.0) * 10) - (long)(Math.Round(c.Dts / 10.0) * 10)));
        Assert.Equal(["", "Hello there", "", "Forced message"], cues.Select(c => c.Text.Text));
        Assert.Equal([false, false, false, true], cues.Select(c => c.Text.Forced));
    }

    [Fact]
    public void Factory_resolves_and_checks_languages()
    {
        var root = Path.Combine(Path.GetTempPath(), "mmw-ocr-tests", "lang-" + Guid.NewGuid().ToString("N"));
        var factory = new OcrSubtitleConverterFactory(new TessdataManager(root, includeSystemDirectories: false));
        Assert.Equal("fra", factory.ResolveLanguage("fr", OcrOptions.Default));
        Assert.Equal("chi_tra", factory.ResolveLanguage("zh-Hant", OcrOptions.Default));
        Assert.Equal("eng", factory.ResolveLanguage("und", OcrOptions.Default));
        Assert.Equal("eng", factory.ResolveLanguage(null, OcrOptions.Default));
        Assert.Equal("deu+eng", factory.ResolveLanguage("fr", new OcrOptions { Language = "deu+eng" }));

        Assert.Contains("fra.traineddata", factory.CheckLanguage("fra"), StringComparison.Ordinal);
        Assert.Contains("French", factory.CheckLanguage("fra"), StringComparison.Ordinal);
        Assert.Contains("not a Tesseract language", factory.CheckLanguage("../x"), StringComparison.Ordinal);

        Directory.CreateDirectory(root);
        var model = new byte[TessdataManager.MinimumSize];
        model[0] = 24;
        File.WriteAllBytes(Path.Combine(root, "fra.traineddata"), model);
        Assert.Null(factory.CheckLanguage("fra"));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Vobsub_imported_into_mp4_is_saved_as_tx3g_text()
    {
        RequireTesseract();
        RequireFfmpeg();
        if (SubtitleOcr.Factory.CheckLanguage("eng") is { } missing)
            Assert.Skip(missing);
        var mks = WriteVobSubMkv([("Good morning", 500, 2000, false), ("Keep out", 3000, 1500, true)]);
        var output = Path.Combine(Path.GetTempPath(), "mmw-tests", "ocr-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var item = Assert.Single(await TrackImporter.InspectAsync(mks, ContainerKind.Mp4, Ct));
            Assert.True(item.CanOcr);
            Assert.Equal(["Passthru", "Tx3g (OCR)", "Skip"], item.Choices.Select(c => c.DisplayName));
            item.Choice = item.Choices.Single(c => c.Ocr);
            Assert.Equal(ImportAction.ConvertToTx3g, item.Action);
            Assert.Equal(OcrOptions.Default, item.Ocr);

            var document = new MediaDocument(null, ContainerKind.Mp4);
            var track = (SubtitleTrack)Assert.Single(TrackImporter.AddToDocument(document, [item]));
            Assert.Equal("Tx3g", track.Format);
            Assert.True(SubtitleConversions.IsOcr(track));

            var support = Assert.Single(await Remuxer.CheckAsync(document, ContainerKind.Mp4, Ct)).Support;
            Assert.Equal(TrackSupportLevel.Converted, support.Level);

            await Remuxer.SaveAsync(document, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, cancellationToken: Ct);
            Assert.Equal(ForcedSubtitleMode.SomeSamplesForced, track.ForcedMode);

            using var demuxer = MediaFormatRegistry.OpenDemuxer(output);
            var text = Assert.Single(demuxer.Tracks);
            Assert.Equal(CodecType.Tx3g, text.Config.Codec);
            var cues = new List<(long Start, StyledText Text)>();
            while (text.ReadNext() is { } s)
                cues.Add((s.Pts, SubtitleText.FromTx3g(s.GetData().Span)));
            var shown = cues.Where(c => !c.Text.IsEmpty).ToList();
            Assert.Equal(["Good morning", "Keep out"], shown.Select(c => c.Text.Text));
            Assert.Equal([500L * text.Config.Timescale / 1000, 3000L * text.Config.Timescale / 1000], shown.Select(c => c.Start));
            Assert.Equal([false, true], shown.Select(c => c.Text.Forced));
        }
        finally
        {
            File.Delete(mks);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Vobsub_converted_by_ocr_in_matroska_becomes_srt()
    {
        RequireTesseract();
        RequireFfmpeg();
        if (SubtitleOcr.Factory.CheckLanguage("eng") is { } missing)
            Assert.Skip(missing);
        var mks = WriteVobSubMkv([("All forced", 500, 2000, true), ("Every one", 3000, 1500, true)]);
        var output = Path.Combine(Path.GetTempPath(), "mmw-tests", "ocr-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var item = Assert.Single(await TrackImporter.InspectAsync(mks, ContainerKind.Matroska, Ct));
            Assert.Equal(["Passthru", "SRT (OCR)", "Skip"], item.Choices.Select(c => c.DisplayName));
            item.Choice = item.Choices.Single(c => c.Ocr);

            var document = new MediaDocument(null, ContainerKind.Matroska);
            var track = (SubtitleTrack)Assert.Single(TrackImporter.AddToDocument(document, [item]));
            await Remuxer.SaveAsync(document, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, cancellationToken: Ct);
            Assert.Equal(ForcedSubtitleMode.AllSamplesForced, track.ForcedMode);

            var streams = MediaProbe.Streams(output);
            Assert.Equal("subrip", Assert.Single(streams).Codec);
            using var demuxer = MediaFormatRegistry.OpenDemuxer(output);
            var text = Assert.Single(demuxer.Tracks);
            var cues = new List<string>();
            while (text.ReadNext() is { } s)
                cues.Add(Encoding.UTF8.GetString(s.GetData().Span));
            Assert.Equal(["All forced", "Every one"], cues);
        }
        finally
        {
            File.Delete(mks);
            File.Delete(output);
        }
    }

    public static IEnumerable<TheoryDataRow<string>> CorpusVobSubFiles() =>
        Corpus.Files(".mkv", ".mks")
            .Where(f => f.Length == 0 || Path.GetFileName(f).Contains("DVD", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("VobSub", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Contains("PGS", StringComparison.OrdinalIgnoreCase))
            .Select(f => new TheoryDataRow<string>(f));

    [Theory]
    [MemberData(nameof(CorpusVobSubFiles))]
    public void Corpus_bitmap_subtitles_are_recognised(string file)
    {
        Corpus.Require(file);
        var data = RequireTesseract();
        RequireFfmpeg();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(file);
        var tracks = demuxer.Tracks.Where(t => SubtitleConversions.IsBitmap(t.Config.Codec)).ToList();
        if (tracks.Count == 0)
            Assert.Skip("No bitmap subtitle track.");

        foreach (var track in tracks)
        {
            track.Reset();
            var language = TesseractLanguages.FromTrackLanguage(track.Config.Language) ?? "eng";
            var tessdata = new TessdataManager(data);
            if (!tessdata.IsInstalled(language))
                language = "eng";
            using var source = new OcrSubtitleSource(track, SubtitleConversionTarget.Srt, () => new TesseractEngine(tessdata.ResolveDataDirectory(language)!, language),
                null, Ct);
            var texts = new List<string>();
            while (source.ReadNext() is { } s && texts.Count < 40)
                texts.Add(Encoding.UTF8.GetString(s.GetData().Span));

            TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(file)} track {track.TrackId} ({language}): {source.EventCount} events, " +
                                                      $"{source.EmptyCount} empty, confidence {source.MeanConfidence:0}; first: {string.Join(" | ", texts.Take(5).Select(Normalise))}");
            Assert.NotEmpty(texts);
            Assert.True(source.EmptyCount * 4 <= source.EventCount, $"{source.EmptyCount} of {source.EventCount} events had no text.");
            Assert.True(texts.Count(t => t.Any(char.IsLetter)) * 10 >= texts.Count * 9, "Most cues should contain letters.");
        }
    }
}
