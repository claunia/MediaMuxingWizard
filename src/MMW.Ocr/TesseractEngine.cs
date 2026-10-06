using MMW.Ocr.Interop;

namespace MMW.Ocr;

/// <summary>
/// <see cref="IOcrEngine"/> backed by libtesseract (loaded by <see cref="TesseractLoader"/>). One instance holds one
/// initialised <c>TessBaseAPI</c> for its language set; it is not thread-safe.
/// </summary>
public sealed class TesseractEngine : IOcrEngine
{
    /// <summary>Resolution reported to Tesseract (the preprocessed subtitle text is roughly 300 ppi print sized).</summary>
    private const int Resolution = 300;

    /// <summary>Upper bound of the time spent on one image (a safety net against pathological bitmaps).</summary>
    private const int DeadlineMs = 20_000;

    private readonly TesseractApi _api;

    /// <summary>Initialises Tesseract for <paramref name="languages"/> with the traineddata files in <paramref name="dataDirectory"/>.</summary>
    /// <param name="dataDirectory">Directory holding <c>&lt;language&gt;.traineddata</c> for every language.</param>
    /// <param name="languages">Tesseract language code(s), several joined with '+'.</param>
    /// <exception cref="NotSupportedException">libtesseract is not available.</exception>
    /// <exception cref="InvalidOperationException">The language data could not be loaded.</exception>
    public TesseractEngine(string dataDirectory, string languages)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(languages);
        Languages = languages;
        _api = TesseractApi.Create(dataDirectory, languages);
    }

    /// <summary>True when libtesseract is loaded.</summary>
    public static bool IsAvailable => TesseractLoader.IsAvailable;

    /// <summary>Tesseract version, or null when unavailable.</summary>
    public static string? Version => TesseractLoader.Version;

    /// <summary>Why Tesseract is unavailable, or null.</summary>
    public static string? Error => TesseractLoader.Error;

    public string Languages { get; }

    public OcrResult Recognize(OcrImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.IsBlank)
            return OcrResult.Empty;
        var mode = image.Layout == OcrLayout.SingleLine ? TessPageSegMode.SingleLine : TessPageSegMode.SingleBlock;
        var (text, confidence) = _api.Recognize(image.Pixels, image.Width, image.Height, mode, Resolution, DeadlineMs);
        return new OcrResult(text, confidence);
    }

    public void Dispose() => _api.Dispose();
}
