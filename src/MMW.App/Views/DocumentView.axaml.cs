using Avalonia.Controls;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class DocumentView : UserControl
{
    public DocumentView() => InitializeComponent();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DocumentViewModel vm)
            vm.SelectedRows = TrackGrid.SelectedItems.OfType<TrackRowViewModel>().ToList();
    }
}
