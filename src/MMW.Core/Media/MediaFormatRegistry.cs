namespace MMW.Core.Media;

/// <summary>
/// Process-wide registry of demuxer and muxer factories. Format assemblies register themselves (the MP4 and Matroska
/// handlers do so in their static constructors; elementary streams are registered by the remux/import component),
/// which lets a handler remux to or from a container whose assembly it does not reference.
/// </summary>
public static class MediaFormatRegistry
{
    private static readonly Lock s_lock = new();
    private static readonly List<IDemuxerFactory> s_demuxers = [];
    private static readonly Dictionary<Model.ContainerKind, IMuxerFactory> s_muxers = [];

    /// <summary>Registers a demuxer factory (once per factory type).</summary>
    public static void Register(IDemuxerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (s_lock)
        {
            if (s_demuxers.All(f => f.GetType() != factory.GetType()))
                s_demuxers.Add(factory);
        }
    }

    /// <summary>Registers (or replaces) the muxer factory of a container family.</summary>
    public static void Register(IMuxerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (s_lock)
            s_muxers[factory.Kind] = factory;
    }

    public static IReadOnlyList<IDemuxerFactory> Demuxers
    {
        get
        {
            lock (s_lock)
                return [.. s_demuxers];
        }
    }

    public static IMuxerFactory? GetMuxer(Model.ContainerKind kind)
    {
        lock (s_lock)
            return s_muxers.GetValueOrDefault(kind);
    }

    /// <summary>The factory most confident that it can read <paramref name="path"/>, or null.</summary>
    public static IDemuxerFactory? FindDemuxer(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var header = new byte[4096];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        IDemuxerFactory? best = null;
        var bestScore = 0;
        foreach (var factory in Demuxers)
        {
            var score = factory.Probe(path, header.AsSpan(0, read));
            if (score > bestScore)
            {
                best = factory;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Opens <paramref name="path"/> with the best matching demuxer.</summary>
    /// <exception cref="NotSupportedException">No registered demuxer recognises the file.</exception>
    public static IDemuxer OpenDemuxer(string path, DemuxOptions? options = null)
    {
        var factory = FindDemuxer(path) ?? throw new NotSupportedException($"'{Path.GetFileName(path)}' is not in a supported format.");
        return factory.Open(path, options);
    }
}
