using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.Queue;

namespace MMW.App.Services;

/// <summary>
/// Queue action: loads subtitle files next to the source whose names start with the source's name, e.g.
/// "Movie.srt", "Movie.fr.srt" or "Movie.English.forced.ass" (Subler's "Load external subtitles").
/// </summary>
public sealed class LoadExternalSubtitlesAction : QueueAction
{
    private static readonly string[] s_extensions = [".srt", ".ass", ".ssa", ".vtt"];

    public override string Description => "Load external subtitles";

    public override async Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = context.Item.SourcePath;
        var directory = Path.GetDirectoryName(source) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(source);
        var files = Directory.EnumerateFiles(directory)
            .Where(f => s_extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                        Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            var tracks = await TrackImporter.InspectAsync(file, context.TargetContainer, cancellationToken);
            var language = LanguageFromName(Path.GetFileNameWithoutExtension(file)[baseName.Length..]);
            foreach (var t in tracks)
            {
                t.Selected = t.Action != ImportAction.Skip;
                if (language is not null)
                    t.Language = language;
            }

            var added = TrackImporter.AddToDocument(context.Document, tracks.Where(t => t.Selected));
            if (Path.GetFileName(file).Contains(".forced.", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var a in added.OfType<SubtitleTrack>())
                {
                    a.IsForced = true;
                    a.ForcedMode = ForcedSubtitleMode.AllSamplesForced;
                }
            }

            context.Log($"Loaded {added.Count} subtitle track(s) from {Path.GetFileName(file)}.");
        }
    }

    /// <summary>Finds a language in ".fr", ".eng" or ".English" style suffixes.</summary>
    internal static string? LanguageFromName(string suffix)
    {
        foreach (var part in suffix.Split('.', '_', '-', ' ').Where(p => p.Length >= 2))
        {
            if (part.Length is 2 or 3 && LanguageTable.Find(part.ToLowerInvariant()) is { } byCode)
                return byCode.Tag;
            if (LanguageTable.All.FirstOrDefault(l => string.Equals(l.Name, part, StringComparison.OrdinalIgnoreCase)) is { } byName)
                return byName.Tag;
        }

        return null;
    }
}

/// <summary>
/// Queue action (always run last): gives tracks the target container cannot carry their default conversion, or
/// drops them, so changing the output type (e.g. MKV with Vorbis to M4V) works without manual choices.
/// </summary>
public sealed class PrepareTracksForTargetAction : QueueAction
{
    public override string Description => "Prepare tracks for the output format";

    public override async Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var doc = context.Document;
        if (doc.Path is null)
            return;

        var problems = (await Remuxer.CheckAsync(doc, context.TargetContainer, cancellationToken))
            .Where(p => p.Support.Level is TrackSupportLevel.NeedsConversion or TrackSupportLevel.Unsupported)
            .Select(p => p.Track)
            .Where(t => t.Source?.Import is null)
            .ToList();
        if (problems.Count == 0)
            return;

        var inspected = await TrackImporter.InspectAsync(doc.Path, context.TargetContainer, cancellationToken);
        foreach (var track in problems)
        {
            var source = inspected.FirstOrDefault(t => t.TrackId == (track.Source?.TrackId ?? track.Id));
            var choice = source?.Choice;
            if (choice is null || choice.Action == ImportAction.Skip)
            {
                TrackConversions.SetAction(doc, track, ImportAction.Skip);
                context.Log($"Dropping track {track.Id} ({track.Format}): it cannot be stored in the output format.");
            }
            else
            {
                TrackConversions.SetAction(doc, track, choice.Action, choice.SettingsFrom(ConversionDefaults.Settings));
                context.Log($"Track {track.Id} ({track.Format}): {choice.DisplayName}.");
            }
        }
    }
}
