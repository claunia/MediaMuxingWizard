using Avalonia.Controls;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class ChaptersInspectorView : UserControl
{
    public ChaptersInspectorView() => InitializeComponent();

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Column.DisplayIndex == 0 && DataContext is ChaptersInspectorViewModel vm)
            vm.SortByTime();
    }
}
