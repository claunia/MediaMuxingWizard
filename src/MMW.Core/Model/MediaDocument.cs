using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Resources;

namespace MMW.Core.Model;

/// <summary>An opened (or new) media file: its tracks, tags, artwork and chapters.</summary>
public sealed partial class MediaDocument : ObservableObject
{
    public MediaDocument(string? path, ContainerKind container)
    {
        _path = path;
        Container = container;
        Metadata.Changed += (_, _) => IsDirty = true;
        Tracks.CollectionChanged += (_, _) => IsDirty = true;
        Chapters.CollectionChanged += (_, _) => IsDirty = true;
    }

    /// <summary>Path of the file on disk; null for a document that was never saved.</summary>
    [ObservableProperty]
    private string? _path;

    [ObservableProperty]
    private bool _isDirty;

    public ContainerKind Container { get; set; }

    public ObservableCollection<Track> Tracks { get; } = [];

    public MetadataSet Metadata { get; } = new();

    public ObservableCollection<Chapter> Chapters { get; } = [];

    /// <summary>Total duration (the longest track).</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Size of the file on disk in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Opaque state owned by the container handler that read the file (box tree, EBML layout, …), used to
    /// update the file in place on save.
    /// </summary>
    public object? ContainerState { get; set; }

    public string DisplayName => Path is null ? Strings.Label_Untitled : System.IO.Path.GetFileName(Path);

    partial void OnPathChanged(string? value) => OnPropertyChanged(nameof(DisplayName));

    public IEnumerable<T> TracksOf<T>() where T : Track => Tracks.OfType<T>();

    public VideoTrack? MainVideo => Tracks.OfType<VideoTrack>().FirstOrDefault();
}
