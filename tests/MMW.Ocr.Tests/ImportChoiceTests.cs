using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Ocr.Tests;

/// <summary>Import actions offered for bitmap subtitles, and the bookkeeping of OCR conversions in Core.</summary>
public sealed class ImportChoiceTests
{
    private static CodecConfig Bitmap(CodecType codec) => new() { Codec = codec, Kind = TrackKind.Subtitle, Timescale = 1000, Language = "en" };

    private static readonly TrackSupport NeedsOcr = new(TrackSupportLevel.NeedsConversion, ImportAction.Skip, "bitmap subtitles cannot be stored in MP4");

    [Fact]
    public void Pgs_into_mp4_suggests_tx3g_ocr()
    {
        var config = Bitmap(CodecType.Pgs);
        var choices = ConversionDefaults.Choices(config, NeedsOcr, ContainerKind.Mp4, canConvert: false, canOcr: true);
        Assert.Equal(["Tx3g (OCR)", "Skip"], choices.Select(c => c.DisplayName));
        Assert.True(choices[0].Ocr);
        Assert.Equal(ImportAction.ConvertToTx3g, choices[0].Action);
        Assert.Same(choices[0], ConversionDefaults.Suggest(config, NeedsOcr, ContainerKind.Mp4, choices));
        Assert.Equal(OcrOptions.Default, choices[0].OcrFrom(null));
        Assert.Null(choices[1].OcrFrom(null));

        var unavailable = ConversionDefaults.Choices(config, NeedsOcr, ContainerKind.Mp4, canConvert: false);
        Assert.Equal(["Not available"], unavailable.Select(c => c.DisplayName));
    }

    [Fact]
    public void Vobsub_keeps_passthrough_by_default_and_offers_ocr()
    {
        var config = Bitmap(CodecType.VobSub);
        var mp4 = ConversionDefaults.Choices(config, TrackSupport.Passthrough, ContainerKind.Mp4, false, canOcr: true);
        Assert.Equal(["Passthru", "Tx3g (OCR)", "Skip"], mp4.Select(c => c.DisplayName));
        Assert.Equal("Passthru", ConversionDefaults.Suggest(config, TrackSupport.Passthrough, ContainerKind.Mp4, mp4).DisplayName);

        var mkv = ConversionDefaults.Choices(config, TrackSupport.Passthrough, ContainerKind.Matroska, false, canOcr: true);
        Assert.Equal(["Passthru", "SRT (OCR)", "Skip"], mkv.Select(c => c.DisplayName));
        Assert.Equal(ImportAction.ConvertToSrt, mkv[1].Action);
        Assert.Equal("Passthru", ConversionDefaults.Suggest(config, TrackSupport.Passthrough, ContainerKind.Matroska, mkv).DisplayName);
    }

