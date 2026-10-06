using System.Globalization;
using MMW.App.Resources;
using MMW.Core.Media;

namespace MMW.App.ViewModels;

/// <summary>Advice shown whenever bitmap subtitles are converted with our built-in OCR.</summary>
public static class OcrAdvice
{
    public static string Warning => Strings.Ocr_Warning;

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
            await dialogs.ShowMessageAsync(Strings.Ocr_NotAvailable_Title, factory.UnavailableReason ?? Strings.Ocr_TesseractNotFound);
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
        if (!await dialogs.ConfirmAsync(Strings.Ocr_Download_Title, string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Download_MessageFormat, names), Strings.Button_Download))
            return false;
        try
        {
            foreach (var code in missing)
                await factory.Tessdata.DownloadAsync(code);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync(Strings.Ocr_DownloadFailed_Title, ex.Message);
            return false;
        }
    }
}
