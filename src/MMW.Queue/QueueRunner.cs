using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MMW.Core.Diagnostics;
using MMW.Core.Model;

namespace MMW.Queue;

/// <summary>Keeps the system awake while work is running.</summary>
public interface IPowerService
{
    IDisposable PreventSleep(string reason);
}

/// <summary>Processes queue items one after another.</summary>
public sealed partial class QueueRunner : ObservableObject
{
    private readonly ContainerRegistry _registry;
    private readonly IPowerService? _power;
    private CancellationTokenSource? _cts;

    public QueueRunner(ContainerRegistry registry, IPowerService? power = null)
    {
        _registry = registry;
        _power = power;
    }

    public ObservableCollection<QueueItem> Items { get; } = [];

    public QueueOptions Options { get; set; } = new();

    public IServiceProvider? Services { get; set; }

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Raised when a run finishes (with the number of completed and failed items).</summary>
    public event EventHandler<(int Completed, int Failed)>? RunFinished;

    /// <summary>Adds files with the queue's default actions.</summary>
    public IReadOnlyList<QueueItem> Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = new List<QueueItem>();
        foreach (var path in paths)
        {
            if (Items.Any(i => i.SourcePath == path && i.Status is QueueItemStatus.Ready or QueueItemStatus.Working))
                continue;
            var item = new QueueItem { SourcePath = path, Actions = [.. QueueStore.CloneActions(Options.DefaultActions)] };
            Items.Add(item);
            added.Add(item);
        }

        return added;
    }

    public void RemoveCompleted()
    {
        foreach (var item in Items.Where(i => i.Status == QueueItemStatus.Completed).ToList())
            Items.Remove(item);
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Runs every ready item; returns when the queue is empty or stopped.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;
        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = 0;
        var failed = 0;
        using var sleep = _power?.PreventSleep("Processing the queue");
        try
        {
            while (Items.FirstOrDefault(i => i.Status == QueueItemStatus.Ready) is { } item)
            {
                if (_cts.IsCancellationRequested)
                    break;
                if (await ProcessAsync(item, _cts.Token))
                    completed++;
                else
                    failed++;
            }
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            RunFinished?.Invoke(this, (completed, failed));
        }
    }

    /// <summary>Processes one item; returns true on success.</summary>
    public async Task<bool> ProcessAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Status = QueueItemStatus.Working;
        item.Progress = 0;
        try
        {
            var document = await _registry.OpenAsync(item.SourcePath, cancellationToken);
            var target = Options.FileType is { } type ? ContainerKinds.FromPath("x" + type) : document.Container;
            var context = new QueueContext(item, document)
            {
                Services = Services,
                TargetContainer = target == ContainerKind.Unknown ? document.Container : target,
            };
            context.Log($"Opened {item.Name}.");

            foreach (var action in item.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Log(action.Description);
                await action.ApplyAsync(context, cancellationToken);
            }

            var destination = Destination(item, context);
            item.DestinationPath = destination;
            var inPlace = string.Equals(Path.GetFullPath(destination), Path.GetFullPath(item.SourcePath), StringComparison.Ordinal);
            if (!inPlace && File.Exists(destination))
                throw new IOException($"'{destination}' already exists.");
            if (!inPlace)
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var handler = _registry.Get(document.Container) ?? throw new NotSupportedException($"No writer for {document.Container}.");
            var progress = new Progress<double>(p => item.Progress = p);
            await handler.SaveAsync(document, new SaveOptions
            {
                OutputPath = inPlace ? null : destination,
                Optimize = Options.Optimize,
            }, progress, cancellationToken);

            item.Progress = 1;
            item.Status = QueueItemStatus.Completed;
            context.Log($"Saved {Path.GetFileName(destination)}.");
            AppLog.Info($"Queue: {item.Name} → {destination}");
            return true;
        }
        catch (OperationCanceledException)
        {
            item.Status = QueueItemStatus.Cancelled;
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            item.Status = QueueItemStatus.Failed;
            item.Error = ex.Message;
            lock (item.Log)
                item.Log.Add($"Failed: {ex.Message}");
            AppLog.Error($"Queue: {item.Name} failed", ex);
            return false;
        }
    }

    /// <summary>Computes the output path from the options and the actions' output name.</summary>
    public string Destination(QueueItem item, QueueContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var directory = Options.Location == OutputLocation.Folder && !string.IsNullOrEmpty(Options.Folder)
            ? Options.Folder
            : Path.GetDirectoryName(item.SourcePath) ?? string.Empty;
        var name = context?.OutputBaseName ?? Path.GetFileNameWithoutExtension(item.SourcePath);
        var extension = Options.FileType ?? Path.GetExtension(item.SourcePath);
        return Path.Combine(directory, name + extension);
    }
}
