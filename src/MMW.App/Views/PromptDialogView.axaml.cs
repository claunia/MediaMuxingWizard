using Avalonia;
using Avalonia.Controls;

namespace MMW.App.Views;

public partial class PromptDialogView : UserControl
{
    public PromptDialogView() => InitializeComponent();

    private void OnTextAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }
}
