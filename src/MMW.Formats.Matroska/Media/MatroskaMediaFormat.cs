using MMW.Core.Media;
using MMW.Core.Model;

namespace MMW.Formats.Matroska.Media;

/// <summary>Registers the Matroska demuxer and muxer with <see cref="MediaFormatRegistry"/>.</summary>
public static class MatroskaMediaFormat
{
    private static int s_registered;

    /// <summary>Registers the Matroska factories (idempotent).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1)
            return;
        MediaFormatRegistry.Register(new MatroskaDemuxerFactory());
        MediaFormatRegistry.Register(new MatroskaMuxerFactory());
    }
}

/// <summary>Opens Matroska/WebM files as <see cref="IDemuxer"/>s.</summary>
public sealed class MatroskaDemuxerFactory : IDemuxerFactory
{
    public string Name => "Matroska";

    public int Probe(string path, ReadOnlySpan<byte> header)
    {
        if (ContainerKinds.Sniff(header) == ContainerKind.Matroska)
            return 90;
        return ContainerKinds.FromPath(path) == ContainerKind.Matroska ? 40 : 0;
    }

    public IDemuxer Open(string path, DemuxOptions? options = null) => MatroskaDemuxer.Open(path);
}

/// <summary>Creates Matroska muxers.</summary>
public sealed class MatroskaMuxerFactory : IMuxerFactory
{
    public ContainerKind Kind => ContainerKind.Matroska;

    public TrackSupport CheckSupport(CodecConfig config) => MatroskaCodecMapping.CheckSupport(config);

    public IMuxer Create(Stream output, MuxerSettings settings) => new MatroskaMuxer(output, settings);

    public async Task AdoptAsync(MediaDocument document, string path, IReadOnlyList<Track> written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(written);
        var (saved, layout) = await Task.Run(() => MatroskaReader.Read(path, cancellationToken), cancellationToken);
        RemuxAdoption.Apply(document, saved, written);

        // Keep the document's artwork instances so the next in-place save sees them as unchanged.
        var attached = layout.Attachments.Where(a => a.Artwork is not null).ToList();
        var artworks = document.Metadata.Artworks;
        if (attached.Count == artworks.Count)
        {
            for (var i = 0; i < attached.Count; i++)
                attached[i].Artwork = artworks[i];
        }

        MatroskaUpdateBuilder.CaptureSnapshot(document, layout);
    }
}
