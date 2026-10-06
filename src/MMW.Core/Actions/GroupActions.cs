using MMW.Core.Languages;
using MMW.Core.Model;

namespace MMW.Core.Actions;

/// <summary>Alternate-group, fallback and language helpers (Subler's "Organize alternate groups" and friends).</summary>
public static class GroupActions
{
    private static readonly string[] s_auxiliaryWords = ["director", "commentary", "lyric", "karaoke", "sign"];

    /// <summary>
    /// Puts video in group 0, audio in group 1 and subtitles/closed captions in group 2; keeps one enabled track
    /// per group (the first video and the first main audio track), disables subtitles and chapters, and optionally
    /// infers languages and media characteristics from track names.
    /// </summary>
    public static void OrganizeAlternateGroups(MediaDocument doc, bool inferMediaCharacteristics = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        InferChineseScripts(doc);
        if (inferMediaCharacteristics)
            InferMediaCharacteristics(doc);

        var firstVideo = true;
        var firstAudio = doc.Tracks.OfType<AudioTrack>()
            .OrderBy(a => a.MediaCharacteristics.Contains(MediaCharacteristics.AuxiliaryContent) ? 1 : 0)
            .ThenBy(a => doc.Tracks.IndexOf(a))
            .FirstOrDefault();

        foreach (var track in doc.Tracks)
        {
            switch (track)
            {
                case VideoTrack:
                    track.AlternateGroup = 0;
                    track.Enabled = firstVideo;
                    firstVideo = false;
                    break;
                case AudioTrack:
                    track.AlternateGroup = 1;
                    track.Enabled = track == firstAudio;
                    break;
                case SubtitleTrack or ClosedCaptionTrack:
                    track.AlternateGroup = 2;
                    track.Enabled = false;
                    break;
                case ChapterTrack:
                    track.Enabled = false;
                    break;
            }
        }
    }

    /// <summary>"Chinese … Simplified/Traditional" names become zh-Hans / zh-Hant.</summary>
    public static void InferChineseScripts(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var t in doc.Tracks)
        {
            if (!t.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || t.Language.Contains('-', StringComparison.Ordinal))
                continue;
            if (t.Name.Contains("simplified", StringComparison.OrdinalIgnoreCase))
                t.Language = "zh-Hans";
            else if (t.Name.Contains("traditional", StringComparison.OrdinalIgnoreCase))
                t.Language = "zh-Hant";
        }
    }

    /// <summary>
    /// Derives media characteristics from track names (commentary → auxiliary, SDH → accessibility tags,
    /// forced → forced-only) and marks one main track per kind and language.
    /// </summary>
    public static void InferMediaCharacteristics(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var t in doc.Tracks.Where(t => t is AudioTrack or SubtitleTrack or ClosedCaptionTrack or VideoTrack))
        {
            var name = t.Name;
            if (s_auxiliaryWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                Add(t, MediaCharacteristics.AuxiliaryContent);
            if (t is SubtitleTrack or ClosedCaptionTrack)
            {
                if (name.Contains("sdh", StringComparison.OrdinalIgnoreCase) || name.Contains("hearing", StringComparison.OrdinalIgnoreCase) || t is ClosedCaptionTrack)
                {
                    Add(t, MediaCharacteristics.TranscribesSpokenDialog);
                    Add(t, MediaCharacteristics.DescribesMusicAndSound);
                }

                if (name.Contains("forced", StringComparison.OrdinalIgnoreCase) || t.IsForced || t is SubtitleTrack { ForcedMode: ForcedSubtitleMode.AllSamplesForced })
                    Add(t, MediaCharacteristics.ForcedOnly);
            }
        }

        // One main-program track per (kind, language), skipping auxiliary and accessibility variants.
        foreach (var group in doc.Tracks.Where(t => t is AudioTrack or SubtitleTrack or ClosedCaptionTrack or VideoTrack).GroupBy(t => (t.Kind, t.Language)))
        {
            if (group.Any(t => t.MediaCharacteristics.Contains(MediaCharacteristics.MainProgramContent)))
                continue;
            var main = group.FirstOrDefault(t =>
                !t.MediaCharacteristics.Contains(MediaCharacteristics.AuxiliaryContent) &&
                !t.MediaCharacteristics.Contains(MediaCharacteristics.ForcedOnly) &&
                !t.MediaCharacteristics.Contains(MediaCharacteristics.TranscribesSpokenDialog));
            if (main is not null)
                Add(main, MediaCharacteristics.MainProgramContent);
        }
    }

    /// <summary>
    /// Gives every AC-3, E-AC-3 and DTS track without a fallback the nearest earlier AAC track in the same
    /// language (or any language when none matches).
    /// </summary>
    public static void FixAudioFallbacks(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var audio = doc.Tracks.OfType<AudioTrack>().ToList();
        for (var i = 0; i < audio.Count; i++)
        {
            var track = audio[i];
            if (track.Fallback is not null || !NeedsFallback(track))
                continue;
            var earlier = audio.Take(i).Where(IsAac).Reverse().ToList();
            var later = audio.Skip(i + 1).Where(IsAac).ToList();
            track.Fallback = earlier.FirstOrDefault(a => a.Language == track.Language)
                             ?? later.FirstOrDefault(a => a.Language == track.Language)
                             ?? earlier.FirstOrDefault()
                             ?? later.FirstOrDefault();
        }
    }

    public static bool NeedsFallback(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Format is "AC-3" or "E-AC-3" or "DTS" || track.CodecId is "ac-3" or "ec-3" or "dtsc" or "dtsh" or "dtsl" or "dtse" or "A_AC3" or "A_EAC3" or "A_DTS";
    }

    private static bool IsAac(AudioTrack track) => track.Format.Contains("AAC", StringComparison.Ordinal);

    /// <summary>Sets <paramref name="language"/> on every track whose language is undetermined.</summary>
    public static void CompleteLanguages(MediaDocument doc, string language)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var t in doc.Tracks.Where(t => t is not ChapterTrack && (string.IsNullOrEmpty(t.Language) || t.Language == LanguageTable.Undetermined)))
            t.Language = language;
    }

    /// <summary>Enables the first track of <paramref name="kind"/> in <paramref name="language"/> and disables its alternates.</summary>
    /// <returns>True when a matching track was found.</returns>
    public static bool EnableTrackWithLanguage(MediaDocument doc, TrackKind kind, string language)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var candidates = doc.Tracks.Where(t => t.Kind == kind || (kind == TrackKind.Subtitle && t.Kind == TrackKind.ClosedCaption)).ToList();
        var match = candidates.FirstOrDefault(t => LanguageTable.ToIso639_2T(t.Language) == LanguageTable.ToIso639_2T(language) &&
                                                   !t.MediaCharacteristics.Contains(MediaCharacteristics.AuxiliaryContent))
                    ?? candidates.FirstOrDefault(t => LanguageTable.ToIso639_2T(t.Language) == LanguageTable.ToIso639_2T(language));
        if (match is null)
            return false;
        foreach (var t in candidates.Where(t => t.AlternateGroup == match.AlternateGroup || match.AlternateGroup == 0))
            t.Enabled = t == match;
        return true;
    }

    /// <summary>Sets the colour description of every video track.</summary>
    public static void ApplyColorSpace(MediaDocument doc, ColorInfo color)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var v in doc.Tracks.OfType<VideoTrack>())
            v.Color = color with { FullRange = v.Color.FullRange };
    }

    private static void Add(Track t, string characteristic)
    {
        if (!t.MediaCharacteristics.Contains(characteristic))
            t.MediaCharacteristics.Add(characteristic);
    }
}
