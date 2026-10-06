namespace MMW.Core.Model;

/// <summary>Options for writing a document.</summary>
public sealed record SaveOptions
{
    /// <summary>Destination path; null or equal to the source path means "update in place".</summary>
    public string? OutputPath { get; init; }

    /// <summary>Interleave samples and put the header before the media data (MP4 "fast start").</summary>
    public bool Optimize { get; init; }

    /// <summary>Force 64-bit chunk offsets (MP4). Turned on automatically for large files.</summary>
    public bool Use64BitOffsets { get; init; }

    /// <summary>Use 64-bit times/durations in MP4 headers.</summary>
    public bool Use64BitTimes { get; init; }
}

/// <summary>Reads and writes one container family.</summary>
public interface IContainerHandler
{
    ContainerKind Kind { get; }

    /// <summary>Opens a file and builds a <see cref="MediaDocument"/>.</summary>
    Task<MediaDocument> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes the document (in place or to <see cref="SaveOptions.OutputPath"/>).</summary>
    Task SaveAsync(MediaDocument document, SaveOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Picks the handler for a file.</summary>
public sealed class ContainerRegistry
{
    private readonly Dictionary<ContainerKind, IContainerHandler> _handlers = [];

    public ContainerRegistry(IEnumerable<IContainerHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        foreach (var h in handlers)
            _handlers[h.Kind] = h;
    }

    public IContainerHandler? Get(ContainerKind kind) => _handlers.GetValueOrDefault(kind);

    /// <summary>Detects the container of <paramref name="path"/> from its content, falling back to the extension.</summary>
    public static ContainerKind Detect(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using (var fs = File.OpenRead(path))
        {
            var read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var kind = ContainerKinds.Sniff(header[..read]);
            if (kind != ContainerKind.Unknown)
                return kind;
        }

        return ContainerKinds.FromPath(path);
    }

    public Task<MediaDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var kind = Detect(path);
        var handler = Get(kind) ?? throw new NotSupportedException($"'{Path.GetFileName(path)}' is not a supported MP4 or Matroska file.");
        return handler.ReadAsync(path, cancellationToken);
    }
}
