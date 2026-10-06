using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;

namespace MMW.App.ViewModels;

public sealed record SaveSetResult(string Name, bool KeepArtworks, bool KeepAnnotations);

public sealed partial class SaveSetDialogViewModel : DialogViewModel<SaveSetResult>
{
    public override string Title => Strings.SaveSet_Title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = string.Empty;

    /// <summary>When applied, keep the target's existing artwork instead of replacing it.</summary>
    [ObservableProperty]
    private bool _keepArtworks = true;

    /// <summary>When applied, keep the target's existing tag values.</summary>
    [ObservableProperty]
    private bool _keepAnnotations;

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Close(new SaveSetResult(Name.Trim(), KeepArtworks, KeepAnnotations));
}
