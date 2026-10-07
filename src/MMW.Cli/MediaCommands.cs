using System.Globalization;
using MMW.Cli.Resources;
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
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException(Strings.Error_MissingFile);
        if (!File.Exists(file))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, file), file);
        var doc = await registry.OpenAsync(file);
        var prefill = SearchPrefill.From(doc);
        var kind = a.Value("season") is not null || a.Value("episode") is not null ? MediaSearchKind.TvEpisode : prefill.Kind;
        var title = a.Value("title") ?? prefill.Title;
        if (string.IsNullOrWhiteSpace(title))
            throw new UsageException(Strings.Error_NoTitle);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        providers ??= MetadataProviderRegistry.CreateDefault(http, new ProviderSettings());
        var provider = (a.Value("provider") is { } name ? providers.Find(name) : providers.DefaultFor(kind))
                       ?? throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_UnknownProvider, string.Join(", ", providers.Providers.Select(p => p.Name))));
        if (!provider.Supports(kind))
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ProviderUnsupported, provider.Name, kind));
        if (!provider.IsConfigured)
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ProviderNeedsKey, provider.Name));

        var language = a.Value("language") ?? provider.DefaultLanguage;
        var query = new SearchQuery(kind, title, ParseInt(a.Value("year")) ?? (kind == MediaSearchKind.Movie ? prefill.Year : null),
            ParseInt(a.Value("season")) ?? prefill.Season, ParseInt(a.Value("episode")) ?? prefill.Episode, language);
        var results = await provider.SearchAsync(query);
        if (results.Count == 0)
        {
            await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Search_NoResults, provider.Name, query));
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
            throw new UsageException(string.Format(CultureInfo.CurrentCulture, Strings.Error_ResultRange, results.Count));
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
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Search_Applied, best.DisplayTitle, provider.Name, a.Value("output") ?? doc.Path));
        return 0;
    }

    /// <summary>mmw nfo &lt;file&gt; --import [nfo] | --export [nfo]</summary>
    public static async Task<int> NfoAsync(Arguments a, TextWriter output, ContainerRegistry registry)
    {
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException(Strings.Error_MissingFile);
        if (!File.Exists(file))
            throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Error_FileNotFound, file), file);
        var doc = await registry.OpenAsync(file);
        if (a.Value("export") is not null || a.Has("export"))
        {
            var path = a.Value("export") ?? NfoMetadata.NfoPathFor(file);
            await File.WriteAllTextAsync(path, NfoMetadata.Export(doc.Metadata));
            await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Output_Wrote, path));
            return 0;
        }

        if (a.Value("import") is null && !a.Has("import"))
            throw new UsageException(Strings.Error_NfoUsage);
        var nfo = a.Value("import") is { } explicitPath
            ? NfoMetadata.Read(explicitPath)
            : NfoMetadata.ImportForMedia(file) ?? throw new FileNotFoundException(Strings.Error_NoNfo);
        doc.Metadata.Merge(nfo, overwrite: true, replaceArtworks: false);
        await registry.Get(doc.Container)!.SaveAsync(doc, new SaveOptions { OutputPath = a.Value("output") });
        await output.WriteLineAsync(string.Format(CultureInfo.CurrentCulture, Strings.Nfo_Imported, a.Value("output") ?? doc.Path));
        return 0;
    }




    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
