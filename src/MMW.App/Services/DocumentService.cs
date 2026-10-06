using System.Globalization;
using MMW.App.Resources;
using MMW.Core.Model;
using MMW.Formats.Mp4;

namespace MMW.App.Services;

/// <summary>Opens and saves documents through the registered container handlers.</summary>
public sealed class DocumentService
{
    private readonly ContainerRegistry _registry;

    public DocumentService(IEnumerable<IContainerHandler>? handlers = null)
    {
        _registry = new ContainerRegistry(handlers ?? DefaultHandlers());
    }

    public static IEnumerable<IContainerHandler> DefaultHandlers()
    {
        yield return new Mp4Handler();
        yield return new MMW.Formats.Matroska.MatroskaHandler();
    }

    public Task<MediaDocument> OpenAsync(string path, CancellationToken ct = default) => _registry.OpenAsync(path, ct);

    public IContainerHandler HandlerFor(MediaDocument doc) =>
        _registry.Get(doc.Container) ?? throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoWriterFormat, doc.Container));

    public static bool IsSupported(string path) => ContainerKinds.FromPath(path) != ContainerKind.Unknown;
}
