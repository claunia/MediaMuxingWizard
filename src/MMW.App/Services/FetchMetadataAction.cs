using System.Globalization;
using MMW.App.Resources;
using MMW.Metadata.Mapping;
using MMW.Metadata.Search;
using MMW.Queue;

namespace MMW.App.Services;

/// <summary>Queue action: searches the default provider for the file and applies the best match.</summary>
public sealed class FetchMetadataAction : QueueAction
{
    /// <summary>Artwork to download; null downloads none.</summary>
    public ArtworkKind? Artwork { get; set; } = ArtworkKind.Poster;

    public override string Description => Strings.QueueAction_FetchMetadata;

    public override async Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var service = context.Services?.GetService(typeof(MetadataService)) as MetadataService
                      ?? throw new InvalidOperationException(Strings.Error_MetadataSearchUnavailable);
        var document = context.Document;

        var prefill = SearchPrefill.From(document);
        var provider = service.Registry.DefaultFor(prefill.Kind) ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoProviderFormat, prefill.Kind));
        var language = service.LanguageFor(provider);
        var query = prefill with { Language = language };
        if (string.IsNullOrWhiteSpace(query.Title))
        {
            context.Log(Strings.QueueLog_NothingToSearch);
            return;
        }

        var results = await provider.SearchAsync(query, cancellationToken);
        if (results.Count == 0)
        {
            context.Log(string.Format(CultureInfo.CurrentCulture, Strings.QueueLog_NoResultsFormat, provider.Name, query));
            return;
        }

        var best = await provider.LoadDetailsAsync(results[0], language, cancellationToken);
        context.Log($"{provider.Name}: {best.DisplayTitle}");
        var set = service.Maps.For(best.Kind).Apply(best);

        if (Artwork is { } kind)
        {
            var remote = best.Artworks.FirstOrDefault(a => a.Kind == kind)
                         ?? best.Artworks.FirstOrDefault(a => a.Kind is ArtworkKind.Poster or ArtworkKind.Season or ArtworkKind.Square);
            if (remote is not null && await service.Downloader.TryDownloadAsync(remote.FullUrl, cancellationToken) is { } image)
                set.Artworks.Add(image);
        }

        MetadataApplier.Apply(document, set, service.ApplyOptionsFor(best.Kind, replaceArtworks: set.Artworks.Count > 0));
    }
}
