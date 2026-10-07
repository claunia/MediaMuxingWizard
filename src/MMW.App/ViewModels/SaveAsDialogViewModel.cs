using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Media;
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
            [new(".m4v", Strings.Format_M4v), new(".mp4", Strings.Format_Mp4), new(".m4a", Strings.Format_M4a), new(".m4b", Strings.Format_M4b), new(".m4r", Strings.Format_M4r)];
        IReadOnlyList<Choice<string>> mkv = [new(".mkv", Strings.Format_Mkv), new(".mka", Strings.Format_Mka), new(".webm", Strings.Format_Webm)];

        // Saving to the other container family remuxes the file (no re-encoding).
        Formats = IsMp4 ? [.. mp4, .. mkv] : [.. mkv, .. mp4];

        // An untitled document is saved next to its first imported track (or in the Videos folder); a document whose
        // output format was switched starts with that format's first extension.
        var folder = document.Path is { } existing
            ? System.IO.Path.GetDirectoryName(existing)
            : document.Tracks.Select(t => t.Source?.Path).OfType<string>().Select(System.IO.Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d))
              ?? DefaultFolder();
        var ext = System.IO.Path.GetExtension(document.Path ?? string.Empty).ToLowerInvariant();
        _selectedFormat = Formats.FirstOrDefault(f => f.Value == ext && ContainerKinds.FromPath("x" + ext) == document.Container) ?? Formats[0];
        var baseName = settings.UseFileNameFormat
            ? FileNameFormatter.FormatFor(document.Metadata, settings.MovieFileNameFormat, settings.TvFileNameFormat)
            : null;
        baseName ??= document.Path is null
            ? Strings.SaveAs_Untitled
            : RemuxPolicy.ChangesContainer(document, document.Container)
                ? System.IO.Path.GetFileNameWithoutExtension(document.Path)
                : string.Format(CultureInfo.CurrentCulture, Strings.SaveAs_CopyNameFormat, System.IO.Path.GetFileNameWithoutExtension(document.Path));
        _path = System.IO.Path.Combine(folder ?? string.Empty, baseName + _selectedFormat.Value);
        _optimize = settings.OptimizeOnSave;
        _use64BitOffsets = settings.Use64BitOffsets || document.FileSize > 3_900_000_000L;
        _use64BitTimes = settings.Use64BitTimes;
    }

    private static string DefaultFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Directory.Exists(videos) ? videos : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public override string Title => Strings.SaveAs_Title;

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
        var chosen = await _dialogs.SaveFileAsync(Strings.SaveAs_Title, System.IO.Path.GetFileName(Path), [filter]);
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
