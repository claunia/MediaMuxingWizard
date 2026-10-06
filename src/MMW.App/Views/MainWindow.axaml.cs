using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MMW.App.ViewModels;

namespace MMW.App.Views;

public partial class MainWindow : Window
{
    private bool _closingConfirmed;
    private LogWindow? _log;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        DragDrop.AddDragEnterHandler(this, (_, _) => WelcomeZone.Classes.Add("dragOver"));
        DragDrop.AddDragLeaveHandler(this, (_, _) => WelcomeZone.Classes.Remove("dragOver"));
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        WelcomeZone.Classes.Remove("dragOver");
        if (ViewModel is null || e.DataTransfer.TryGetFiles() is not { } files)
            return;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await ViewModel.OpenPathsAsync(paths);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || ViewModel is null || ViewModel.Documents.All(d => !d.IsDirty))
        {
            SaveWindowSize();
            return;
        }

        e.Cancel = true;
        if (await ViewModel.ConfirmExitAsync())
        {
            _closingConfirmed = true;
            SaveWindowSize();
            Close();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel?.Settings is { RememberWindowSize: true } s && s.WindowWidth > 300 && s.WindowHeight > 200)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
    }

    private void SaveWindowSize()
    {
        if (ViewModel?.Settings is { RememberWindowSize: true } s && WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            ViewModel.PersistSettings();
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnShowLogClick(object? sender, RoutedEventArgs e)
    {
        if (_log is null)
        {
            _log = new LogWindow { DataContext = new LogViewModel() };
            _log.Closed += (_, _) => _log = null;
            _log.Show(this);
        }
        else
        {
            _log.Activate();
        }
    }
}
