using System.Windows.Input;

namespace MMW.App.ViewModels;

/// <summary>A data-bound menu entry (Header "-" renders a separator).</summary>
public sealed class MenuNode
{
    public MenuNode(string header, ICommand? command = null, object? parameter = null, IReadOnlyList<MenuNode>? children = null, string? gesture = null)
    {
        Header = header;
        Command = command;
        CommandParameter = parameter;
        Children = children ?? [];
        Gesture = gesture;
    }

    public string Header { get; }

    public ICommand? Command { get; }

    public object? CommandParameter { get; }

    public IReadOnlyList<MenuNode> Children { get; }

    public string? Gesture { get; }

    public static MenuNode Separator { get; } = new("-");
}
