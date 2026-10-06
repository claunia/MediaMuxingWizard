using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class QueueWindow : Window
{
    public QueueWindow()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(this, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            if (DataContext is QueueViewModel vm && e.DataTransfer.TryGetFiles() is { } files)
                vm.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        });
    }
}
