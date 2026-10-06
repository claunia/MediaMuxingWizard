using MMW.Core.Media;

namespace MMW.App.ViewModels;

/// <summary>Advice shown whenever bitmap subtitles are converted with our built-in OCR.</summary>
public static class OcrAdvice
{
    public const string Warning =
        "Our built-in OCR is basic. Subtitle Edit (https://www.nikse.dk/subtitleedit) does a much better job of " +
        "converting image subtitles to text, and we recommend using it, then importing the resulting SRT file.";

    public static bool IsOcr(ImportChoice? choice) => choice?.Ocr == true;

    /// <summary>OCR options from the preferences (null language = derive it from the track).</summary>
    public static OcrOptions Options(Services.AppSettings settings) =>
        string.IsNullOrEmpty(settings.OcrLanguage) ? OcrOptions.Default : new OcrOptions { Language = settings.OcrLanguage };

    /// <summary>
    /// Makes sure the language models for OCR of the given track languages are installed, offering to download
    /// missing ones. Returns false when OCR is unavailable or the user declined.
    /// </summary>
    public static async Task<bool> EnsureModelsAsync(Services.IDialogService dialogs, Services.AppSettings settings, IEnumerable<string> trackLanguages)
    {
        var factory = MMW.Ocr.SubtitleOcr.Factory;
        if (!factory.IsAvailable)
        {
            await dialogs.ShowMessageAsync("OCR not available", factory.UnavailableReason ?? "Tesseract was not found.");
            return false;
        }

        var options = Options(settings);
        var missing = trackLanguages
            .Select(l => factory.ResolveLanguage(l, options))
            .SelectMany(l => l.Split('+'))
            .Distinct(StringComparer.Ordinal)
            .Where(code => !factory.Tessdata.IsInstalled(code))
            .ToList();
        if (missing.Count == 0)
            return true;

        var names = string.Join(", ", missing.Select(MMW.Ocr.TesseractLanguages.DisplayName));
        if (!await dialogs.ConfirmAsync("Download OCR language", $"OCR needs the {names} language model, which is not installed. Download it now (a few MB)?", "Download"))
            return false;
        try
        {
            foreach (var code in missing)
                await factory.Tessdata.DownloadAsync(code);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync("Download failed", ex.Message);
            return false;
        }
    }
}
