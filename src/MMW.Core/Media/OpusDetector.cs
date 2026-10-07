using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// Describes Opus tracks from their packets' TOC bytes, whatever the container: the coding modes (SILK, Hybrid,
/// CELT), the audio bandwidth and the frame durations. No container header says what the encoder chose.
/// </summary>
public static class OpusDetector
{
    /// <summary>Packets examined: ten seconds of 20 ms packets, enough to see the modes an encoder switches between.</summary>
    public const int MaxSamples = 500;

    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec == CodecType.Opus;

    /// <summary>True for a document's Opus track that has not been described yet.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.Profile.Length == 0 && audio.Source is not null && !audio.IsPending &&
               (audio.Format == "Opus" || audio.CodecId is "A_OPUS" or "Opus");
    }

    /// <summary>Reads the first packets of an open source (rewound afterwards); empty when it is not Opus.</summary>
    public static string Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanScan(track.Config))
            return string.Empty;
        track.Reset();
        try
        {
            var packets = new List<ReadOnlyMemory<byte>>();
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = sample.Size > 1 ? new byte[2] : [];
                sample.CopyHead(data);
                packets.Add(data);
            }

            return Opus.DescribeStream(packets);
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>Describes a document's Opus track from its source file and updates its profile.</summary>
    public static async Task<bool> DescribeAsync(AudioTrack audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var source = audio.Source ?? throw new InvalidOperationException("The track has no source file.");
        var description = await Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is { } track ? Detect(track, cancellationToken) : string.Empty;
        }, cancellationToken).ConfigureAwait(false);
        if (description.Length == 0)
            return false;
        audio.Profile = description;
        return true;
    }
}
