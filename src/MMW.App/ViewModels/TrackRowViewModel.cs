using System.ComponentModel;
using System.Globalization;
using MMW.App.Resources;
using MMW.Core.Languages;
using MMW.Core.Model;

namespace MMW.App.ViewModels;

/// <summary>A row of the track list: either the "Metadata" pseudo-row or a track.</summary>
public sealed class TrackRowViewModel : ViewModelBase
{
    private TrackRowViewModel(Track? track)
    {
        Track = track;
        if (track is not null)
            track.PropertyChanged += OnTrackChanged;
    }

    public static TrackRowViewModel ForMetadata() => new(null);

    public static TrackRowViewModel ForTrack(Track track) => new(track);

    public Track? Track { get; }

    public bool IsMetadata => Track is null;

    public bool IsTrack => Track is not null;

    public string IdText => Track switch
    {
        null => string.Empty,
        ChapterTrack => string.Empty,
        { IsPending: true } => "na",
        _ => Track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public bool Enabled
    {
        get => Track?.Enabled ?? true;
        set
        {
            if (Track is not null && Track.Enabled != value)
                Track.Enabled = value;
        }
    }

    public bool CanToggleEnabled => Track is not null and not ChapterTrack;

    public string Name
    {
        get => Track is null ? Strings.Tracks_Row_Metadata : Track.Name;
        set
        {
            if (Track is not null && Track.Name != value)
                Track.Name = value;
        }
    }

    public string Duration => Track is null ? string.Empty : FormatDuration(Track.Duration);

    public string Language => Track is null or ChapterTrack ? string.Empty : LanguageTable.DisplayName(Track.Language);

    public string Format => Track switch
    {
        null => Strings.Tracks_Row_MetadataFormat,
        ChapterTrack c => c.FormatDetails.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Tracks_Row_ChaptersFormat, c.FormatDetails) : Strings.Tracks_Row_Chapters,
        _ => Track.DisplayFormat,
    };

    /// <summary>Resource key of the icon shown for the row kind.</summary>
    public string IconKey => Track?.Kind switch
    {
        null => "IconTag",
        TrackKind.Video => "IconFilm",
        TrackKind.Audio => "IconAudio",
        TrackKind.Subtitle => "IconSubtitles",
        TrackKind.ClosedCaption => "IconCaptions",
        TrackKind.Chapters => "IconChapters",
        _ => "IconAction",
    };

    public static string FormatDuration(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Core.Model.Track.Enabled):
                OnPropertyChanged(nameof(Enabled));
                break;
            case nameof(Core.Model.Track.Name):
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(Core.Model.Track.Language):
                OnPropertyChanged(nameof(Language));
                break;
            case nameof(Core.Model.Track.Id):
                OnPropertyChanged(nameof(IdText));
                break;
        }
    }

    /// <summary>Raises change notifications for every column (after a save re-reads track IDs, …).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public void Detach()
    {
        if (Track is not null)
            Track.PropertyChanged -= OnTrackChanged;
    }
}
