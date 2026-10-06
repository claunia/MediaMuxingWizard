using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MMW.Core.Diagnostics;

namespace MMW.App.ViewModels;

public sealed partial class LogViewModel : ViewModelBase, IDisposable
{
    public LogViewModel()
    {
        foreach (var e in AppLog.Snapshot())
            Entries.Add(Format(e));
        AppLog.EntryAdded += OnEntry;
    }

    public ObservableCollection<string> Entries { get; } = [];

    private void OnEntry(object? sender, LogEntry e) => Dispatcher.UIThread.Post(() => Entries.Add(Format(e)));

    private static string Format(LogEntry e) => $"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Level,-7}  {e.Message}";

    [RelayCommand]
    private void Clear()
    {
        AppLog.Clear();
        Entries.Clear();
    }

    public void Dispose() => AppLog.EntryAdded -= OnEntry;
}
