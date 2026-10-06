using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion;
using MMW.Ocr.Interop;

namespace MMW.Ocr;

/// <summary>
/// <see cref="ISubtitleConverterFactory"/> backed by FFmpeg (bitmap decoding) and Tesseract (recognition), with the
/// language models of a <see cref="TessdataManager"/>. Registered by <see cref="SubtitleOcr.Register"/>.
/// </summary>
public sealed class OcrSubtitleConverterFactory : ISubtitleConverterFactory
{
    private readonly Func<string, string, IOcrEngine> _engineFactory;

    /// <summary>Creates a factory using <paramref name="tessdata"/> for the language models and Tesseract engines.</summary>
    public OcrSubtitleConverterFactory(TessdataManager tessdata)
        : this(tessdata, static (directory, languages) => new TesseractEngine(directory, languages))
    {
    }

    /// <summary>Creates a factory with a custom engine factory (directory, languages) → engine.</summary>
    internal OcrSubtitleConverterFactory(TessdataManager tessdata, Func<string, string, IOcrEngine> engineFactory)
    {
        ArgumentNullException.ThrowIfNull(tessdata);
        ArgumentNullException.ThrowIfNull(engineFactory);
        Tessdata = tessdata;
        _engineFactory = engineFactory;
    }

    /// <summary>The language models used.</summary>
    public TessdataManager Tessdata { get; }

    public string Name => TesseractLoader.Version is { } v ? $"Tesseract {v}" : "Tesseract";

    public bool IsAvailable => TesseractLoader.IsAvailable && MediaConversion.IsAvailable;

    public string? UnavailableReason => !TesseractLoader.IsAvailable ? TesseractLoader.Error
        : !MediaConversion.IsAvailable ? $"FFmpeg is needed to decode bitmap subtitles: {MediaConversion.Error}"
        : null;

    public bool CanDecode(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == TrackKind.Subtitle && SubtitleConversions.IsBitmap(config.Codec) && IsAvailable && BitmapSubtitleDecoder.CanDecode(config);
    }

    /// <summary>
    /// <see cref="OcrOptions.Language"/> when set; otherwise the model for the track language
    /// (<see cref="TesseractLanguages.FromTrackLanguage"/>), or English when the track has no language with a model.
    /// </summary>
    public string ResolveLanguage(string? trackLanguage, OcrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.Language))
            return options.Language.Trim();
        return TesseractLanguages.FromTrackLanguage(trackLanguage) ?? TesseractLanguages.English;
    }

    public string? CheckLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var codes = TesseractLanguages.Split(language);
        if (codes.Count == 0 || codes.Any(c => !TessdataManager.IsValidCode(c)))
            return $"'{language}' is not a Tesseract language";
        var missing = codes.Where(c => Tessdata.FindFile(c) is null).ToList();
        return missing.Count == 0
            ? null
            : $"the {TesseractLanguages.DisplayName(string.Join('+', missing))} OCR language data ({string.Join(", ", missing.Select(m => m + TessdataManager.Extension))}) is not installed";
    }

    public ForcedSubtitleMode DetectForcedMode(ISampleSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        int total = 0, forced = 0;
        try
        {
            foreach (var bitmap in BitmapSubtitleDecoder.Decode(source, cancellationToken))
            {
                total++;
                if (bitmap.Forced)
                    forced++;
            }
        }
        finally
        {
            source.Reset();
        }

        return forced == 0 ? ForcedSubtitleMode.None : forced == total ? ForcedSubtitleMode.AllSamplesForced : ForcedSubtitleMode.SomeSamplesForced;
    }

    public ISampleSource Create(ISampleSource source, SubtitleConversionTarget target, OcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsAvailable)
            throw new NotSupportedException($"Subtitle OCR is not available: {UnavailableReason}");
        if (!CanDecode(source.Config))
            throw new NotSupportedException($"{source.Config.FormatName} subtitles cannot be decoded for OCR.");
        var language = ResolveLanguage(source.Config.Language, options);
        if (CheckLanguage(language) is { } missing)
            throw new NotSupportedException($"Subtitle OCR is not available: {missing}.");
        var directory = Tessdata.ResolveDataDirectory(language) ??
                        throw new NotSupportedException($"The {TesseractLanguages.DisplayName(language)} OCR language data is not installed.");
        return new OcrSubtitleSource(source, target, () => _engineFactory(directory, language), null, cancellationToken);
    }
}

/// <summary>Entry point of the OCR component.</summary>
public static class SubtitleOcr
{
    private static readonly Lock s_lock = new();
    private static OcrSubtitleConverterFactory? s_factory;

    /// <summary>
    /// Registers the OCR converter with <see cref="MediaFormatRegistry"/>, using <paramref name="tessdata"/> (or, the
    /// first time, a manager for <see cref="TessdataManager.DefaultDirectory"/>). Idempotent when called without a
    /// manager. Does not load Tesseract: it is looked for on first use, and a missing library only makes the converter
    /// report itself unavailable.
    /// </summary>
    public static OcrSubtitleConverterFactory Register(TessdataManager? tessdata = null)
    {
        lock (s_lock)
        {
            if (s_factory is null || tessdata is not null)
                s_factory = new OcrSubtitleConverterFactory(tessdata ?? new TessdataManager());
            MediaFormatRegistry.Register(s_factory);
            return s_factory;
        }
    }

    /// <summary>The registered factory (registering the default one when needed).</summary>
    public static OcrSubtitleConverterFactory Factory
    {
        get
        {
            lock (s_lock)
            {
                if (s_factory is not null)
                    return s_factory;
            }

            return Register();
        }
    }

    /// <summary>The language models of the registered factory.</summary>
    public static TessdataManager Tessdata => Factory.Tessdata;

    /// <summary>True when OCR can run: Tesseract and FFmpeg are loaded (a language model is needed too).</summary>
    public static bool IsAvailable => Factory.IsAvailable;

    /// <summary>Tesseract version, or null when unavailable.</summary>
    public static string? Version => TesseractLoader.Version;

    /// <summary>Why OCR is unavailable, or null.</summary>
    public static string? Error => Factory.UnavailableReason;
}
