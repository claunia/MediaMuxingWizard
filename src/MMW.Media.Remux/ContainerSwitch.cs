using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Media.Remux;

/// <summary>A track whose action changes when a document switches containers.</summary>
/// <param name="Track">The document's track.</param>
/// <param name="From">Its action for the old container.</param>
/// <param name="To">The recommended action for the new container (<see cref="ImportAction.Skip"/> when it cannot be stored at all).</param>
/// <param name="Reason">Why the new container cannot keep the old action, when the muxer says.</param>
public sealed record TrackRetarget(Track Track, ImportAction From, ImportChoice To, string? Reason);

/// <summary>
/// Switching a document between MP4 and Matroska before it is saved: every track keeps its action when the new
/// container offers it, and otherwise gets the action the import dialog recommends for that container (a conversion,
/// or leaving the track out when nothing can store it).
/// </summary>
public static class ContainerSwitch
{
    /// <summary>The tracks of <paramref name="document"/> whose action has to change for <paramref name="target"/>.</summary>
    public static async Task<IReadOnlyList<TrackRetarget>> PlanAsync(MediaDocument document, ContainerKind target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var changes = new List<TrackRetarget>();
        var tracks = document.Tracks.Where(t => t is not ChapterTrack && t.Source is not null).ToList();
        foreach (var group in tracks.GroupBy(t => Path.GetFullPath(t.Source!.Path), StringComparer.Ordinal))
        {
            IReadOnlyList<ImportableTrack> inspected;
            try
            {
                inspected = await TrackImporter.InspectAsync(group.Key, target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                continue; // the save reports what cannot be read
            }

            foreach (var track in group)
            {
                var source = track.Source!;
                if (inspected.FirstOrDefault(i => i.TrackId == source.TrackId) is not { } info)
                    continue;
                var action = source.Import?.Action ?? ImportAction.Passthrough;
                // Passthrough stays only where the track is stored unchanged; an automatic conversion (SubRip → tx3g)
                // becomes the explicit choice the track's conversion menu shows.
                // OCR to SubRip becomes tx3g in MP4 by itself (SubtitleConversions.TargetIn): nothing to change.
                var ocrAsTx3g = target == ContainerKind.Mp4 && action == ImportAction.ConvertToSrt && SubtitleConversions.IsOcr(track);
                var kept = ocrAsTx3g || info.Choices.Any(c => c.Action == action) ||
                           action == ImportAction.Passthrough && info.Support.Level == TrackSupportLevel.Passthrough;
                if (kept)
                    continue;
                var recommended = info.Choice ?? info.Choices[^1];
                changes.Add(new TrackRetarget(track, action, recommended, info.Support.Reason));
            }
        }

        return changes;
    }

    /// <summary>Applies <paramref name="changes"/> and makes <paramref name="target"/> the document's container.</summary>
    public static void Apply(MediaDocument document, ContainerKind target, IEnumerable<TrackRetarget> changes)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            TrackConversions.SetAction(document, change.Track, change.To.Action, change.To.SettingsFrom(ConversionDefaults.Settings),
                change.To.OcrFrom(null));
        }

        document.Container = target;
    }
}
