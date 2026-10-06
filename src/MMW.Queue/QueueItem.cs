using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MMW.Queue;

public enum QueueItemStatus
{
    Ready,
    Working,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One file in the queue.</summary>
public sealed partial class QueueItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string SourcePath { get; init; }

    /// <summary>Actions run for this item, in order (copied from the queue defaults when added).</summary>
    public List<QueueAction> Actions { get; init; } = [];

    [ObservableProperty]
    private QueueItemStatus _status;

    /// <summary>Where the result was (or will be) written.</summary>
    [ObservableProperty]
    private string? _destinationPath;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [property: JsonIgnore]
    private double _progress;

    public List<string> Log { get; init; } = [];

    public string Name => Path.GetFileName(SourcePath);

    /// <summary>Resets a finished item so it runs again.</summary>
    public void Reset()
    {
        Status = QueueItemStatus.Ready;
        Error = null;
        Progress = 0;
        Log.Clear();
    }
}
