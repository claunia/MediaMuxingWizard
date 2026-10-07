using System.Globalization;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// The details shown next to a track's format ("3840×2160, Main 10@5.1, DV Profile 8.1, level 6", "7.1, 48 kHz,
/// Atmos"), built from the track itself so every container shows the same thing for the same stream. Rebuilt whenever
/// something learnt from the bitstream (Atmos, the DTS product, Dolby Vision) changes the track.
/// </summary>
public static class TrackDetails
{
    /// <summary>Sets <see cref="Track.FormatDetails"/> of a video or audio track; other tracks keep theirs.</summary>
    public static void Refresh(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.FormatDetails = track switch
        {
            VideoTrack video => Video(video),
            AudioTrack audio => Audio(audio),
            _ => track.FormatDetails,
        };
    }

    public static string Video(VideoTrack video)
    {
        ArgumentNullException.ThrowIfNull(video);
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{video.PixelWidth}×{video.PixelHeight}") };
        if (video.ParNumerator > 0 && video.ParDenominator > 0 && video.ParNumerator != video.ParDenominator)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"PAR {video.ParNumerator}:{video.ParDenominator}"));
        if (video.ProfileLevel.Length > 0)
            parts.Add(video.ProfileLevel);
        if (video.DolbyVision is { } dv)
            parts.Add("DV " + dv);
        else if (video.Hdr10Plus)
            parts.Add("HDR10+");
        else if (video.Color.Transfer == 18)
            parts.Add("HLG");
        else if (video.Hdr is not null || video.Color.Transfer == 16)
            parts.Add("HDR10");
        return string.Join(", ", parts);
    }

    public static string Audio(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var parts = new List<string>();
        if (audio.Channels > 0)
            parts.Add(TrackConversions.ChannelName(audio.Channels));
        if (audio.SampleRate > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{audio.SampleRate / 1000.0:0.###} kHz"));
        if (audio.IsAtmos)
            parts.Add("Atmos");
        return string.Join(", ", parts);
    }
}
