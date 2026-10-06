using MMW.Core.Model;

namespace MMW.Queue;

/// <summary>State shared by the actions of one queue item.</summary>
public sealed class QueueContext(QueueItem item, MediaDocument document)
{
    public QueueItem Item { get; } = item;

    public MediaDocument Document { get; } = document;

    /// <summary>Container family the item is written as (from the queue's file type, or the source's).</summary>
    public ContainerKind TargetContainer { get; init; }

    /// <summary>Output file name without extension, when an action decided one.</summary>
    public string? OutputBaseName { get; set; }

    /// <summary>Services for external actions (metadata providers, importers), keyed by type.</summary>
    public IServiceProvider? Services { get; init; }

    public void Log(string message)
    {
        lock (Item.Log)
            Item.Log.Add($"{DateTime.Now:HH:mm:ss} {message}");
    }
}
