using System.Globalization;
using MMW.Core.Chapters;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Actions;

/// <summary>Batch operations on a document's tracks and chapters, shared by the UI and the queue.</summary>
public static class TrackActions
{
    /// <summary>Removes every track name.</summary>
    public static void ClearTrackNames(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var t in doc.Tracks.Where(t => t is not ChapterTrack))
            t.Name = string.Empty;
    }

    /// <summary>
    /// Names every audio track from what it is: its layout, its codec as listeners know it and Dolby Atmos or the DTS
    /// product ("7.1 Surround (Dolby TrueHD Atmos)", "5.1 Surround (DTS-HD MA)", "Stereo (AAC)"). Run
    /// <see cref="DescribeAudioAsync"/> first so Atmos and DTS products found in the bitstream are known.
    /// </summary>
    public static void PrettifyAudioNames(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var a in doc.Tracks.OfType<AudioTrack>())
        {
            if (PrettyAudioName(a) is { } name)
                a.Name = name;
        }
    }

    /// <summary>
    /// Reads from the bitstream what the containers do not say and the names show: Dolby Atmos in TrueHD and E-AC-3,
    /// and the DTS product (DTS-HD MA, DTS:X…). Tracks already examined are skipped; unreadable ones are left as they are.
    /// </summary>
    public static async Task DescribeAudioAsync(MediaDocument doc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var audio in doc.Tracks.OfType<AudioTrack>().ToList())
        {
            try
            {
                if (AtmosDetector.NeedsCheck(audio))
                    await AtmosDetector.DescribeAsync(audio, cancellationToken).ConfigureAwait(true);
                if (DtsDetector.NeedsCheck(audio))
                    await DtsDetector.DescribeAsync(audio, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                Diagnostics.AppLog.Debug(string.Format(CultureInfo.CurrentCulture, Strings.Log_AudioCheckSkippedFormat, audio.Id, ex.Message));
            }
        }
    }

    /// <summary>The name <see cref="PrettifyAudioNames"/> gives <paramref name="audio"/>; null when nothing is known about it.</summary>
    public static string? PrettyAudioName(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var layout = audio.Channels switch
        {
            <= 0 => null,
            1 => Strings.Name_Mono,
            2 => Strings.Name_Stereo,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Name_SurroundFormat, TrackConversions.ChannelName(audio.Channels)),
        };
        var codec = CodecName(audio);
        return (layout, codec) switch
        {
            (null, null) => null,
            (null, { } c) => c,
            ({ } l, null) => l,
            ({ } l, { } c) => string.Format(CultureInfo.CurrentCulture, Strings.Name_AudioFormat, l, c),
        };
    }

    /// <summary>The codec as listeners know it ("Dolby Digital Plus Atmos", "DTS:X", "AAC"); null when unknown.</summary>
    private static string? CodecName(AudioTrack audio)
    {
        var format = audio.Format;
        var atmos = audio.IsAtmos ? " Atmos" : string.Empty;
        return format switch
        {
            "AC-3" => "Dolby Digital",
            "E-AC-3" => "Dolby Digital Plus" + atmos,
            "TrueHD" => "Dolby TrueHD" + atmos,
            "MLP" => "Dolby MLP",
            "AC-4" => "Dolby AC-4" + atmos,
            // The product found in the bitstream says more than "DTS" (DTS-HD MA, DTS:X, DTS Express…).
            "DTS" => audio.Profile.Length > 0 ? audio.Profile : "DTS",
            "ALAC" => "Apple Lossless",
            "MPEG-H" => "MPEG-H 3D Audio",
            "" => null,
            _ => format,
        };
    }

    /// <summary>Replaces the chapters with marks every <paramref name="interval"/> (or a single one at the start).</summary>
    public static void InsertChaptersEvery(MediaDocument doc, TimeSpan? interval)
    {
        ArgumentNullException.ThrowIfNull(doc);
        RemoveAll(doc.Chapters);
        if (interval is not { } step || step <= TimeSpan.Zero)
        {
            doc.Chapters.Add(new Chapter(TimeSpan.Zero, string.Format(CultureInfo.CurrentCulture, Strings.Label_ChapterNumber, 1)));
        }
        else
        {
            var n = 1;
            for (var t = TimeSpan.Zero; t < doc.Duration || n == 1; t += step)
                doc.Chapters.Add(new Chapter(t, string.Format(CultureInfo.CurrentCulture, Strings.Label_ChapterNumber, n++)));
        }

        EnsureChapterTrack(doc);
    }

    /// <summary>Renames all chapters "Chapter 1", "Chapter 2", ….</summary>
    public static void RenameChapters(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var n = 1;
        foreach (var c in doc.Chapters.OrderBy(c => c.Start))
            c.Title = string.Format(CultureInfo.CurrentCulture, Strings.Label_ChapterNumber, n++);
    }

    /// <summary>Replaces the chapters (item by item, so the change can be undone).</summary>
    public static void ReplaceChapters(MediaDocument doc, IEnumerable<Chapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(doc);
        RemoveAll(doc.Chapters);
        foreach (var c in chapters)
            doc.Chapters.Add(c);
        EnsureChapterTrack(doc);
    }

    /// <summary>Removes all items one by one (unlike Clear, this raises undoable Remove notifications).</summary>
    public static void RemoveAll<T>(System.Collections.ObjectModel.Collection<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var i = items.Count - 1; i >= 0; i--)
            items.RemoveAt(i);
    }

    /// <summary>Adds the pseudo chapter track row when the document has chapters but no such row.</summary>
    public static void EnsureChapterTrack(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Chapters.Count > 0 && !doc.Tracks.OfType<ChapterTrack>().Any())
            doc.Tracks.Add(new ChapterTrack { Name = "Chapters", Language = "en", Duration = doc.Duration, Enabled = false });
    }
}
