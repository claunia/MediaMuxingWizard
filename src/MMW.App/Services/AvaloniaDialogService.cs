using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MMW.App.ViewModels;
using MMW.App.Views;

namespace MMW.App.Services;

/// <summary><see cref="IDialogService"/> implemented with Avalonia windows and the platform storage provider.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private static Window Owner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive)
        ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("No window to own the dialog.");

    private static List<FilePickerFileType> ToFileTypes(IReadOnlyList<FileFilter> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => e == "*" ? "*" : "*." + e).ToList() }).ToList();

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool allowMultiple)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = ToFileTypes(filters),
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFileTypes(filters),
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(new MessageDialogViewModel(title, message, ["OK"]));

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") =>
        await ShowDialogAsync(new MessageDialogViewModel(title, message, [confirmText, "Cancel"])) == confirmText;

    public async Task<SaveChangesChoice> AskSaveChangesAsync(string documentName)
    {
        const string save = "Save", discard = "Don't Save", cancel = "Cancel";
        var result = await ShowDialogAsync(new MessageDialogViewModel("Unsaved changes",
            $"Do you want to save the changes made to \"{documentName}\"?\n\nYour changes will be lost if you don't save them.",
            [save, discard, cancel], save));
        return result switch
        {
            save => SaveChangesChoice.Save,
            discard => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public Task<string?> PromptAsync(string title, string message, string initialText = "") =>
        ShowDialogAsync(new PromptDialogViewModel(title, message, initialText));

    public async Task<TResult?> ShowDialogAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var window = new DialogWindow { DataContext = dialog, Title = dialog.Title };
        TResult? result = default;
        dialog.CloseRequested += (_, r) =>
        {
            result = r;
            window.Close();
        };
        await window.ShowDialog(Owner);
        return result;
    }
}
