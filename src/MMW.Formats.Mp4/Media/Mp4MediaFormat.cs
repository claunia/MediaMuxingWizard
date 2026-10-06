using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4.Media;

/// <summary>Registers the MP4 demuxer and muxer with <see cref="MediaFormatRegistry"/>.</summary>
public static class Mp4MediaFormat
{
    private static int s_registered;

    /// <summary>Registers the MP4 factories (idempotent).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1)
            return;
        MediaFormatRegistry.Register(new Mp4DemuxerFactory());
        MediaFormatRegistry.Register(new Mp4MuxerFactory());
    }

    /// <summary>
    /// True when the file's media data is laid out for streaming: moov before mdat and the chunks of all tracks
    /// interleaved in time (no track runs ahead of another by more than <paramref name="tolerance"/>).
    /// </summary>
    internal static bool IsOptimized(Mp4Layout layout, Box moov, TimeSpan tolerance)
    {
        var moovIndex = layout.Boxes.ToList().FindIndex(b => b.Type == "moov");
        var firstMdat = layout.Boxes.ToList().FindIndex(b => b.Type == "mdat");
        if (firstMdat >= 0 && moovIndex > firstMdat)
            return false;

        var chunks = new List<(long Offset, double Time)>();
        foreach (var trak in moov.FindAll("trak"))
        {
            var stbl = trak.FindPath("mdia/minf/stbl");
            var mdhd = trak.FindPath("mdia/mdhd");
            if (stbl is null || mdhd is null)
                continue;
            // The first and last sample of every chunk (a whole track in one chunk is not interleaved either).
            var timescale = (double)Math.Max(1u, HeaderBoxes.MdhdTimescale(mdhd));
            var samples = SampleTable.Expand(stbl);
            var chunkStarts = SampleTable.ChunkOffsets(stbl).ToHashSet();
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                var first = chunkStarts.Contains(s.Offset);
                var last = i + 1 == samples.Length || chunkStarts.Contains(samples[i + 1].Offset);
                if (first || last)
                    chunks.Add((s.Offset, s.Dts / timescale));
            }
        }

        chunks.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var maxTime = double.MinValue;
        foreach (var (_, time) in chunks)
        {
            if (time < maxTime - tolerance.TotalSeconds)
                return false;
            maxTime = Math.Max(maxTime, time);
        }

        return true;
    }
}

/// <summary>Opens MP4/MOV files as <see cref="IDemuxer"/>s.</summary>
public sealed class Mp4DemuxerFactory : IDemuxerFactory
{
    public string Name => "MP4";

    public int Probe(string path, ReadOnlySpan<byte> header)
    {
        if (ContainerKinds.Sniff(header) == ContainerKind.Mp4)
            return 90;
        return ContainerKinds.FromPath(path) == ContainerKind.Mp4 ? 40 : 0;
    }

    public IDemuxer Open(string path, DemuxOptions? options = null) => Mp4Demuxer.Open(path);
}

/// <summary>Creates MP4 muxers.</summary>
public sealed class Mp4MuxerFactory : IMuxerFactory
{
    public ContainerKind Kind => ContainerKind.Mp4;

    public TrackSupport CheckSupport(CodecConfig config) => Mp4SampleEntries.CheckSupport(config);

    public IMuxer Create(Stream output, MuxerSettings settings) => new Mp4Muxer(output, settings);

    public async Task AdoptAsync(MediaDocument document, string path, IReadOnlyList<Track> written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var saved = await Task.Run(() => Mp4Reader.Read(path), cancellationToken);
        RemuxAdoption.Apply(document, saved, written);
    }
}
