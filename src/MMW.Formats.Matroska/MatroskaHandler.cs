using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using MMW.Formats.Matroska.Media;

namespace MMW.Formats.Matroska;

/// <summary>
/// Reads Matroska/WebM files and saves metadata edits in place (mkvpropedit-style): only the Info, Tracks, Tags,
/// Chapters, Attachments and SeekHead elements are rewritten; Clusters and Cues are never touched.
/// </summary>
/// <remarks>
/// Saving to a different path copies the file first and then edits the copy; afterwards the document refers to the
/// new file. Adding, removing or reordering tracks, and saving as MP4, remux the file through <see cref="Remuxer"/>.
/// </remarks>
public sealed class MatroskaHandler : IContainerHandler
{
    private const int CopyBufferSize = 1024 * 1024;

    static MatroskaHandler() => MatroskaMediaFormat.Register();

    /// <inheritdoc />
    public ContainerKind Kind => ContainerKind.Matroska;

    /// <inheritdoc />
    public async Task<MediaDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var (document, _) = await Task.Run(() => MatroskaReader.Read(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The edits require remuxing, or the file cannot be edited in place.</exception>
    /// <exception cref="IOException">The file changed on disk since it was read.</exception>
    public async Task SaveAsync(MediaDocument document, SaveOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var targetKind = RemuxPolicy.TargetKind(document, options);
        if (targetKind != ContainerKind.Matroska || RemuxPolicy.HasImportedTracks(document) ||
            document.ContainerState is not MatroskaLayout state || MatroskaUpdateBuilder.TrackListChange(document, state) is not null)
        {
            await Remuxer.SaveAsync(document, options, targetKind, progress, cancellationToken);
            return;
        }

        var source = document.Path ?? throw new InvalidOperationException("The document has no file to update.");
        var layout = state;

        var target = options.OutputPath is null || SamePath(options.OutputPath, source) ? source : Path.GetFullPath(options.OutputPath);

        // Heavy lifting off the caller's thread; observable document properties are updated back on it.
        var newLayout = await Task.Run(() => Save(document, layout, source, target, progress, cancellationToken), cancellationToken);

        document.ContainerState = newLayout;
        document.FileSize = newLayout.FileLength;
        if (!SamePath(target, source))
            document.Path = target;
        document.IsDirty = false;
        progress?.Report(1.0);
    }

    private static MatroskaLayout Save(MediaDocument document, MatroskaLayout layout, string source, string target, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureUnchanged(source, layout);
        var inPlace = ReferenceEquals(source, target);
        if (!inPlace)
            Copy(source, target, progress, ct);

        try
        {
            using (var handle = File.OpenHandle(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var reader = new EbmlReader(handle);
                var updates = MatroskaUpdateBuilder.Build(document, layout, reader);
                if (updates.Count > 0)
                {
                    MatroskaInPlaceWriter.Apply(handle, layout, updates, ct);
                    RandomAccess.FlushToDisk(handle);
                }
            }

            progress?.Report(0.95);
            return Reload(document, target, ct);
        }
        catch when (!inPlace)
        {
            // Do not leave a half-written copy behind.
            File.Delete(target);
            throw;
        }
    }

    /// <summary>Re-reads the layout of the saved file and records the document's state as the file's state.</summary>
    private static MatroskaLayout Reload(MediaDocument document, string path, CancellationToken ct)
    {
        var (_, layout) = MatroskaReader.Read(path, ct);

        // The artwork attachments were written in document order: keep the document's Artwork instances so the next
        // save recognises them as unchanged.
        var written = layout.Attachments.Where(a => a.Artwork is not null).ToList();
        var artworks = document.Metadata.Artworks;
        if (written.Count == artworks.Count)
        {
            for (var i = 0; i < written.Count; i++)
                written[i].Artwork = artworks[i];
        }

        MatroskaUpdateBuilder.CaptureSnapshot(document, layout);
        return layout;
    }

    private static void EnsureUnchanged(string path, MatroskaLayout layout)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The file no longer exists.", path);
        if (info.Length != layout.FileLength || info.LastWriteTimeUtc != layout.LastWriteTimeUtc)
            throw new IOException($"'{info.Name}' was modified by another application since it was opened; reopen it before saving.");
    }

    private static void Copy(string source, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        var total = Math.Max(1, input.Length);
        try
        {
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
                copied += n;
                progress?.Report(0.9 * copied / total);
            }
        }
        catch
        {
            output.Dispose();
            File.Delete(target);
            throw;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
