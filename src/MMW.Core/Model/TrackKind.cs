namespace MMW.Core.Model;

public enum TrackKind
{
    Video,
    Audio,
    Subtitle,
    ClosedCaption,
    Chapters,
    Other,
}

/// <summary>Forced-subtitle mode of a subtitle track.</summary>
public enum ForcedSubtitleMode
{
    None,
    SomeSamplesForced,
    AllSamplesForced,
}
