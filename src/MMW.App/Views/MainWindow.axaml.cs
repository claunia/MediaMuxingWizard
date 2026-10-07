using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MMW.App.ViewModels;

namespace MMW.App.Views;

/// <summary>A document window (the home screen while it has no document).</summary>
public partial class MainWindow : Window
{
    private bool _closingConfirmed;
    private MainWindowViewModel? _subscribed;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        DragDrop.AddDragEnterHandler(this, (_, _) => WelcomeZone.Classes.Add("dragOver"));
        DragDrop.AddDragLeaveHandler(this, (_, _) => WelcomeZone.Classes.Remove("dragOver"));
        Activated += (_, _) => ViewModel?.App.WindowActivated(ViewModel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.ActivateRequested -= OnActivateRequested;
            _subscribed.CloseRequested -= OnCloseRequested;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.ActivateRequested += OnActivateRequested;
            _subscribed.CloseRequested += OnCloseRequested;
        }
    }

    private void OnActivateRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnCloseRequested(object? sender, bool confirmed)
    {
        _closingConfirmed |= confirmed;
        Close();
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        WelcomeZone.Classes.Remove("dragOver");
        if (ViewModel is null || e.DataTransfer.TryGetFiles() is not { } files)
            return;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await ViewModel.DropAsync(paths);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || ViewModel is not { Document.IsDirty: true } vm)
        {
            SaveWindowSize();
            return;
        }

        e.Cancel = true;
        if (await vm.ConfirmCloseAsync())
        {
            _closingConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (ViewModel is { } vm)
            vm.App.WindowClosed(vm);
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
}
