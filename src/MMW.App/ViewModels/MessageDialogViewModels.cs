using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MMW.App.ViewModels;

/// <summary>A message with a set of buttons; the result is the chosen button's text.</summary>
public sealed partial class MessageDialogViewModel(string title, string message, IReadOnlyList<string> buttons, string? defaultButton = null)
    : DialogViewModel<string>
{
    public override string Title { get; } = title;

    public string Message { get; } = message;

    public IReadOnlyList<string> Buttons { get; } = buttons;

    public string? DefaultButton { get; } = defaultButton ?? buttons.FirstOrDefault();

    [RelayCommand]
    private void Choose(string button) => Close(button);
}

/// <summary>Asks for one line of text.</summary>
public sealed partial class PromptDialogViewModel(string title, string message, string initialText) : DialogViewModel<string>
{
    public override string Title { get; } = title;

    public string Message { get; } = message;

    [ObservableProperty]
    private string _text = initialText;

    [RelayCommand]
    private void Accept() => Close(Text);
}
