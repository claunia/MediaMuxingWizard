using CommunityToolkit.Mvvm.ComponentModel;

namespace MMW.Core.Chapters;

/// <summary>A single chapter mark.</summary>
public sealed partial class Chapter : ObservableObject
{
    public Chapter()
    {
    }

    public Chapter(TimeSpan start, string title)
    {
        _start = start;
        _title = title;
    }

    [ObservableProperty]
    private TimeSpan _start;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Optional thumbnail (JPEG) shown in the chapter strip and written as chapter preview image.</summary>
    [ObservableProperty]
    private byte[]? _thumbnail;

    public Chapter Clone() => new(Start, Title) { Thumbnail = Thumbnail };
}
