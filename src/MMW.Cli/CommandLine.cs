using System.Globalization;
using System.Text.Json;
using MMW.Cli.Resources;
using MMW.Core.Actions;
using MMW.Core.Chapters;
using MMW.Core.Languages;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Matroska;
using MMW.Formats.Mp4;
using MMW.Queue;

namespace MMW.Cli;

/// <summary>The <c>mmw</c> command line: scriptable access to everything the editor does.</summary>
internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string? queuePath = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Strings.Help_Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "info" => await InfoAsync(Arguments.Parse(args[1..]), output, includeTracks: true),
                "tags" => await InfoAsync(Arguments.Parse(args[1..]), output, includeTracks: false),
                "set" => await EditAsync(Arguments.Parse(args[1..]), output, SetTags),
                "clear-tags" => await EditAsync(Arguments.Parse(args[1..]), output, (doc, _) => doc.Metadata.Clear()),
                "artwork" => await EditAsync(Arguments.Parse(args[1..]), output, Artwork),
                "chapters" => await EditAsync(Arguments.Parse(args[1..]), output, Chapters),
                "tracks" => await TrackCommands.TracksAsync(Arguments.Parse(args[1..]), output, Registry(), Tracks),
                "queue" => await QueueAsync(args[1..], output, queuePath ?? DefaultQueuePath),
                "tag-names" => await TagNamesAsync(output),
                "search" => await MediaCommands.SearchAsync(Arguments.Parse(args[1..]), output, Registry()),
                "nfo" => await MediaCommands.NfoAsync(Arguments.Parse(args[1..]), output, Registry()),
                "import" => await TrackCommands.ImportAsync(Arguments.Parse(args[1..]), output, Registry()),
                "remux" => await TrackCommands.RemuxAsync(Arguments.Parse(args[1..]), output, Registry()),
                "extract" => await TrackCommands.ExtractAsync(Arguments.Parse(args[1..]), output),
                "probe" => await TrackCommands.ProbeAsync(Arguments.Parse(args[1..]), output),
                _ => throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownCommand, args[0])),
            };
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.WriteLineAsync(Strings.Error_RunHelp);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException or HttpRequestException)
        {
            await error.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Error_Generic, ex.Message));
            return 1;
        }
    }

    private static string DefaultQueuePath => Path.Combine(Directory.CreateDirectory(MMW.Core.AppDataFolders.Roaming).FullName, "queue.json");

    internal static ContainerRegistry Registry() => new([new Mp4Handler(), new MatroskaHandler()]);

    private static string RequireFile(Arguments a)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException(Strings.Error_MissingFile);
        if (!File.Exists(file))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, file), file);
        return file;
    }

    // ------------------------------------------------------------------ info

    private static async Task<int> InfoAsync(Arguments a, TextWriter output, bool includeTracks)
    {
        var doc = await Registry().OpenAsync(RequireFile(a));
        // What the containers do not say (Dolby Atmos, the DTS product) is read from the bitstream, as the editor does.
        MMW.Media.Remux.MediaRemux.EnsureRegistered();
        await TrackActions.DescribeAudioAsync(doc);
        var tags = doc.Metadata.Keys.ToDictionary(id => TagCatalog.Get(id).Name, id => (object)MetadataSet.FormatValue(id, doc.Metadata[id]!));

        if (a.Has("json"))
        {
            var info = new Dictionary<string, object?>
            {
                ["file"] = doc.Path,
                ["container"] = doc.Container.ToString(),
                ["duration"] = doc.Duration.TotalSeconds,
                ["tags"] = tags,
                ["custom"] = doc.Metadata.CustomItems,
                ["artworks"] = doc.Metadata.Artworks.Count,
            };
            if (includeTracks)
            {
                info["tracks"] = doc.Tracks.Where(t => t is not ChapterTrack).Select(t => new
                {
                    id = t.Id,
                    kind = t.Kind.ToString(),
                    format = t.Format,
                    details = t.FormatDetails,
                    language = t.Language,
                    name = t.Name,
                    enabled = t.Enabled,
                    alternateGroup = t.AlternateGroup,
                    characteristics = t.MediaCharacteristics,
                }).ToList();
                info["chapters"] = doc.Chapters.Select(c => new { start = c.Start.TotalSeconds, title = c.Title }).ToList();
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(info, s_json));
            return 0;
        }

        if (includeTracks)
        {
            await output.WriteLineAsync($"{Path.GetFileName(doc.Path)}  ({doc.Container}, {doc.Duration:hh\\:mm\\:ss})");
            foreach (var t in doc.Tracks.Where(t => t is not ChapterTrack))
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  #{t.Id,-3} {t.Kind,-13} {t.DisplayFormat,-40} {LanguageTable.DisplayName(t.Language),-14} {(t.Enabled ? Strings.Info_TrackOn : Strings.Info_TrackOff),-3} {t.Name}"));
            }

            if (doc.Chapters.Count > 0)
            {
                await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Info_Chapters, doc.Chapters.Count));
                foreach (var c in doc.Chapters)
                    await output.WriteLineAsync($"  {ChapterTime.Format(c.Start)}  {c.Title}");
            }

            await output.WriteLineAsync(Strings.Info_Tags);
        }

        foreach (var (name, value) in tags)
            await output.WriteLineAsync($"  {name}: {value}");
        foreach (var (key, value) in doc.Metadata.CustomItems)
            await output.WriteLineAsync($"  [{key}]: {value}");
        if (doc.Metadata.Artworks.Count > 0)
            await output.WriteLineAsync("  " + string.Format(CultureInfo.CurrentCulture, Strings.Info_Artwork, doc.Metadata.Artworks.Count));
        return 0;
    }

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private static async Task<int> TagNamesAsync(TextWriter output)
    {
        foreach (var d in TagCatalog.All)
            await output.WriteLineAsync($"{d.Name}  ({d.Kind}{(d.Choices is { } c ? ": " + string.Join(", ", c.Select(x => $"{x.Value}={x.Name}")) : string.Empty)})");
        return 0;
    }

    // ------------------------------------------------------------------ editing

    private static async Task<int> EditAsync(Arguments a, TextWriter output, Action<MediaDocument, Arguments> edit)
    {
        var registry = Registry();
        var doc = await registry.OpenAsync(RequireFile(a));
        try
        {
            edit(doc, a);
        }
        catch (NothingToSaveException)
        {
            return 0;
        }

        var handler = registry.Get(doc.Container)!;
        var destination = a.Value("output");
        await handler.SaveAsync(doc, new SaveOptions { OutputPath = destination, Optimize = a.Has("optimize") });
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Edit_Saved, destination ?? doc.Path));
        return 0;
    }

    private static void SetTags(MediaDocument doc, Arguments a)
    {
        var assignments = a.Positional.Skip(1).ToList();
        if (assignments.Count == 0)
            throw new UsageException(Strings.Error_SetNeedsAssignment);
        foreach (var assignment in assignments)
        {
            var eq = assignment.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NotAssignment, assignment));
            var name = assignment[..eq].Trim();
            var value = assignment[(eq + 1)..];
            var def = TagCatalog.All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                                         string.Equals(d.Id.ToString(), name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownTag, name));
            if (def.Choices is { } choices && !int.TryParse(value, out _) && value.Length > 0)
            {
                value = (choices.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.OrdinalIgnoreCase))
                         ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_InvalidChoice, value, def.Name))).Value.ToString(CultureInfo.InvariantCulture);
            }

            doc.Metadata.Set(def.Id, value.Length == 0 ? null : value);
        }
    }

    private static void Artwork(MediaDocument doc, Arguments a)
    {
        if (a.Has("remove-all"))
        {
            doc.Metadata.Artworks.Clear();
        }
        else if (a.Value("export") is { } dir)
        {
            Directory.CreateDirectory(dir);
            var i = 1;
            foreach (var art in doc.Metadata.Artworks)
                File.WriteAllBytes(Path.Combine(dir, $"artwork{i++}{art.Extension}"), art.Data);
            throw new NothingToSaveException();
        }
        else
        {
            var images = a.Values("add");
            if (images.Count == 0)
                throw new UsageException(Strings.Error_ArtworkUsage);
            if (a.Has("replace"))
                doc.Metadata.Artworks.Clear();
            foreach (var path in images)
            {
                var data = File.ReadAllBytes(path);
                if (Core.Metadata.Artwork.Detect(data) == ArtworkFormat.Unknown)
                    throw new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NotAnImage, path));
                doc.Metadata.Artworks.Add(new Core.Metadata.Artwork(data));
            }
        }

        doc.Metadata.NotifyArtworksChanged();
    }

    private static void Chapters(MediaDocument doc, Arguments a)
    {
        if (a.Value("export") is { } export)
        {
            File.WriteAllText(export, ChapterTextFormat.ToOgg(doc.Chapters));
            throw new NothingToSaveException();
        }

        if (a.Value("import") is { } import)
            TrackActions.ReplaceChapters(doc, ChapterTextFormat.Parse(File.ReadAllText(import)));
        else if (a.Value("every") is { } every)
            TrackActions.InsertChaptersEvery(doc, TimeSpan.FromMinutes(double.Parse(every, CultureInfo.InvariantCulture)));
        else if (a.Has("clear"))
            TrackActions.RemoveAll(doc.Chapters);
        else
            throw new UsageException(Strings.Error_ChaptersUsage);
    }

    internal static void Tracks(MediaDocument doc, Arguments a)
    {
        if (a.Value("complete-languages") is { } complete)
            GroupActions.CompleteLanguages(doc, LanguageTable.ToBcp47(complete));
        if (a.Has("organize-groups"))
            GroupActions.OrganizeAlternateGroups(doc);
        if (a.Has("fix-fallbacks"))
            GroupActions.FixAudioFallbacks(doc);
        if (a.Has("clear-names"))
            TrackActions.ClearTrackNames(doc);
        if (a.Has("prettify-audio-names"))
        {
            // Atmos and the DTS product are read from the bitstream first (the console has no synchronisation context).
            TrackActions.DescribeAudioAsync(doc).GetAwaiter().GetResult();
            TrackActions.PrettifyAudioNames(doc);
        }
        if (a.Value("enable-audio") is { } audio && !GroupActions.EnableTrackWithLanguage(doc, TrackKind.Audio, audio))
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoAudioInLanguage, audio));
        if (a.Value("enable-subtitles") is { } subs && !GroupActions.EnableTrackWithLanguage(doc, TrackKind.Subtitle, subs))
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoSubtitleInLanguage, subs));

        if (a.Value("track") is { } idText)
        {
            var id = uint.Parse(idText, CultureInfo.InvariantCulture);
            var track = doc.Tracks.FirstOrDefault(t => t.Id == id && t is not ChapterTrack) ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoTrackWithId, id));
            // "--name 3=…" / "--language 3=…" name their track themselves (applied with the other per-track options).
            if (a.Values("name").LastOrDefault(v => !PerTrack(v)) is { } name)
                track.Name = name;
            if (a.Values("language").LastOrDefault(v => !PerTrack(v)) is { } lang)
                track.Language = LanguageTable.ToBcp47(lang);
            if (a.Value("enabled") is { } enabled)
                track.Enabled = bool.Parse(enabled);
        }
    }

    private static bool PerTrack(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^(\d+:)?\d+=");

    // ------------------------------------------------------------------ queue

    private static async Task<int> QueueAsync(string[] args, TextWriter output, string queuePath)
    {
        if (args.Length == 0)
            throw new UsageException(Strings.Error_QueueUsage);
        var runner = new QueueRunner(Registry());
        QueueStore.Load(runner, queuePath);
        switch (args[0])
        {
            case "add":
                var added = runner.Add(args[1..].Select(Path.GetFullPath).Where(File.Exists));
                QueueStore.Save(runner, queuePath);
                await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Queue_Added, added.Count, runner.Items.Count));
                return 0;
            case "start":
                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                    };
                    await runner.RunAsync(cts.Token);
                }

                QueueStore.Save(runner, queuePath);
                await WriteStatus(runner, output);
                return runner.Items.Any(i => i.Status == QueueItemStatus.Failed) ? 1 : 0;
            case "status":
                await WriteStatus(runner, output);
                return 0;
            case "clear-completed":
                runner.RemoveCompleted();
                QueueStore.Save(runner, queuePath);
                return 0;
            default:
                throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownQueueCommand, args[0]));
        }
    }

    private static async Task WriteStatus(QueueRunner runner, TextWriter output)
    {
        foreach (var item in runner.Items)
            await output.WriteLineAsync($"{item.Status,-10} {item.SourcePath}{(item.Error is { } e ? "  — " + e : string.Empty)}");
    }
}

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Thrown by read-only sub-commands (exports) to skip saving.</summary>
internal sealed class NothingToSaveException() : IOException(Strings.Error_NothingToSave);
