using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class MetadataInspectorView : UserControl
{
    private static readonly string[] s_images = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    public MetadataInspectorView()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(ArtworkList, OnDragOver);
        DragDrop.AddDropHandler(ArtworkList, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MetadataInspectorViewModel vm || e.DataTransfer.TryGetFiles() is not { } files)
            return;
        var images = new List<byte[]>();
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (s_images.Contains(Path.GetExtension(path).ToLowerInvariant()))
                images.Add(await File.ReadAllBytesAsync(path));
        }

        vm.AddArtworkData(images);
    }
}
