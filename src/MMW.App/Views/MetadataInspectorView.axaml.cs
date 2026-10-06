using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    private async void OnArtworkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != Gestures.Command)
            return;
        if (e.Key == Key.V)
        {
            e.Handled = true;
            await PasteAsync();
        }
        else if (e.Key == Key.C)
        {
            e.Handled = true;
            await CopyAsync();
        }
    }

    private async void OnPasteArtwork(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await PasteAsync();

    private async void OnCopyArtwork(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await CopyAsync();

    /// <summary>Pastes an image (or image files) from the clipboard as new artwork.</summary>
    private async Task PasteAsync()
    {
        if (DataContext is not MetadataInspectorViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        var files = await clipboard.TryGetFilesAsync();
        var images = new List<byte[]>();
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            if (s_images.Contains(Path.GetExtension(path).ToLowerInvariant()))
                images.Add(await File.ReadAllBytesAsync(path));
        }

        if (images.Count == 0 && await clipboard.TryGetBitmapAsync() is { } bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms);
            images.Add(ms.ToArray());
        }

        vm.AddArtworkData(images);
    }

    private async Task CopyAsync()
    {
        if (DataContext is not MetadataInspectorViewModel { SelectedArtwork.Artwork: { } art } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        using var ms = new MemoryStream(art.Data);
        var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        await clipboard.SetBitmapAsync(bitmap);
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
