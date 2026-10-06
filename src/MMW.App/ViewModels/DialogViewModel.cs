using CommunityToolkit.Mvvm.Input;

namespace MMW.App.ViewModels;

/// <summary>Base for view models shown in modal dialogs.</summary>
public abstract partial class DialogViewModel<TResult> : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>Raised when the dialog should close, with its result.</summary>
    public event EventHandler<TResult?>? CloseRequested;

    protected void Close(TResult? result) => CloseRequested?.Invoke(this, result);

    [RelayCommand]
    private void Cancel() => Close(default);
}
