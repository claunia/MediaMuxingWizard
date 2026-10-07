using System.Globalization;
using MMW.Core.Chapters;
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

    /// <summary>Names audio tracks "Mono Audio", "Stereo Audio" or "Surround Audio" from their channel count.</summary>
    public static void PrettifyAudioNames(MediaDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var a in doc.Tracks.OfType<AudioTrack>())
        {
            a.Name = a.Channels switch
            {
                1 => Strings.Name_MonoAudio,
                2 => Strings.Name_StereoAudio,
                > 2 => Strings.Name_SurroundAudio,
                _ => a.Name,
            };
        }
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
