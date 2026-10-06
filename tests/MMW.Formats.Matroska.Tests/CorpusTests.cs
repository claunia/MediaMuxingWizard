using System.Text;
using System.Text.Json;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Matroska.Tests;

/// <summary>
/// Opt-in tests against a corpus of real-world files. Set <c>MMW_CORPUS</c> to the corpus directory to run them.
/// The corpus is only ever read; write tests work on temporary copies of small files.
/// </summary>
public sealed class CorpusTests
{
    private const long MaxCopySize = 50L * 1024 * 1024;
    private const int EditedFiles = 12;

    private static readonly string[] s_extensions = [".mkv", ".mka", ".mks", ".mk3d", ".webm"];
    private static readonly MatroskaHandler s_handler = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<string> CorpusFiles()
    {
        var dir = Environment.GetEnvironmentVariable("MMW_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            Assert.Skip("Set MMW_CORPUS to a directory of Matroska files to run the corpus tests.");
        MkvFixtures.RequireTools("mkvmerge");

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => s_extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal)) // macOS AppleDouble files
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task EveryFile_ReadsLikeMkvmerge()
    {
        var files = CorpusFiles();
        var failures = new List<string>();
        var log = new StringBuilder();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var doc = await s_handler.ReadAsync(file, Ct);
                var id = MkvFixtures.Identify(file);
                var problems = Compare(doc, id);
                var state = (MatroskaLayout)doc.ContainerState!;
                log.AppendLine($"{name}: {doc.Tracks.Count} tracks [{string.Join(", ", doc.Tracks.Select(t => $"{t.Format} {t.Language}"))}], " +
                               $"{doc.Metadata.Count} tags, {doc.Metadata.CustomItems.Count} custom, {doc.Chapters.Count} chapters, " +
                               $"{doc.Metadata.Artworks.Count} artwork{(state.ScanProblem is null ? string.Empty : ", scan: " + state.ScanProblem)}" +
                               (doc.MainVideo is { } v ? $", video {v.FormatDetails} colour {v.Color}{(v.Hdr is null ? string.Empty : " HDR")}{(v.DolbyVision is null ? string.Empty : " DV " + v.DolbyVision)}" : string.Empty));
                failures.AddRange(problems.Select(p => $"{name}: {p}"));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or InvalidOperationException)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(log.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Count} files differ:\n" + string.Join('\n', failures));
    }

    private static List<string> Compare(MediaDocument doc, JsonElement id)
    {
        var problems = new List<string>();
        if (!id.GetProperty("container").GetProperty("recognized").GetBoolean())
            return problems;

        var tracks = id.GetProperty("tracks").EnumerateArray().ToList();
        if (tracks.Count != doc.Tracks.Count)
        {
            problems.Add($"{doc.Tracks.Count} tracks, mkvmerge sees {tracks.Count}");
            return problems;
        }

        foreach (var t in tracks)
        {
            var p = t.GetProperty("properties");
            var number = p.GetProperty("number").GetUInt32();
            var track = doc.Tracks.FirstOrDefault(x => x.Id == number);
            if (track is null)
            {
                problems.Add($"track {number} missing");
                continue;
            }

            var kind = t.GetProperty("type").GetString() switch
            {
                "video" => TrackKind.Video,
                "audio" => TrackKind.Audio,
                "subtitles" => TrackKind.Subtitle,
                _ => TrackKind.Other,
            };
            if (kind != track.Kind)
                problems.Add($"track {number} is {track.Kind}, mkvmerge says {kind}");
            if (p.TryGetProperty("codec_id", out var codec) && codec.GetString() != track.CodecId)
                problems.Add($"track {number} codec {track.CodecId}, mkvmerge says {codec.GetString()}");
            if (p.TryGetProperty("language_ietf", out var lang) && !string.Equals(lang.GetString(), track.Language, StringComparison.OrdinalIgnoreCase))
                problems.Add($"track {number} language {track.Language}, mkvmerge says {lang.GetString()}");
            if ((p.TryGetProperty("track_name", out var tn) ? tn.GetString() : string.Empty) != track.Name)
                problems.Add($"track {number} name '{track.Name}', mkvmerge says '{tn}'");
        }

        return problems;
    }

    [Fact]
    public async Task SmallFiles_EditInPlace_KeepMediaIntact()
    {
        MkvFixtures.RequireTools("ffmpeg");
        var files = CorpusFiles()
            .Select(f => new FileInfo(f))
            .Where(f => f.Length <= MaxCopySize)
            .OrderBy(f => f.Length)
            .Take(EditedFiles)
            .ToList();
        var failures = new List<string>();
        var log = new StringBuilder();
        var cover = await File.ReadAllBytesAsync(MkvFixtures.Cover(), Ct);

        foreach (var file in files)
        {
            var copy = Fixtures.CopyToTemp(file.FullName);
            try
            {
                var before = MkvFixtures.FrameMd5(copy);
                var warningsBefore = Warnings(copy);
                var doc = await s_handler.ReadAsync(copy, Ct);

                doc.Metadata.Set(TagId.Name, "Corpus edit of " + file.Name);
                doc.Metadata.Set(TagId.Genre, "Test");
                doc.Metadata.Set(TagId.Cast, (string[])["One", "Two"]);
                doc.Metadata.CustomItems["50/MMW_CORPUS_TEST"] = "yes";
                doc.Chapters.Clear();
                doc.Chapters.Add(new Chapter(TimeSpan.Zero, "Start"));
                doc.Chapters.Add(new Chapter(TimeSpan.FromSeconds(1), "One second"));
                foreach (var track in doc.Tracks)
                    track.Name = $"Edited {track.Kind} {track.Id}";
                if (doc.TracksOf<AudioTrack>().FirstOrDefault() is { } audio)
                    audio.Language = "it";
                var artworks = doc.Metadata.Artworks.Count + 1;
                doc.Metadata.Artworks.Add(new Artwork(cover));
                doc.Metadata.NotifyArtworksChanged();

                var size = file.Length;
                await s_handler.SaveAsync(doc, new SaveOptions(), null, Ct);

                var id = MkvFixtures.Identify(copy);
                var errors = id.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
                var warningsAfter = Warnings(copy);
                if (errors.Count > 0)
                    failures.Add($"{file.Name}: mkvmerge errors: {string.Join("; ", errors)}");
                if (!warningsAfter.SetEquals(warningsBefore))
                    failures.Add($"{file.Name}: mkvmerge warnings changed: {string.Join("; ", warningsAfter)}");
                if (MkvFixtures.FrameMd5(copy) != before)
                    failures.Add($"{file.Name}: packet hashes changed");

                var reread = await s_handler.ReadAsync(copy, Ct);
                if (reread.Metadata.GetString(TagId.Name) != doc.Metadata.GetString(TagId.Name) ||
                    !reread.Metadata.GetList(TagId.Cast).SequenceEqual((string[])["One", "Two"]) ||
                    reread.Metadata.CustomItems.GetValueOrDefault("50/MMW_CORPUS_TEST") != "yes")
                {
                    failures.Add($"{file.Name}: tags did not round-trip");
                }

                if (reread.Metadata.Artworks.Count != artworks)
                    failures.Add($"{file.Name}: artwork did not round-trip");
                if (reread.Chapters.Count != 2 || reread.Tracks.Select(t => t.Name).SequenceEqual(doc.Tracks.Select(t => t.Name)) is false)
                    failures.Add($"{file.Name}: chapters or track names did not round-trip");

                log.AppendLine($"{file.Name}: OK, size {size} -> {new FileInfo(copy).Length}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or InvalidOperationException)
            {
                failures.Add($"{file.Name}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                File.Delete(copy);
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(log.ToString());
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static HashSet<string> Warnings(string path) =>
        MkvFixtures.Identify(path).GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? string.Empty).ToHashSet();
}
