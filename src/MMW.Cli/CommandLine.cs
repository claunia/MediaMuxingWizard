using System.Globalization;
using System.Text.Json;
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
    private const string Usage = """
        Usage: mmw <command> [arguments]

          info <file> [--json]                 Show tracks, tags and chapters
          tags <file> [--json]                 List tags
          set <file> "Tag=value"...            Set tags (empty value removes the tag)
          clear-tags <file>                    Remove all tags and artwork
          artwork <file> --add <image>... [--replace] | --export <dir> | --remove-all
          chapters <file> --import <txt> | --export <txt> | --every <minutes> | --clear
          tracks <file> [--organize-groups] [--fix-fallbacks] [--clear-names] [--prettify-audio-names]
                        [--complete-languages <lang>] [--enable-audio <lang>] [--enable-subtitles <lang>]
                        [--track <id> --name <name> --language <lang> --enabled <true|false>]
          queue add <file>... | queue start | queue status | queue clear-completed
                                               Use the editor's saved queue (and its options)
          search <file> [--title t] [--year y] [--season n] [--episode n] [--provider p] [--language l]
                        [--apply [--result n] [--artwork poster|season|episode|backdrop|none]]
                                               Search online metadata; --apply writes the chosen result
          nfo <file> --import [nfo] | --export [nfo]
                                               Merge tags from a Kodi .nfo, or write one
          import <file> <source>... [--language l] [--frame-rate fps] [--only video|audio|subtitle]
                                               Add tracks from other files (remuxes on save)
          remux <file> <output>                Rewrite as MP4 or Matroska (by extension), no re-encoding
          tag-names                            List the tag names accepted by "set"

        Every editing command saves the file in place, or to --output <path> when given.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string? queuePath = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Usage);
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
                "tracks" => await EditAsync(Arguments.Parse(args[1..]), output, Tracks),
                "queue" => await QueueAsync(args[1..], output, queuePath ?? DefaultQueuePath),
                "tag-names" => await TagNamesAsync(output),
                "search" => await MediaCommands.SearchAsync(Arguments.Parse(args[1..]), output, Registry()),
                "nfo" => await MediaCommands.NfoAsync(Arguments.Parse(args[1..]), output, Registry()),
                "import" => await MediaCommands.ImportAsync(Arguments.Parse(args[1..]), output, Registry()),
                "remux" => await MediaCommands.RemuxAsync(Arguments.Parse(args[1..]), output, Registry()),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.WriteLineAsync("Run 'mmw --help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FormatException or HttpRequestException)
        {
            await error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string DefaultQueuePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "MediaMetadataWizard", "queue.json");

    internal static ContainerRegistry Registry() => new([new Mp4Handler(), new MatroskaHandler()]);

    private static string RequireFile(Arguments a)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException("Missing file argument.");
        if (!File.Exists(file))
            throw new FileNotFoundException($"'{file}' does not exist.", file);
        return file;
    }

    // ------------------------------------------------------------------ info

    private static async Task<int> InfoAsync(Arguments a, TextWriter output, bool includeTracks)
    {
        var doc = await Registry().OpenAsync(RequireFile(a));
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
                    $"  #{t.Id,-3} {t.Kind,-13} {t.DisplayFormat,-40} {LanguageTable.DisplayName(t.Language),-14} {(t.Enabled ? "on " : "off")} {t.Name}"));
            }

            if (doc.Chapters.Count > 0)
            {
                await output.WriteLineAsync($"Chapters ({doc.Chapters.Count}):");
                foreach (var c in doc.Chapters)
                    await output.WriteLineAsync($"  {ChapterTime.Format(c.Start)}  {c.Title}");
            }

            await output.WriteLineAsync("Tags:");
        }

        foreach (var (name, value) in tags)
            await output.WriteLineAsync($"  {name}: {value}");
        foreach (var (key, value) in doc.Metadata.CustomItems)
            await output.WriteLineAsync($"  [{key}]: {value}");
        if (doc.Metadata.Artworks.Count > 0)
            await output.WriteLineAsync($"  Artwork: {doc.Metadata.Artworks.Count} image(s)");
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
        await output.WriteLineAsync($"Saved {destination ?? doc.Path}.");
        return 0;
    }

    private static void SetTags(MediaDocument doc, Arguments a)
    {
        var assignments = a.Positional.Skip(1).ToList();
        if (assignments.Count == 0)
            throw new UsageException("Give at least one \"Tag=value\" assignment.");
        foreach (var assignment in assignments)
        {
            var eq = assignment.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                throw new UsageException($"'{assignment}' is not in Tag=value form.");
            var name = assignment[..eq].Trim();
            var value = assignment[(eq + 1)..];
            var def = TagCatalog.All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                                         string.Equals(d.Id.ToString(), name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new UsageException($"Unknown tag '{name}'. Run 'mmw tag-names' for the list.");
            if (def.Choices is { } choices && !int.TryParse(value, out _) && value.Length > 0)
            {
                value = (choices.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.OrdinalIgnoreCase))
                         ?? throw new UsageException($"'{value}' is not a valid {def.Name}.")).Value.ToString(CultureInfo.InvariantCulture);
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
                throw new UsageException("Use --add <image>, --export <dir> or --remove-all.");
            if (a.Has("replace"))
                doc.Metadata.Artworks.Clear();
            foreach (var path in images)
            {
                var data = File.ReadAllBytes(path);
                if (Core.Metadata.Artwork.Detect(data) == ArtworkFormat.Unknown)
                    throw new FormatException($"'{path}' is not a JPEG, PNG, BMP or GIF image.");
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
            throw new UsageException("Use --import <txt>, --export <txt>, --every <minutes> or --clear.");
    }

    private static void Tracks(MediaDocument doc, Arguments a)
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
            TrackActions.PrettifyAudioNames(doc);
        if (a.Value("enable-audio") is { } audio && !GroupActions.EnableTrackWithLanguage(doc, TrackKind.Audio, audio))
            throw new UsageException($"No audio track in '{audio}'.");
        if (a.Value("enable-subtitles") is { } subs && !GroupActions.EnableTrackWithLanguage(doc, TrackKind.Subtitle, subs))
            throw new UsageException($"No subtitle track in '{subs}'.");

        if (a.Value("track") is { } idText)
        {
            var id = uint.Parse(idText, CultureInfo.InvariantCulture);
            var track = doc.Tracks.FirstOrDefault(t => t.Id == id && t is not ChapterTrack) ?? throw new UsageException($"No track with id {id}.");
            if (a.Value("name") is { } name)
                track.Name = name;
            if (a.Value("language") is { } lang)
                track.Language = LanguageTable.ToBcp47(lang);
            if (a.Value("enabled") is { } enabled)
                track.Enabled = bool.Parse(enabled);
        }
    }

    // ------------------------------------------------------------------ queue

    private static async Task<int> QueueAsync(string[] args, TextWriter output, string queuePath)
    {
        if (args.Length == 0)
            throw new UsageException("Use 'queue add <file>...', 'queue start', 'queue status' or 'queue clear-completed'.");
        var runner = new QueueRunner(Registry());
        QueueStore.Load(runner, queuePath);
        switch (args[0])
        {
            case "add":
                var added = runner.Add(args[1..].Select(Path.GetFullPath).Where(File.Exists));
                QueueStore.Save(runner, queuePath);
                await output.WriteLineAsync($"Added {added.Count} item(s); {runner.Items.Count} in the queue.");
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
                throw new UsageException($"Unknown queue command '{args[0]}'.");
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
internal sealed class NothingToSaveException() : IOException("Nothing to save.");
