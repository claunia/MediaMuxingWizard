using MMW.Core.Model;

namespace MMW.Formats.Mp4;

/// <summary>Reads and writes MP4/M4V/M4A/M4B/MOV files.</summary>
public sealed class Mp4Handler : IContainerHandler
{
    public ContainerKind Kind => ContainerKind.Mp4;

    public Task<MediaDocument> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Mp4Reader.Read(path), cancellationToken);

    public async Task SaveAsync(MediaDocument document, SaveOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
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
}
