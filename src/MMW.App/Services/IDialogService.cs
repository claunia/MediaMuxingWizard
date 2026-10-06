namespace MMW.App.Services;

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>A file type filter for open/save pickers.</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);

/// <summary>Dialogs and pickers, abstracted so view models stay testable.</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool allowMultiple);

    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters);

    Task<string?> PickFolderAsync(string title);

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    Task<SaveChangesChoice> AskSaveChangesAsync(string documentName);

    /// <summary>Asks for a single line of text; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string initialText = "");

    /// <summary>Shows a view model in a modal dialog window and returns its result.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(ViewModels.DialogViewModel<TResult> dialog);
}

public static class FileFilters
{
    public static readonly FileFilter Media = new("Media files", ["mp4", "m4v", "m4a", "m4b", "m4r", "mov", "mkv", "mka", "mks", "webm"]);
    public static readonly FileFilter Mp4 = new("MPEG-4", ["mp4", "m4v", "m4a", "m4b", "m4r", "mov"]);
    public static readonly FileFilter Matroska = new("Matroska", ["mkv", "mka", "mks", "webm"]);
    public static readonly FileFilter Images = new("Images", ["jpg", "jpeg", "png", "bmp", "gif"]);
    public static readonly FileFilter ChapterText = new("Chapter files", ["txt", "csv", "xml"]);
    public static readonly FileFilter Text = new("Text", ["txt"]);
    public static readonly FileFilter All = new("All files", ["*"]);
}
