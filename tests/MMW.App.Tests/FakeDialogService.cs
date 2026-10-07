using MMW.App.Services;
using MMW.App.ViewModels;

namespace MMW.App.Tests;

/// <summary>Dialog service that records requests and returns scripted answers.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public List<string> Messages { get; } = [];

    public SaveChangesChoice SaveChangesAnswer { get; set; } = SaveChangesChoice.Discard;

    public Queue<object?> DialogResults { get; } = new();

    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool allowMultiple) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

    public string? FolderAnswer { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderAnswer);

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null) => Task.FromResult(true);

    public Task<SaveChangesChoice> AskSaveChangesAsync(string documentName) => Task.FromResult(SaveChangesAnswer);

    public Task<string?> PromptAsync(string title, string message, string initialText = "") => Task.FromResult<string?>(initialText);

    /// <summary>Runs a dialog the way a user would (e.g. ticking tracks and importing); true when it handled the dialog.</summary>
    public Func<object, Task<bool>>? OnShowDialog { get; set; }

    public async Task<TResult?> ShowDialogAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        if (OnShowDialog is not null && await OnShowDialog(dialog))
            return default;
        return DialogResults.Count > 0 ? (TResult?)DialogResults.Dequeue() : default;
    }
}
