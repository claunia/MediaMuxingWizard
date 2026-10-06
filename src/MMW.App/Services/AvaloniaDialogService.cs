using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MMW.App.Resources;
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

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(new MessageDialogViewModel(title, message, [Strings.Button_OK]));

    public async Task<bool> ConfirmAsync(string title, string message, string? confirmText = null)
    {
        confirmText ??= Strings.Button_OK;
        return await ShowDialogAsync(new MessageDialogViewModel(title, message, [confirmText, Strings.Button_Cancel])) == confirmText;
    }

    public async Task<SaveChangesChoice> AskSaveChangesAsync(string documentName)
    {
        string save = Strings.Button_Save, discard = Strings.Button_DontSave, cancel = Strings.Button_Cancel;
        var result = await ShowDialogAsync(new MessageDialogViewModel(Strings.Dialog_SaveChanges_Title,
            string.Format(CultureInfo.CurrentCulture, Strings.Dialog_SaveChanges_MessageFormat, documentName),
            [save, discard, cancel], save));
        return result == save ? SaveChangesChoice.Save
            : result == discard ? SaveChangesChoice.Discard
            : SaveChangesChoice.Cancel;
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
