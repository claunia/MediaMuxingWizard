namespace MMW.Queue;

public enum OutputLocation
{
    /// <summary>Write next to the source (in place when the name and type do not change).</summary>
    SameAsSource,

    /// <summary>Write into <see cref="QueueOptions.Folder"/>.</summary>
    Folder,
}

/// <summary>Global queue settings.</summary>
public sealed class QueueOptions
{
    public OutputLocation Location { get; set; } = OutputLocation.SameAsSource;

    public string? Folder { get; set; }

    /// <summary>Output extension (".m4v", ".mp4", ".mkv"); null keeps the source type.</summary>
    public string? FileType { get; set; }

    public bool AutoStart { get; set; }

    public bool NotifyWhenDone { get; set; } = true;

    /// <summary>Use 64-bit chunk offsets and optimize when writing MP4.</summary>
    public bool Optimize { get; set; }

    /// <summary>Actions given to newly added items.</summary>
    public List<QueueAction> DefaultActions { get; set; } = [];
}
