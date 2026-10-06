using Avalonia.Controls;
using Avalonia.Input;

namespace MMW.App.Views;

/// <summary>Hosts a dialog view model; its view is resolved by the view locator.</summary>
public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }
}
