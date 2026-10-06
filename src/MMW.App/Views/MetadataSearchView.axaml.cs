using Avalonia.Controls;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class MetadataSearchView : UserControl
{
    public MetadataSearchView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MetadataSearchViewModel vm)
            SeriesBox.AsyncPopulator = vm.SuggestSeriesAsync;
    }
}
