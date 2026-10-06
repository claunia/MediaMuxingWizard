using System.Globalization;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// Applies conversion actions to document tracks: a track (pending or already in the file) can be given a conversion
/// that is performed by the next save, and the two-track actions ("AAC + Passthru", "AAC + AC3") are expanded into
/// the AAC track plus the original/AC-3 track.
/// </summary>
public static class TrackConversions
{
    /// <summary>True when a track of <paramref name="document"/> asks for an audio conversion or a subtitle OCR on save.</summary>
    public static bool HasConversions(MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tracks.Any(t => t.Source?.Import is { } i && (ConversionDefaults.IsConversion(i.Action) || SubtitleConversions.IsOcr(t)));
    }

    /// <summary>
    /// Sets the import action of <paramref name="track"/> (e.g. "Convert to AAC" on an existing DTS or FLAC track);
    /// the conversion happens on the next save. Two-track actions are expanded immediately.
    /// </summary>
    /// <remarks>
    /// A tx3g/SRT action on a bitmap subtitle track (PGS, VobSub, DVB) is an OCR conversion: <paramref name="ocr"/>
    /// (or the track's previous OCR settings, or <see cref="OcrOptions.Default"/>) is stored with it.
    /// </remarks>
    /// <returns>The tracks added to the document (the AAC track of a two-track action), possibly empty.</returns>
    /// <exception cref="InvalidOperationException">The track has no source.</exception>
    public static IReadOnlyList<Track> SetAction(MediaDocument document, Track track, ImportAction action, AudioConversionSettings? settings = null,
        OcrOptions? ocr = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(track);
        var source = track.Source ?? throw new InvalidOperationException($"Track '{track.Name}' ({track.Format}) has no source file.");
        var import = source.Import ?? new TrackImportOptions();
        var isOcr = SubtitleConversions.Target(action) is not null && (ocr is not null || import.Ocr is not null || SubtitleConversions.IsBitmapFormat(track.Format));
        track.Source = source with
        {
            Import = import with
            {
                Action = action,
                Conversion = ConversionDefaults.IsConversion(action) ? settings ?? import.Conversion ?? ConversionDefaults.Settings : import.Conversion,
                Ocr = isOcr ? ocr ?? import.Ocr ?? OcrOptions.Default : null,
            },
        };
        return ExpandTrack(document, track);
    }

    /// <summary>
    /// Expands every track whose action is <see cref="ImportAction.AacPlusPassthrough"/> or
    /// <see cref="ImportAction.AacPlusAc3"/>: an AAC conversion of the track is inserted before it (taking over its
    /// enabled/default state, in the same alternate group), the track itself is disabled, copied (or converted to
    /// AC-3) and its fallback points at the AAC track. Must be called on the document's (UI) context.
    /// </summary>
    /// <returns>The AAC tracks that were added.</returns>
    public static IReadOnlyList<Track> Expand(MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var added = new List<Track>();
        foreach (var track in document.Tracks.ToList())
            added.AddRange(ExpandTrack(document, track));
        return added;
    }

    private static List<Track> ExpandTrack(MediaDocument document, Track track)
    {
        if (track.Source is not { Import: { } import } source || import.Action is not (ImportAction.AacPlusPassthrough or ImportAction.AacPlusAc3))
            return [];

        var settings = import.Conversion ?? ConversionDefaults.Settings;
        var original = track as AudioTrack;
        var channels = original?.Channels ?? 0;
        var sampleRate = original?.SampleRate ?? 0;
        var aacChannels = settings.AacChannels(channels);
        var aacRate = AudioConversionSettings.AacSampleRate(sampleRate);
        var group = track.AlternateGroup > 0 ? track.AlternateGroup : 1;
        var aac = new AudioTrack
        {
            Id = 0,
            Name = track.Name,
            Language = track.Language,
            Enabled = track.Enabled,
            IsDefault = track.IsDefault || track.Enabled,
            AlternateGroup = group,
            StartOffset = track.StartOffset,
            Format = "AAC",
            CodecId = "mp4a",
            Channels = aacChannels,
            SampleRate = aacRate,
            ChannelLayout = ChannelName(aacChannels),
            FormatDetails = Details(aacChannels, aacRate),
            Duration = track.Duration,
            Timescale = (uint)aacRate,
            Volume = original?.Volume ?? 1.0,
            FollowsSubtitle = original?.FollowsSubtitle,
            Source = source with { Import = import with { Action = ImportAction.ConvertToAac, Conversion = settings } },
        };
        foreach (var c in track.MediaCharacteristics)
            aac.MediaCharacteristics.Add(c);

        track.Source = source with
        {
            Import = import with
            {
                Action = import.Action == ImportAction.AacPlusAc3 ? ImportAction.ConvertToAc3 : ImportAction.Passthrough,
                Conversion = settings,
            },
        };
        track.Enabled = false;
        track.IsDefault = false;
        track.AlternateGroup = group;
        if (original is not null)
            original.Fallback = aac;
        if (import.Action == ImportAction.AacPlusAc3 && track.IsPending)
        {
            track.Format = "AC-3";
            track.CodecId = "ac-3";
        }

        var index = document.Tracks.IndexOf(track);
        document.Tracks.Insert(index < 0 ? document.Tracks.Count : index, aac);
        return [aac];
    }

    /// <summary>Short description of a channel count ("Mono", "Stereo", "5.1", …).</summary>
    public static string ChannelName(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => channels.ToString(CultureInfo.InvariantCulture) + " ch",
    };

    /// <summary>"Stereo, 48 kHz"-style details of a converted track.</summary>
    public static string Details(int channels, int sampleRate) =>
        sampleRate > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ChannelName(channels)}, {sampleRate / 1000.0:0.###} kHz")
            : ChannelName(channels);
}
