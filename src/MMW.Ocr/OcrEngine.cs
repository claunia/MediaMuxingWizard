namespace MMW.Ocr;

/// <summary>How the text of an <see cref="OcrImage"/> is laid out, used to pick the engine's segmentation mode.</summary>
public enum OcrLayout
{
    /// <summary>One line of text.</summary>
    SingleLine,

    /// <summary>A block of one or more lines.</summary>
    Block,
}

/// <summary>An 8-bit grayscale image prepared for OCR (dark text on a light background, rows without padding).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">Luminance, one byte per pixel, <paramref name="Width"/> bytes per row.</param>
/// <param name="Lines">Number of text lines found in the image (0 when it has no ink).</param>
/// <param name="Layout">Segmentation hint derived from <paramref name="Lines"/>.</param>
public sealed record OcrImage(int Width, int Height, byte[] Pixels, int Lines, OcrLayout Layout)
{
    /// <summary>True when the image contains nothing to recognise.</summary>
    public bool IsBlank => Lines == 0 || Width == 0 || Height == 0;

    /// <summary>Luminance of the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte At(int x, int y) => Pixels[(y * Width) + x];

    /// <summary>Encodes the image as a binary PGM (P5) file, for diagnostics.</summary>
    public byte[] ToPgm()
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{Width} {Height}\n255\n");
        return [.. header, .. Pixels];
    }
}

/// <summary>Recognised text of one image.</summary>
/// <param name="Text">Raw engine output (lines separated by '\n'); empty when nothing was recognised.</param>
/// <param name="Confidence">Mean confidence, 0–100.</param>
public sealed record OcrResult(string Text, int Confidence)
{
    public static OcrResult Empty { get; } = new(string.Empty, 0);
}

/// <summary>
/// An OCR engine initialised for one language set. Engines are not thread-safe: an instance is used by one thread at
/// a time and reused across images (initialisation is the expensive part).
/// </summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>The languages the engine recognises (Tesseract notation, e.g. "eng" or "eng+fra").</summary>
    string Languages { get; }

    /// <summary>Recognises the text of <paramref name="image"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    OcrResult Recognize(OcrImage image, CancellationToken cancellationToken = default);
}
