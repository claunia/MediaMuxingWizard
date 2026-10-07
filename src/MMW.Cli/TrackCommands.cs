using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MMW.Cli.Resources;
using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;

namespace MMW.Cli;

/// <summary>
/// Commands that choose tracks and what happens to them, the way the editor's import dialog and track inspector do:
/// <c>probe</c>, <c>import</c>, <c>remux</c>, <c>extract</c> and the conversion options of <c>tracks</c>.
/// </summary>
/// <remarks>
/// Per-track options take a value for every track ("--language de") or for one ("--language 2=de"); with several
/// sources a track is "&lt;source&gt;:&lt;track&gt;" ("--action 2:3=ac3", sources counted from 1). Actions are named as
/// <c>probe</c> lists them (copy, aac, aac-stereo, ac3, aac+copy, tx3g, ass, srt-ocr …).
/// </remarks>
internal static partial class TrackCommands
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    // ------------------------------------------------------------------ actions

    /// <summary>The command-line name of an import choice.</summary>
    public static string Name(ImportChoice choice) => choice.Action switch
    {
        ImportAction.Passthrough => "copy",
        ImportAction.ConvertToTx3g => choice.Ocr ? "tx3g-ocr" : "tx3g",
        ImportAction.ConvertToSrt => choice.Ocr ? "srt-ocr" : "srt",
        ImportAction.ConvertToAss => "ass",
        ImportAction.ConvertToSsa => "ssa",
        ImportAction.ConvertToWebVtt => "webvtt",
        ImportAction.ConvertToAac => choice.Mixdown switch
        {
            AudioMixdown.DolbyProLogicII => "aac-dpl2",
            AudioMixdown.DolbyProLogic => "aac-dpl",
            AudioMixdown.Stereo => "aac-stereo",
            AudioMixdown.Mono => "aac-mono",
            AudioMixdown.Multichannel => "aac-multichannel",
            _ => "aac",
        },
        ImportAction.ConvertToAc3 => "ac3",
        ImportAction.AacPlusPassthrough => "aac+copy",
        ImportAction.AacPlusAc3 => "aac+ac3",
        ImportAction.ConvertToPcm => "pcm",
        ImportAction.ConvertToAlac => "alac",
        _ => "skip",
    };

    /// <summary>The choice called <paramref name="name"/> (or an alias of it) among those offered for a track.</summary>
    public static ImportChoice Resolve(IReadOnlyList<ImportChoice> choices, string name, int channels, string what)
    {
        var key = name.Trim().ToLowerInvariant() switch
        {
            "passthrough" or "passthru" or "pass" or "keep" => "copy",
            "aac+passthru" or "aac+passthrough" or "aac+pass" => "aac+copy",
            "vtt" or "wvtt" => "webvtt",
            "subrip" => "srt",
            "lpcm" => "pcm",
            "none" or "drop" => "skip",
            var k => k,
        };
        var match = choices.FirstOrDefault(c => Name(c) == key);
        if (match is null && key == "aac")
        {
            // Plain "aac": the mixdown the conversion settings choose for this many channels.
            var mixdown = ConversionDefaults.Settings.EffectiveMixdown(channels);
            match = choices.FirstOrDefault(c => c.Action == ImportAction.ConvertToAac && c.Mixdown == mixdown) ??
                    choices.FirstOrDefault(c => c.Action == ImportAction.ConvertToAac);
        }

        if (match is null && key == "ocr")
            match = choices.FirstOrDefault(c => c.Ocr);
        return match ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ActionNotOffered, name, what, string.Join(", ", choices.Select(Name))));
    }

    // ------------------------------------------------------------------ track references

    /// <summary>A per-track option value: for every track, or for one track (optionally of one source).</summary>
    private sealed record Assignment(int? Source, uint? Track, string Value)
    {
        public bool Matches(int source, uint track) => (Source is null || Source == source) && (Track is null || Track == track);

        /// <summary>How specific it is (the most specific matching assignment wins).</summary>
        public int Rank => (Source is null ? 0 : 1) + (Track is null ? 0 : 2);
    }

    private static List<Assignment> Assignments(Arguments a, string option) =>
        a.Values(option).Select(v =>
        {
            var m = AssignmentRegex().Match(v);
            return m.Success
                ? new Assignment(m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                    uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value)
                : new Assignment(null, null, v);
        }).ToList();

    private static string? Pick(List<Assignment> assignments, int source, uint track) =>
        assignments.Where(x => x.Matches(source, track)).OrderByDescending(x => x.Rank).Select(x => x.Value).FirstOrDefault();

    /// <summary>Track numbers: "3", "1,3", "2-5", "1,4-6".</summary>
    private static HashSet<uint> Ids(string text)
    {
        var ids = new HashSet<uint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && uint.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var from) &&
                uint.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var to) && to >= from)
            {
                for (var i = from; i <= to; i++)
                    ids.Add(i);
            }
            else if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
            else
            {
                throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NotTrackList, text));
            }
        }

        return ids;
    }

    /// <summary>"movie.mkv:2,4" → the file and the tracks picked from it (null: not given), unless the whole text is a file.</summary>
    private static (string Path, HashSet<uint>? Ids) SourceSpec(string spec)
    {
        if (File.Exists(spec))
            return (spec, null);
        var colon = spec.LastIndexOf(':');
        if (colon > 0 && File.Exists(spec[..colon]) && IdListRegex().IsMatch(spec[(colon + 1)..]))
            return (spec[..colon], Ids(spec[(colon + 1)..]));
        throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, spec), spec);
    }

    /// <summary>The path when the file exists; otherwise the (localized) error the commands report.</summary>
    private static string Existing(string path) =>
        File.Exists(path) ? path : throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, path), path);

    private static bool Bool(string value, string option) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_BoolOption, option, value)),
    };

    // ------------------------------------------------------------------ probe

    /// <summary>mmw probe &lt;source&gt;... [--target mp4|mkv] [--json]: the tracks of any importable file and what can be done with each.</summary>
    public static async Task<int> ProbeAsync(Arguments a, TextWriter output)
    {
        if (a.Positional.Count == 0)
            throw new UsageException(Strings.Error_ProbeUsage);
        MediaRemux.EnsureRegistered();
        var targets = a.Value("target")?.ToLowerInvariant() switch
        {
            null => new[] { ContainerKind.Mp4, ContainerKind.Matroska },
            "mp4" or "m4v" or "mov" => [ContainerKind.Mp4],
            "mkv" or "matroska" or "webm" => [ContainerKind.Matroska],
            var t => throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownTarget, t)),
        };
        var result = new List<object>();
        foreach (var source in a.Positional)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, source), source);
            var byTarget = new Dictionary<ContainerKind, IReadOnlyList<ImportableTrack>>();
            foreach (var target in targets)
                byTarget[target] = await TrackImporter.InspectAsync(source, target);
            var first = byTarget[targets[0]];
            if (a.Has("json"))
            {
                result.Add(new
                {
                    file = source,
                    format = first.Count > 0 ? first[0].SourceFormat : null,
                    tracks = first.Select(t => new
                    {
                        id = t.TrackId,
                        kind = t.Kind.ToString(),
                        format = t.Format,
                        details = t.Details,
                        language = t.Language,
                        name = t.Name,
                        duration = t.Duration.TotalSeconds,
                        actions = targets.ToDictionary(k => k == ContainerKind.Mp4 ? "mp4" : "mkv", k =>
                        {
                            var same = byTarget[k].First(x => x.TrackId == t.TrackId);
                            return new { recommended = same.Choice is { } c ? Name(c) : null, offered = same.Choices.Select(Name), note = same.Support.Reason };
                        }),
                    }),
                });
                continue;
            }

            await output.WriteLineAsync($"{source} ({(first.Count > 0 ? first[0].SourceFormat : Strings.Probe_NoTracks)})");
            foreach (var t in first)
            {
                var name = t.Name.Length > 0 ? $" \"{t.Name}\"" : string.Empty;
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  {t.TrackId,3}  {t.Kind,-9} {t.Format,-12} {t.Language,-6} {t.Details}{name}"));
                foreach (var target in targets)
                {
                    var same = byTarget[target].First(x => x.TrackId == t.TrackId);
                    var actions = string.Join(' ', same.Choices.Select(c => (ReferenceEquals(c, same.Choice) || c == same.Choice ? "*" : string.Empty) + Name(c)));
                    await output.WriteLineAsync($"         {(target == ContainerKind.Mp4 ? "MP4" : "MKV")}: {actions}");
                }
            }
        }

        if (a.Has("json"))
            await output.WriteLineAsync(JsonSerializer.Serialize(result, s_json));
        else
            await output.WriteLineAsync(Strings.Probe_Legend);
        return 0;
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// mmw import &lt;file&gt; &lt;source[:tracks]&gt;... [--track [n:]ids] [--only kind] [--action [[n:]id=]name]
    /// [--language [[n:]id=]lang] [--name [n:]id=name] [--forced/--default/--enabled [n:]id=bool]
    /// [--ocr-language [[n:]id=]lang] [--frame-rate [[n:]id=]fps] [--duplicate [n:]id=name] [--dry-run] [--output path] [--optimize]
    /// </summary>
    public static async Task<int> ImportAsync(Arguments a, TextWriter output, ContainerRegistry registry)
    {
        if (a.Positional.Count < 2)
            throw new UsageException(Strings.Error_ImportUsage);
        MediaRemux.EnsureRegistered();
        var doc = await registry.OpenAsync(Existing(a.Positional[0]));
        var only = a.Value("only")?.ToLowerInvariant();
        // --track "1,3-5" (every source) or "2:1,3" (source 2).
        var picks = a.Values("track").Select(v => v.Contains(':', StringComparison.Ordinal)
            ? (Source: (int?)int.Parse(v[..v.IndexOf(':')], CultureInfo.InvariantCulture), Ids: Ids(v[(v.IndexOf(':') + 1)..]))
            : (Source: null, Ids: Ids(v))).ToList();
        var actions = Assignments(a, "action");
        var languages = Assignments(a, "language");
        var names = Assignments(a, "name");
        var forced = Assignments(a, "forced");
        var defaults = Assignments(a, "default");
        var enabled = Assignments(a, "enabled");
        var ocrLanguages = Assignments(a, "ocr-language");
        var frameRates = Assignments(a, "frame-rate");
        var duplicates = Assignments(a, "duplicate");
        var added = 0;
        var plan = new List<string>();

        for (var n = 1; n < a.Positional.Count; n++)
        {
            var (path, specIds) = SourceSpec(a.Positional[n]);
            var file = Path.GetFileName(path);
            var tracks = await TrackImporter.InspectAsync(path, doc.Container);

            // Explicit picks: "source:ids", or --track with ids for this source (or for every source).
            var ids = specIds;
            foreach (var pick in picks.Where(p => p.Source is null || p.Source == n))
                (ids ??= []).UnionWith(pick.Ids);

            List<ImportableTrack> selected;
            if (ids is not null)
            {
                var missing = ids.Where(id => tracks.All(t => t.TrackId != id)).ToList();
                if (missing.Count > 0)
                    throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoSuchTracks, file, string.Join(", ", missing), string.Join(", ", tracks.Select(t => $"{t.TrackId} ({t.Kind} {t.Format})"))));
                selected = tracks.Where(t => ids.Contains(t.TrackId)).ToList();
            }
            else
            {
                selected = tracks.Where(t => t.Action != ImportAction.Skip && (only is null || t.Kind.ToString().Equals(only, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            foreach (var t in selected)
            {
                var what = string.Format(CultureInfo.CurrentCulture, Strings.Track_OfFile, file, t.TrackId, t.Format);
                // An action for this track must be offered; one for every track applies where it is.
                var specific = actions.Where(x => x.Track is not null && x.Matches(n, t.TrackId)).OrderByDescending(x => x.Rank).FirstOrDefault();
                if (specific is not null)
                    t.Choice = Resolve(t.Choices, specific.Value, t.Config.Channels, what);
                else if (actions.LastOrDefault(x => x.Track is null && x.Matches(n, t.TrackId)) is { } general)
                {
                    try
                    {
                        t.Choice = Resolve(t.Choices, general.Value, t.Config.Channels, what);
                    }
                    catch (UsageException)
                    {
                        // not offered for this track: it keeps its recommended action
                    }
                }
                if (t.Action == ImportAction.Skip)
                    throw new UsageException(t.Support.Reason is { } r
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Error_CannotStoreReason, what, doc.Container, r, string.Join(", ", t.Choices.Select(Name)))
                        : string.Format(CultureInfo.CurrentCulture, Strings.Error_CannotStore, what, doc.Container, string.Join(", ", t.Choices.Select(Name))));
                if (Pick(languages, n, t.TrackId) is { } lang)
                    t.Language = LanguageTable.ToBcp47(lang);
                if (Pick(names, n, t.TrackId) is { } name)
                    t.Name = name;
                if (Pick(ocrLanguages, n, t.TrackId) is { } ocr && t.Ocr is not null)
                    t.Ocr = t.Ocr with { Language = ocr };
                if (t.RequiresFrameRate)
                {
                    var rate = Pick(frameRates, n, t.TrackId) ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NeedsFrameRate, file));
                    t.FrameRate = double.Parse(rate, CultureInfo.InvariantCulture);
                }

                t.Selected = true;
                plan.Add(string.Format(CultureInfo.CurrentCulture, Strings.Plan_Import, file, t.TrackId, t.Kind, t.Format, t.Language, Name(t.Choice!)));
            }

            foreach (var skipped in tracks.Except(selected).Where(t => ids is null && t.Support.Reason is not null))
                await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Import_Skipping, file, skipped.TrackId, skipped.Support.Reason));
            if (a.Has("dry-run"))
                continue;

            var tracksAdded = TrackImporter.AddToDocument(doc, selected);
            added += tracksAdded.Count;
            foreach (var t in selected)
            {
                var track = tracksAdded.FirstOrDefault(x => x.Source?.TrackId == t.TrackId && x.Source.Import?.Action == t.Action) ??
                            tracksAdded.FirstOrDefault(x => x.Source?.TrackId == t.TrackId);
                if (track is null)
                    continue;
                ApplyFlags(track, forced, defaults, enabled, n, t.TrackId);
                if (Pick(duplicates, n, t.TrackId) is { } duplicate)
                    added += Duplicate(doc, track, t, duplicate, plan, file);
            }
        }

        foreach (var line in plan)
            await output.WriteLineAsync(line);
        if (a.Has("dry-run"))
            return 0;
        if (added == 0)
        {
            await output.WriteLineAsync(Strings.Import_Nothing);
            return 1;
        }

        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = a.Value("output"), Optimize = a.Has("optimize") });
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Import_Done, added, a.Value("output") ?? doc.Path));
        return 0;
    }

    private static void ApplyFlags(Track track, List<Assignment> forced, List<Assignment> defaults, List<Assignment> enabled, int source, uint id)
    {
        if (Pick(forced, source, id) is { } f)
            track.IsForced = Bool(f, "forced");
        if (Pick(defaults, source, id) is { } d)
            track.IsDefault = Bool(d, "default");
        if (Pick(enabled, source, id) is { } e)
            track.Enabled = Bool(e, "enabled");
    }

    /// <summary>Adds a copy of a subtitle track with another action; returns the number of tracks added.</summary>
    private static int Duplicate(MediaDocument doc, Track track, ImportableTrack inspected, string actionName, List<string> plan, string file)
    {
        if (track is not SubtitleTrack subtitle || !TrackImporter.CanDuplicate(subtitle))
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NotSubtitle, string.Format(CultureInfo.CurrentCulture, Strings.Track_OfFile, file, inspected.TrackId, inspected.Format)));
        var choice = Resolve(inspected.Choices, actionName, 0, string.Format(CultureInfo.CurrentCulture, Strings.Track_OfFile, file, inspected.TrackId, inspected.Format));
        var copy = TrackImporter.Duplicate(doc, subtitle, inspected);
        TrackConversions.SetAction(doc, copy, choice.Action, ocr: choice.OcrFrom(inspected.Ocr));
        copy.Enabled = true;
        plan.Add(string.Format(CultureInfo.CurrentCulture, Strings.Plan_Duplicated, file, inspected.TrackId, Name(choice)));
        return 1;
    }

    // ------------------------------------------------------------------ remux

    /// <summary>
    /// mmw remux &lt;file&gt; &lt;output&gt; [--track ids] [--action [id=]name] [--language/--name id=…] [--forced/--default/--enabled id=bool]
    /// [--duplicate id=name] [--drop-unsupported] [--dry-run] [--optimize]
    /// </summary>
    public static async Task<int> RemuxAsync(Arguments a, TextWriter output, ContainerRegistry registry)
    {
        if (a.Positional.Count != 2)
            throw new UsageException(Strings.Error_RemuxUsage);
        MediaRemux.EnsureRegistered();
        var doc = await registry.OpenAsync(Existing(a.Positional[0]));
        var target = ContainerKinds.FromPath(a.Positional[1]);
        if (target == ContainerKind.Unknown)
            throw new UsageException(Strings.Error_OutputExtension);
        var plan = new List<string>();
        await ApplyTrackOptionsAsync(doc, a, target, plan, dropUnsupported: a.Has("drop-unsupported"), keepOnlyPicked: true);

        var checks = await Remuxer.CheckAsync(doc, target);
        foreach (var (track, support) in checks.Where(c => c.Support.Level == TrackSupportLevel.Passthrough && c.Support.Reason is not null))
            await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Remux_Warning, track.Id, track.Format, support.Reason));
        foreach (var (track, support) in checks.Where(p => p.Support.Level is TrackSupportLevel.NeedsConversion or TrackSupportLevel.Unsupported))
        {
            plan.Add(string.Format(CultureInfo.CurrentCulture, Strings.Plan_Dropping, track.Id, track.Format, support.Reason));
            track.Source = track.Source! with { Import = new TrackImportOptions { Action = ImportAction.Skip } };
        }

        foreach (var line in plan)
            await output.WriteLineAsync(line);
        if (a.Has("dry-run"))
            return 0;
        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = Path.GetFullPath(a.Positional[1]), Optimize = a.Has("optimize") });
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Output_Wrote, a.Positional[1]));
        return 0;
    }

    /// <summary>
    /// The per-track options of <c>remux</c> and <c>tracks</c> on the tracks of an opened document: which to keep,
    /// their actions (the recommended one for a track the target cannot store as it is, unless
    /// <paramref name="dropUnsupported"/>), names, languages, flags and duplicates.
    /// </summary>
    public static async Task ApplyTrackOptionsAsync(MediaDocument doc, Arguments a, ContainerKind target, List<string> plan, bool dropUnsupported, bool keepOnlyPicked)
    {
        var picks = keepOnlyPicked ? a.Values("track").SelectMany(v => Ids(v)).ToHashSet() : [];
        var actions = Assignments(a, "action");
        if (actions.Any(x => x.Track is null))
            throw new UsageException(Strings.Error_ActionNeedsTrack);
        var duplicates = Assignments(a, "duplicate");
        var forced = Assignments(a, "forced");
        var defaults = Assignments(a, "default");
        var enabled = Assignments(a, "enabled");
        var perTrackName = Assignments(a, "name").Where(x => x.Track is not null).ToList();
        var perTrackLanguage = Assignments(a, "language").Where(x => x.Track is not null).ToList();
        var needsChoices = actions.Count > 0 || duplicates.Count > 0 || !dropUnsupported && keepOnlyPicked;
        var inspected = needsChoices && doc.Path is { } path
            ? (await TrackImporter.InspectAsync(path, target)).ToDictionary(t => t.TrackId)
            : [];
        var support = keepOnlyPicked ? (await Remuxer.CheckAsync(doc, target)).ToDictionary(c => c.Track, c => c.Support) : [];

        foreach (var track in doc.Tracks.Where(t => t is not ChapterTrack).ToList())
        {
            if (track.Source is null)
                continue;
            var what = string.Format(CultureInfo.CurrentCulture, Strings.Track_Short, track.Id, track.Format);
            if (picks.Count > 0 && !picks.Contains(track.Id))
            {
                plan.Add(string.Format(CultureInfo.CurrentCulture, Strings.Plan_LeavingOut, what));
                track.Source = track.Source with { Import = (track.Source.Import ?? new TrackImportOptions()) with { Action = ImportAction.Skip } };
                continue;
            }

            if (Pick(perTrackName, 1, track.Id) is { } name)
                track.Name = name;
            if (Pick(perTrackLanguage, 1, track.Id) is { } lang)
                track.Language = LanguageTable.ToBcp47(lang);
            ApplyFlags(track, forced, defaults, enabled, 1, track.Id);

            var info = inspected.GetValueOrDefault(track.Id);
            ImportChoice? choice = null;
            if (Pick(actions, 1, track.Id) is { } actionName)
                choice = Resolve(info?.Choices ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoActionsKnown, what)), actionName, info.Config.Channels, what);
            else if (!dropUnsupported && support.TryGetValue(track, out var s) && s.Level is TrackSupportLevel.NeedsConversion or TrackSupportLevel.Unsupported &&
                     info?.Choice is { Action: not ImportAction.Skip } recommended)
                choice = recommended; // what the editor preselects for a track the target cannot hold as it is

            if (choice is null && support.TryGetValue(track, out var converted) && converted.Level == TrackSupportLevel.Converted)
                plan.Add($"{what} → {Name(new ImportChoice(converted.SuggestedAction, string.Empty))} ({converted.Reason})");
            if (choice is not null && choice.Action != (track.Source.Import?.Action ?? ImportAction.Passthrough))
            {
                TrackConversions.SetAction(doc, track, choice.Action, choice.SettingsFrom(ConversionDefaults.Settings), choice.OcrFrom(null));
                plan.Add($"{what} → {Name(choice)}");
            }

            if (Pick(duplicates, 1, track.Id) is { } duplicate && info is not null)
                Duplicate(doc, track, info, duplicate, plan, Path.GetFileName(doc.Path ?? string.Empty));
        }
    }

    // ------------------------------------------------------------------ tracks

    /// <summary>
    /// mmw tracks with conversions: <c>--action id=name</c> / <c>--duplicate id=name</c> on tracks already in the file
    /// (the file is remuxed on save), next to the editing options <paramref name="edit"/> applies.
    /// </summary>
    public static async Task<int> TracksAsync(Arguments a, TextWriter output, ContainerRegistry registry, Action<MediaDocument, Arguments> edit)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException(Strings.Error_MissingFile);
        if (!File.Exists(file))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, file), file);
        MediaRemux.EnsureRegistered();
        var doc = await registry.OpenAsync(file);
        edit(doc, a);
        var plan = new List<string>();
        await ApplyTrackOptionsAsync(doc, a, doc.Container, plan, dropUnsupported: true, keepOnlyPicked: false);
        foreach (var line in plan)
            await output.WriteLineAsync(line);
        if (a.Has("dry-run"))
            return 0;
        var destination = a.Value("output");
        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = destination, Optimize = a.Has("optimize") });
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Edit_Saved, destination ?? doc.Path));
        return 0;
    }

    // ------------------------------------------------------------------ extract

    /// <summary>
    /// mmw extract &lt;file&gt; &lt;track-ids&gt;... [output] | --all [--output-dir dir]: writes tracks of any readable file
    /// as raw streams ("&lt;file&gt; - &lt;id&gt;&lt;extension&gt;" next to the file unless told otherwise).
    /// </summary>
    public static async Task<int> ExtractAsync(Arguments a, TextWriter output)
    {
        if (a.Positional.Count < 1 || a.Positional.Count == 1 && !a.Has("all"))
            throw new UsageException(Strings.Error_ExtractUsage);
        var file = a.Positional[0];
        if (!File.Exists(file))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, file), file);
        MediaRemux.EnsureRegistered();
        using var demuxer = MediaFormatRegistry.OpenDemuxer(Path.GetFullPath(file));
        var rest = a.Positional.Skip(1).ToList();
        string? single = null;
        if (rest.Count >= 2 && !IdListRegex().IsMatch(rest[^1]))
        {
            single = rest[^1];
            rest.RemoveAt(rest.Count - 1);
        }

        var ids = a.Has("all")
            ? demuxer.Tracks.Where(t => TrackExport.CanExport(t.Config)).Select(t => t.TrackId).ToHashSet()
            : rest.SelectMany(Ids).ToHashSet();
        if (single is not null && ids.Count != 1)
            throw new UsageException(Strings.Error_OutputForOneTrack);
        var directory = a.Value("output-dir") ?? Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(directory);
        foreach (var id in ids.Order())
        {
            var track = demuxer.Tracks.FirstOrDefault(t => t.TrackId == id)
                        ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoTrackWithIdList, id, string.Join(", ", demuxer.Tracks.Select(t => $"{t.TrackId} ({t.Config.FormatName})"))));
            var extension = TrackExport.Extension(track.Config)
                            ?? throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_CannotExportRaw, track.Config.FormatName));
            var target = single ?? Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(file)} - {id}{extension}"));
            await TrackExport.ExportAsync(track, Path.GetFullPath(target));
            track.Reset();
            await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Extract_Wrote, target, track.Config.FormatName));
        }

        return 0;
    }

    [GeneratedRegex(@"^(?:(\d+):)?(\d+)=(.*)$", RegexOptions.Singleline)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(@"^\d+(-\d+)?(,\d+(-\d+)?)*$")]
    private static partial Regex IdListRegex();
}