    [Theory]
    [InlineData(CodecType.Pgs, ContainerKind.Mp4)]
    [InlineData(CodecType.VobSub, ContainerKind.Mp4)]
    [InlineData(CodecType.VobSub, ContainerKind.Matroska)]
    [InlineData(CodecType.DvbSub, ContainerKind.Matroska)]
    public void Only_ocr_choices_are_labelled_ocr(CodecType codec, ContainerKind target)
    {
        // The application recognises OCR choices by "(OCR)" in the label (and the Ocr flag).
        var support = codec == CodecType.Pgs ? NeedsOcr : TrackSupport.Passthrough;
        var choices = ConversionDefaults.Choices(Bitmap(codec), support, target, canConvert: true, canOcr: true);
        Assert.Single(choices, c => c.Ocr);
        Assert.All(choices, c => Assert.Equal(c.Ocr, c.DisplayName.Contains("(OCR)", StringComparison.Ordinal)));

        var audio = new CodecConfig { Codec = CodecType.Dts, Kind = TrackKind.Audio, Channels = 6, SampleRate = 48000 };
        Assert.DoesNotContain(ConversionDefaults.Choices(audio, TrackSupport.Passthrough, target, canConvert: true, canOcr: true),
            c => c.Ocr || c.DisplayName.Contains("(OCR)", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_subtitles_are_not_offered_ocr()
    {
        var srt = new CodecConfig { Codec = CodecType.TextUtf8, Kind = TrackKind.Subtitle };
        var converted = new TrackSupport(TrackSupportLevel.Converted, ImportAction.ConvertToTx3g);
        var choices = ConversionDefaults.Choices(srt, converted, ContainerKind.Mp4, false, canOcr: true);
        Assert.Equal(["Tx3g", "Skip"], choices.Select(c => c.DisplayName));
        Assert.False(choices[0].Ocr);
        Assert.False(SubtitleConversions.IsOcr(new TrackImportOptions { Action = ImportAction.ConvertToTx3g }, CodecType.TextUtf8));
    }

    [Fact]
    public void Ocr_on_an_existing_track_is_a_conversion_that_needs_a_remux()
    {
        var document = new MediaDocument("/tmp/movie.mkv", ContainerKind.Matroska);
        var track = new SubtitleTrack { Id = 3, Format = "VobSub", Source = new TrackSource("/tmp/movie.mkv", ContainerKind.Matroska, 3) };
        document.Tracks.Add(track);
        Assert.False(TrackConversions.HasConversions(document));

        TrackConversions.SetAction(document, track, ImportAction.ConvertToSrt);
        Assert.True(SubtitleConversions.IsOcr(track));
        Assert.Equal(OcrOptions.Default, track.Source!.Import!.Ocr);
        Assert.True(TrackConversions.HasConversions(document));
        Assert.True(RemuxPolicy.HasImportedTracks(document));

        var french = new OcrOptions { Language = "fra" };
        TrackConversions.SetAction(document, track, ImportAction.ConvertToSrt, ocr: french);
        Assert.Equal(french, track.Source.Import.Ocr);
        Assert.Equal(french, ImportPlan.FromDocument(document).Imports[0].Ocr);

        TrackConversions.SetAction(document, track, ImportAction.Passthrough);
        Assert.Null(track.Source.Import.Ocr);
        Assert.False(TrackConversions.HasConversions(document));
    }

    [Fact]
    public void Ocr_output_is_predicted_per_target()
    {
        var input = Bitmap(CodecType.VobSub) with { SubtitleWidth = 720, SubtitleHeight = 576, Name = "Commentary" };
        var tx3g = SubtitleConversions.PredictOutput(input, SubtitleConversionTarget.Tx3g);
        Assert.Equal((CodecType.Tx3g, "tx3g", 1000u, 720, 576, "en", "Commentary"),
            (tx3g.Codec, tx3g.SourceCodecId, tx3g.Timescale, tx3g.SubtitleWidth, tx3g.SubtitleHeight, tx3g.Language, tx3g.Name));
        var srt = SubtitleConversions.PredictOutput(input, SubtitleConversionTarget.Srt);
        Assert.Equal((CodecType.TextUtf8, "S_TEXT/UTF8"), (srt.Codec, srt.SourceCodecId));
        Assert.Equal(SubtitleConversionTarget.Srt, SubtitleConversions.TargetFor(ContainerKind.Matroska));
        Assert.Equal(SubtitleConversionTarget.Tx3g, SubtitleConversions.TargetFor(ContainerKind.Mp4));
    }

    [Fact]
    public void Forced_flag_round_trips_through_tx3g_samples()
    {
        var forced = new MMW.Core.Media.Codecs.StyledText("Forced", []) { Forced = true };
        var bytes = MMW.Core.Media.Codecs.SubtitleText.ToTx3g(forced, 18);
        Assert.Equal("frcd", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        Assert.True(MMW.Core.Media.Codecs.SubtitleText.FromTx3g(bytes).Forced);
        Assert.False(MMW.Core.Media.Codecs.SubtitleText.FromTx3g(MMW.Core.Media.Codecs.SubtitleText.ToTx3g(forced with { Forced = false }, 18)).Forced);

        // Merged cues are forced when any part is.
        var joined = MMW.Core.Media.Codecs.StyledText.Join([forced, new MMW.Core.Media.Codecs.StyledText("Normal", [])]);
        Assert.True(joined.Forced);
        Assert.Equal("Forced\nNormal", joined.Text);
    }
}
