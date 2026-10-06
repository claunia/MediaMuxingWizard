using MMW.Core.Media;

namespace MMW.App.ViewModels;

/// <summary>Advice shown whenever bitmap subtitles are converted with our built-in OCR.</summary>
public static class OcrAdvice
{
    public const string Warning =
        "Our built-in OCR is basic. Subtitle Edit (https://www.nikse.dk/subtitleedit) does a much better job of " +
        "converting image subtitles to text, and we recommend using it, then importing the resulting SRT file.";

    public static bool IsOcr(ImportChoice? choice) =>
        choice is not null && choice.DisplayName.Contains("OCR", StringComparison.OrdinalIgnoreCase);
}
