using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Formats.Mp4.Media;

namespace MMW.Formats.Mp4;

/// <summary>Reads and writes MP4/M4V/M4A/M4B/MOV files.</summary>
/// <remarks>
/// Saves are done in place (or by copying the media data verbatim) when only metadata and track properties changed.
/// The file is remuxed (rebuilt from its tracks' sources through <see cref="Remuxer"/>) when the document has tracks
/// imported from other files, when it is saved as Matroska, or when <see cref="SaveOptions.Optimize"/> is requested
/// for a file whose media data is not interleaved.
/// </remarks>
public sealed class Mp4Handler : IContainerHandler
{
    static Mp4Handler() => Mp4MediaFormat.Register();

    public ContainerKind Kind => ContainerKind.Mp4;

    public Task<MediaDocument> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Mp4Reader.Read(path), cancellationToken);

    public async Task SaveAsync(MediaDocument document, SaveOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var target = RemuxPolicy.TargetKind(document, options);
        if (target != ContainerKind.Mp4 || RemuxPolicy.HasImportedTracks(document) || (options.Optimize && NeedsInterleaving(document)))
        {
            await Remuxer.SaveAsync(document, options, target, progress, cancellationToken);
            return;
        }

        // No ConfigureAwait(false): the document is bound to the UI, so it must be updated on the caller's context.
        await Task.Run(() => Mp4Writer.Save(document, options, progress, cancellationToken), cancellationToken);

        // Re-read so track IDs, layout and preserved items reflect the new file.
        var saved = await ReadAsync(options.OutputPath ?? document.Path!, cancellationToken);
        document.ContainerState = saved.ContainerState;
        document.FileSize = saved.FileSize;
        if (options.OutputPath is not null)
            document.Path = options.OutputPath;
        foreach (var chapterTrack in document.Tracks.OfType<ChapterTrack>())
            chapterTrack.Id = saved.Tracks.OfType<ChapterTrack>().FirstOrDefault()?.Id ?? 0;
        document.IsDirty = false;
    }

    /// <summary>True when optimizing requires re-interleaving the media data (not just moving the header).</summary>
    private static bool NeedsInterleaving(MediaDocument document) =>
        document.ContainerState is Mp4State state && !Mp4MediaFormat.IsOptimized(state.Layout, state.Moov, TimeSpan.FromSeconds(1));
}
