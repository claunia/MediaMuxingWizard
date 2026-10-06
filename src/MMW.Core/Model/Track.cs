using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MMW.Core.Model;

/// <summary>Common properties of every track, regardless of container.</summary>
public abstract partial class Track : ObservableObject
{
    protected Track(TrackKind kind) => Kind = kind;

    public TrackKind Kind { get; }

    /// <summary>Track ID in the source/target file (0 when the track has not been written yet).</summary>
    [ObservableProperty]
    private uint _id;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>BCP-47 language tag ("und" when unknown).</summary>
    [ObservableProperty]
    private string _language = "und";

    /// <summary>MP4 alternate group (0 = none). Mapped to the default flag in Matroska.</summary>
    [ObservableProperty]
    private int _alternateGroup;

    /// <summary>Start delay (edit list in MP4, timestamp offset when remuxing).</summary>
    [ObservableProperty]
    private TimeSpan _startOffset;

    /// <summary>Matroska "default" flag; derived from <see cref="Enabled"/> for MP4.</summary>
    [ObservableProperty]
    private bool _isDefault;

    /// <summary>Matroska "forced" flag.</summary>
    [ObservableProperty]
    private bool _isForced;

    public ObservableCollection<string> MediaCharacteristics { get; } = [];

    /// <summary>Short codec/format name (e.g. "H.264", "AAC", "Text").</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Raw codec identifier (MP4 sample entry four-cc or Matroska CodecID).</summary>
    public string CodecId { get; set; } = string.Empty;

    /// <summary>Human-readable details such as "1920×1080, High@4.1" or "6 ch, 48 kHz".</summary>
    public string FormatDetails { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    /// <summary>Average bitrate in bits per second (0 when unknown).</summary>
    public long Bitrate { get; set; }

    public long DataLength { get; set; }

    public uint Timescale { get; set; }

    /// <summary>Where the samples of this track come from (and how they are imported or converted).</summary>
    [ObservableProperty]
    private TrackSource? _source;

    /// <summary>True for tracks that are not yet in the file on disk (imported, waiting to be muxed).</summary>
    public bool IsPending => Id == 0;

    public string DisplayFormat => string.IsNullOrEmpty(FormatDetails) ? Format : $"{Format}, {FormatDetails}";
}

/// <summary>Identifies a track in a source file.</summary>
public sealed record TrackSource(string Path, ContainerKind Container, uint TrackId)
{
    /// <summary>Import settings of a pending track (null for tracks already in the document's file).</summary>
    public Media.TrackImportOptions? Import { get; init; }
}

public sealed partial class VideoTrack : Track
{
    public VideoTrack() : base(TrackKind.Video)
    {
    }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>Display (presentation) width; MP4 <c>tkhd</c> width.</summary>
    [ObservableProperty]
    private double _displayWidth;

    [ObservableProperty]
    private double _displayHeight;

    /// <summary>Pixel aspect ratio numerator (<c>pasp</c> hSpacing).</summary>
    [ObservableProperty]
    private int _parNumerator = 1;

    [ObservableProperty]
    private int _parDenominator = 1;

    [ObservableProperty]
    private ColorInfo _color = ColorInfo.Unspecified;

    public double FrameRate { get; set; }

    /// <summary>Codec profile/level description (e.g. "High@4.1").</summary>
    public string ProfileLevel { get; set; } = string.Empty;

    public HdrInfo? Hdr { get; set; }

    public DolbyVisionInfo? DolbyVision { get; set; }
}

public sealed partial class AudioTrack : Track
{
    public AudioTrack() : base(TrackKind.Audio)
    {
    }

    /// <summary>Linear volume, 1.0 = 0 dB.</summary>
    [ObservableProperty]
    private double _volume = 1.0;

    /// <summary>Fallback track (MP4 <c>tref/fall</c>), typically AAC for AC-3/DTS.</summary>
    [ObservableProperty]
    private Track? _fallback;

    /// <summary>Subtitle track that follows this audio (MP4 <c>tref/folw</c>).</summary>
    [ObservableProperty]
    private Track? _followsSubtitle;

    public int Channels { get; set; }

    public int SampleRate { get; set; }

    public string ChannelLayout { get; set; } = string.Empty;

    /// <summary>E-AC-3 with Joint Object Coding (Dolby Atmos).</summary>
    public bool IsAtmos { get; set; }
}

public sealed partial class SubtitleTrack : Track
{
    public SubtitleTrack() : base(TrackKind.Subtitle)
    {
    }

    [ObservableProperty]
    private ForcedSubtitleMode _forcedMode;

    /// <summary>Subtitle track holding only the forced subtitles (MP4 <c>tref/forc</c>).</summary>
    [ObservableProperty]
    private Track? _forcedTrack;

    /// <summary>Place subtitles at the top of the frame.</summary>
    [ObservableProperty]
    private bool _placeAtTop;

    public int Width { get; set; }

    public int Height { get; set; }
}

public sealed class ClosedCaptionTrack : Track
{
    public ClosedCaptionTrack() : base(TrackKind.ClosedCaption)
    {
    }
}

/// <summary>Chapter track; the chapter list itself lives on <see cref="MediaDocument.Chapters"/>.</summary>
public sealed class ChapterTrack : Track
{
    public ChapterTrack() : base(TrackKind.Chapters)
    {
        Format = "Text";
    }
}

public sealed class OtherTrack : Track
{
    public OtherTrack() : base(TrackKind.Other)
    {
    }
}
