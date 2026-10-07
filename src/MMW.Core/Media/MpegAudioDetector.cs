using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// Describes MPEG audio (MP1/MP2/MP3) tracks from their frame headers, whatever the container: constant or variable
/// bit rate and the rate, the channel mode, and the encoder of a LAME header.
/// </summary>
public static class MpegAudioDetector
{
    /// <summary>Frames examined: about ten seconds, enough to tell a variable bit rate from a constant one.</summary>
    public const int MaxSamples = 400;

    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec is CodecType.Mp3 or CodecType.Mp2 or CodecType.Mp1;

    /// <summary>True for a document's MPEG audio track that has not been described yet.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.Profile.Length == 0 && audio.Source is not null && !audio.IsPending &&
               (audio.Format is "MP3" or "MP2" or "MP1" || audio.CodecId is "A_MPEG/L3" or "A_MPEG/L2" or "A_MPEG/L1");
    }

    /// <summary>Reads the first frames of an open source (rewound afterwards); empty when it is not MPEG audio.</summary>
    public static string Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanScan(track.Config))
            return string.Empty;
        track.Reset();
        try
        {
            var frames = new List<ReadOnlyMemory<byte>>();
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The headers, and the whole of the first frames, which may be a LAME/Xing summary.
                var data = new byte[i < 2 ? sample.Size : Math.Min(sample.Size, 4)];
                sample.CopyHead(data);
                frames.Add(data);
            }

            return MpegAudio.DescribeStream(frames);
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>Describes a document's MPEG audio track from its source file and updates its profile.</summary>
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
