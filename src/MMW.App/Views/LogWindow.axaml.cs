using Avalonia.Controls;

namespace MMW.App.Views;

public partial class LogWindow : Window
{
    public LogWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
