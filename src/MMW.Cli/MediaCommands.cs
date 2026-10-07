using System.Globalization;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.Metadata;
using MMW.Metadata.Artwork;
using MMW.Metadata.Mapping;
using MMW.Metadata.Nfo;
using MMW.Metadata.Search;

namespace MMW.Cli;

/// <summary>Commands that use metadata providers, NFO files and the remuxer.</summary>
internal static class MediaCommands
{
    /// <summary>
    /// mmw search &lt;file&gt; [--provider name] [--language code] [--title t] [--season n] [--episode n] [--year y]
    /// [--apply [--result n] [--artwork poster|season|episode|backdrop|none]] [--json]
    /// </summary>
    public static async Task<int> SearchAsync(Arguments a, TextWriter output, ContainerRegistry registry, MetadataProviderRegistry? providers = null)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException("Missing file argument.");
        var doc = await registry.OpenAsync(file);
        var prefill = SearchPrefill.From(doc);
        var kind = a.Value("season") is not null || a.Value("episode") is not null ? MediaSearchKind.TvEpisode : prefill.Kind;
        var title = a.Value("title") ?? prefill.Title;
        if (string.IsNullOrWhiteSpace(title))
            throw new UsageException("Could not guess a title; pass --title.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        providers ??= MetadataProviderRegistry.CreateDefault(http, new ProviderSettings());
        var provider = (a.Value("provider") is { } name ? providers.Find(name) : providers.DefaultFor(kind))
                       ?? throw new UsageException($"Unknown provider. Available: {string.Join(", ", providers.Providers.Select(p => p.Name))}.");
        if (!provider.Supports(kind))
            throw new UsageException($"{provider.Name} does not support {kind}.");
        if (!provider.IsConfigured)
            throw new UsageException($"{provider.Name} needs an API key (appsettings.json or MMW_TMDB_API_KEY / MMW_TVDB_API_KEY).");

        var language = a.Value("language") ?? provider.DefaultLanguage;
        var query = new SearchQuery(kind, title, ParseInt(a.Value("year")) ?? (kind == MediaSearchKind.Movie ? prefill.Year : null),
            ParseInt(a.Value("season")) ?? prefill.Season, ParseInt(a.Value("episode")) ?? prefill.Episode, language);
        var results = await provider.SearchAsync(query);
        if (results.Count == 0)
        {
            await output.WriteLineAsync($"No results from {provider.Name} for {query}.");
            return 1;
        }

        if (!a.Has("apply"))
        {
            for (var i = 0; i < results.Count; i++)
                await output.WriteLineAsync($"{i + 1,3}. {results[i].DisplayTitle}  {results[i].Subtitle}");
            return 0;
        }

        var index = (ParseInt(a.Value("result")) ?? 1) - 1;
        if (index < 0 || index >= results.Count)
            throw new UsageException($"--result must be between 1 and {results.Count}.");
        var best = await provider.LoadDetailsAsync(results[index], language);
        var set = MetadataMap.CreateDefault(best.Kind).Apply(best);

        var artworkKind = (a.Value("artwork") ?? "poster").ToLowerInvariant();
        if (artworkKind != "none")
        {
            var wanted = Enum.TryParse<ArtworkKind>(artworkKind, ignoreCase: true, out var k) ? k : ArtworkKind.Poster;
            var remote = best.Artworks.FirstOrDefault(r => r.Kind == wanted) ?? best.Artworks.FirstOrDefault();
            if (remote is not null && await new ArtworkDownloader(http).TryDownloadAsync(remote.FullUrl) is { } image)
                set.Artworks.Add(image);
        }

        MetadataApplier.Apply(doc, set, new ApplyOptions { ReplaceArtworks = set.Artworks.Count > 0, MappedTags = MetadataMap.CreateDefault(best.Kind).MappedTags.ToList() });
        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = a.Value("output") });
        await output.WriteLineAsync($"Applied \"{best.DisplayTitle}\" from {provider.Name} to {a.Value("output") ?? doc.Path}.");
        return 0;
    }

    /// <summary>mmw nfo &lt;file&gt; --import [nfo] | --export [nfo]</summary>
    public static async Task<int> NfoAsync(Arguments a, TextWriter output, ContainerRegistry registry)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException("Missing file argument.");
        var doc = await registry.OpenAsync(file);
        if (a.Value("export") is not null || a.Has("export"))
        {
            var path = a.Value("export") ?? NfoMetadata.NfoPathFor(file);
            await File.WriteAllTextAsync(path, NfoMetadata.Export(doc.Metadata));
            await output.WriteLineAsync($"Wrote {path}.");
            return 0;
        }

        if (a.Value("import") is null && !a.Has("import"))
            throw new UsageException("Use --import [nfo] or --export [nfo].");
        var nfo = a.Value("import") is { } explicitPath
            ? NfoMetadata.Read(explicitPath)
            : NfoMetadata.ImportForMedia(file) ?? throw new FileNotFoundException("No .nfo file found next to the media file.");
        doc.Metadata.Merge(nfo, overwrite: true, replaceArtworks: false);
        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = a.Value("output") });
        await output.WriteLineAsync($"Imported tags into {a.Value("output") ?? doc.Path}.");
        return 0;
    }




    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
