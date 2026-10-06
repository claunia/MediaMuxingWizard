using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>Text format a bitmap subtitle track is converted to by OCR.</summary>
public enum SubtitleConversionTarget
{
    /// <summary>3GPP timed text (MP4 <c>tx3g</c>), with per-sample forced flags.</summary>
    Tx3g,

    /// <summary>SubRip text (Matroska <c>S_TEXT/UTF8</c>).</summary>
    Srt,
}

/// <summary>Settings of a bitmap subtitle OCR conversion (stored on the pending track's import options).</summary>
public sealed record OcrOptions
{
    /// <summary>Default options: the language is taken from the track.</summary>
    public static OcrOptions Default { get; } = new();

    /// <summary>
    /// Recognition language in the OCR engine's own notation (Tesseract: "eng", "fra", "chi_sim", or several joined
    /// with '+', e.g. "eng+fra"); null to derive it from the track language (English when the track has none).
    /// </summary>
    public string? Language { get; init; }
}

/// <summary>
/// Converts bitmap subtitles (PGS, VobSub, DVB) to text by OCR. The implementation lives in an optional component
/// (Tesseract + FFmpeg) registered with <see cref="MediaFormatRegistry.Register(ISubtitleConverterFactory)"/>, so the
/// remuxer does not depend on it.
/// </summary>
public interface ISubtitleConverterFactory
{
    /// <summary>Name and version of the implementation (e.g. "Tesseract 5.5.0").</summary>
    string Name { get; }

    /// <summary>True when conversions can be performed (the native libraries were loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false; null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>True when <paramref name="config"/> is a bitmap subtitle format that can be decoded.</summary>
    bool CanDecode(CodecConfig config);

    /// <summary>
    /// The recognition language used for a track whose language is <paramref name="trackLanguage"/> (BCP-47), in the
    /// engine's notation (see <see cref="OcrOptions.Language"/>).
    /// </summary>
    string ResolveLanguage(string? trackLanguage, OcrOptions options);

    /// <summary>Null when <paramref name="language"/> can be recognised; otherwise why not (e.g. its data is not installed).</summary>
    string? CheckLanguage(string language);

    /// <summary>
    /// Decodes the bitmaps of <paramref name="source"/> (without OCR; the source is rewound before and after) and
    /// reports whether all, some or none of them are forced.
    /// </summary>
    ForcedSubtitleMode DetectForcedMode(ISampleSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Wraps <paramref name="source"/> (positioned at its first sample) into a sample source producing the recognised
    /// text track. The returned source implements <see cref="IDisposable"/> and must be disposed; it never disposes
    /// <paramref name="source"/>. Reading it honours <paramref name="cancellationToken"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">OCR is unavailable, the codec cannot be decoded or the language is missing.</exception>
    ISampleSource Create(ISampleSource source, SubtitleConversionTarget target, OcrOptions options, CancellationToken cancellationToken);
}

/// <summary>Helpers shared by the import UI, the remuxer and the OCR component.</summary>
public static class SubtitleConversions
{
    /// <summary>Label of the OCR conversion to tx3g in the import UI.</summary>
    public const string Tx3gOcrName = "Tx3g (OCR)";

    /// <summary>Label of the OCR conversion to SubRip in the import UI.</summary>
    public const string SrtOcrName = "SRT (OCR)";

    /// <summary>True for bitmap subtitle codecs (PGS, VobSub, DVB).</summary>
    public static bool IsBitmap(CodecType codec) => codec is CodecType.Pgs or CodecType.VobSub or CodecType.DvbSub;

    /// <summary>True for a track <see cref="Track.Format"/> naming a bitmap subtitle codec ("PGS", "VobSub", "DVB").</summary>
    public static bool IsBitmapFormat(string? format) =>
        format is not null && (format.Equals(CodecNames.Display(CodecType.Pgs), StringComparison.OrdinalIgnoreCase) ||
                               format.Equals(CodecNames.Display(CodecType.VobSub), StringComparison.OrdinalIgnoreCase) ||
                               format.Equals(CodecNames.Display(CodecType.DvbSub), StringComparison.OrdinalIgnoreCase));

    /// <summary>The text format an import action produces from a bitmap track, or null when it is not an OCR action.</summary>
    public static SubtitleConversionTarget? Target(ImportAction action) => action switch
    {
        ImportAction.ConvertToTx3g => SubtitleConversionTarget.Tx3g,
        ImportAction.ConvertToSrt => SubtitleConversionTarget.Srt,
        _ => null,
    };

    /// <summary>The import action converting to <paramref name="target"/>.</summary>
    public static ImportAction Action(SubtitleConversionTarget target) =>
        target == SubtitleConversionTarget.Tx3g ? ImportAction.ConvertToTx3g : ImportAction.ConvertToSrt;

    /// <summary>The OCR conversion offered for a container: tx3g for MP4, SubRip for Matroska.</summary>
    public static SubtitleConversionTarget TargetFor(ContainerKind container) =>
        container == ContainerKind.Matroska ? SubtitleConversionTarget.Srt : SubtitleConversionTarget.Tx3g;

    /// <summary>Label of an OCR conversion ("Tx3g (OCR)", "SRT (OCR)").</summary>
    public static string DisplayName(SubtitleConversionTarget target) => target == SubtitleConversionTarget.Tx3g ? Tx3gOcrName : SrtOcrName;

    /// <summary>True when the import options ask for a tx3g/SRT conversion of a bitmap track (i.e. OCR).</summary>
    public static bool IsOcr(TrackImportOptions? import, CodecType codec) =>
        import is not null && Target(import.Action) is not null && IsBitmap(codec);

    /// <summary>
    /// True when a document track is to be converted by OCR on save: its import options carry
    /// <see cref="TrackImportOptions.Ocr"/> or it is a bitmap track with a tx3g/SRT action.
    /// </summary>
    public static bool IsOcr(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Source?.Import is { } import && Target(import.Action) is not null && (import.Ocr is not null || IsBitmapFormat(track.Format));
    }

    /// <summary>
    /// The configuration an OCR conversion of <paramref name="input"/> produces (millisecond timescale, no
    /// extradata), used to check container support before converting.
    /// </summary>
    public static CodecConfig PredictOutput(CodecConfig input, SubtitleConversionTarget target)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tx3g = target == SubtitleConversionTarget.Tx3g;
        return new CodecConfig
        {
            Codec = tx3g ? CodecType.Tx3g : CodecType.TextUtf8,
            Kind = TrackKind.Subtitle,
            SourceCodecId = tx3g ? "tx3g" : "S_TEXT/UTF8",
            Timescale = 1000,
            Language = input.Language,
            Name = input.Name,
            SubtitleWidth = input.SubtitleWidth,
            SubtitleHeight = input.SubtitleHeight,
        };
    }
}
