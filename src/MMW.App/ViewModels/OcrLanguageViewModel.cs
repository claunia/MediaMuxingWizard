using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.Ocr;

namespace MMW.App.ViewModels;

/// <summary>One Tesseract language model in Preferences › OCR.</summary>
public sealed partial class OcrLanguageViewModel : ViewModelBase
{
    private readonly TessdataManager _tessdata;

    public OcrLanguageViewModel(TesseractLanguage language, TessdataManager tessdata)
    {
        Language = language;
        _tessdata = tessdata;
        Refresh();
    }

    public TesseractLanguage Language { get; }

    public string Name => $"{Language.Name} ({Language.Code})";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    private bool _isInstalled;

    /// <summary>True when the model lives in our own folder (system and bundled models cannot be deleted).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _error;

    private void Refresh()
    {
        IsInstalled = _tessdata.IsInstalled(Language.Code);
        IsDownloaded = _tessdata.IsDownloaded(Language.Code);
    }

    private bool CanDownload() => !IsInstalled && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        IsBusy = true;
        Error = null;
        try
        {
            await _tessdata.DownloadAsync(Language.Code, new Progress<double>(p => Progress = p));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanDelete() => IsDownloaded;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        _tessdata.Delete(Language.Code);
        Refresh();
    }
}
