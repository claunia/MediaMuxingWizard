using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.Core.Languages;

namespace MMW.App.ViewModels;

/// <summary>Picks a language (searchable list).</summary>
public sealed partial class LanguagePickerDialogViewModel(string title, string message, string? initial = null) : DialogViewModel<string>
{
    public override string Title { get; } = title;

    public string Message { get; } = message;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private Language? _selected = LanguageTable.Find(initial ?? "en");

    public IReadOnlyList<Language> Languages => Filter.Length == 0
        ? LanguageTable.All
        : LanguageTable.All.Where(l => l.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || l.Tag.StartsWith(Filter, StringComparison.OrdinalIgnoreCase)).ToList();

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(Languages));

    private bool CanAccept() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept() => Close(Selected!.Tag);
}
