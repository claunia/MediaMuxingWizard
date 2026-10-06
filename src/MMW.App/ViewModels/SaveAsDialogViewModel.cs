using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Metadata;
using MMW.Core.Model;

namespace MMW.App.ViewModels;

/// <summary>"Save As" with Subler's save options (format, 64-bit offsets/times, optimize).</summary>
public sealed partial class SaveAsDialogViewModel : DialogViewModel<SaveOptions>
{
    private readonly IDialogService _dialogs;

    public SaveAsDialogViewModel(MediaDocument document, IDialogService dialogs, AppSettings settings)
    {
        _dialogs = dialogs;
        IsMp4 = document.Container == ContainerKind.Mp4;
        IReadOnlyList<Choice<string>> mp4 =
            [new(".m4v", "MPEG-4 Video (.m4v)"), new(".mp4", "MPEG-4 (.mp4)"), new(".m4a", "MPEG-4 Audio (.m4a)"), new(".m4b", "Audiobook (.m4b)"), new(".m4r", "Ringtone (.m4r)")];
        IReadOnlyList<Choice<string>> mkv = [new(".mkv", "Matroska (.mkv)"), new(".mka", "Matroska Audio (.mka)"), new(".webm", "WebM (.webm)")];

        // Saving to the other container family remuxes the file (no re-encoding).
        Formats = IsMp4 ? [.. mp4, .. mkv] : [.. mkv, .. mp4];

        var source = document.Path ?? "Untitled.m4v";
        var ext = System.IO.Path.GetExtension(source).ToLowerInvariant();
        _selectedFormat = Formats.FirstOrDefault(f => f.Value == ext) ?? Formats[0];
        var baseName = settings.UseFileNameFormat
            ? FileNameFormatter.FormatFor(document.Metadata, settings.MovieFileNameFormat, settings.TvFileNameFormat)
            : null;
        baseName ??= System.IO.Path.GetFileNameWithoutExtension(source) + " copy";
        _path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source) ?? string.Empty, baseName + _selectedFormat.Value);
        _optimize = settings.OptimizeOnSave;
        _use64BitOffsets = settings.Use64BitOffsets || document.FileSize > 3_900_000_000L;
        _use64BitTimes = settings.Use64BitTimes;
    }

    public override string Title => "Save As";

    public bool IsMp4 { get; }

    /// <summary>True when the chosen format is MPEG-4 (MP4 save options apply).</summary>
    public bool TargetIsMp4 => SelectedFormat.Value is ".m4v" or ".mp4" or ".m4a" or ".m4b" or ".m4r";

    public IReadOnlyList<Choice<string>> Formats { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _path;

    [ObservableProperty]
    private Choice<string> _selectedFormat;

    [ObservableProperty]
    private bool _optimize;

    [ObservableProperty]
    private bool _use64BitOffsets;

    [ObservableProperty]
    private bool _use64BitTimes;

    partial void OnSelectedFormatChanged(Choice<string> value)
    {
        OnPropertyChanged(nameof(TargetIsMp4));
        if (!string.IsNullOrEmpty(Path))
            Path = System.IO.Path.ChangeExtension(Path, value.Value);
    }

    [RelayCommand]
    private async Task Browse()
    {
        var filter = new FileFilter(SelectedFormat.Name, [SelectedFormat.Value.TrimStart('.')]);
        var chosen = await _dialogs.SaveFileAsync("Save As", System.IO.Path.GetFileName(Path), [filter]);
        if (chosen is not null)
            Path = chosen;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Path) && Directory.Exists(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Close(new SaveOptions
    {
        OutputPath = System.IO.Path.GetFullPath(Path),
        Optimize = Optimize,
        Use64BitOffsets = Use64BitOffsets,
        Use64BitTimes = Use64BitTimes,
    });
}
