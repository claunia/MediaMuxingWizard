using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Finds Dolby Vision in a video track's bitstream (RPU NAL units / T.35 metadata OBUs) and rebuilds the decoder
/// configuration record the container needs for players to recognise it.
/// </summary>
public static class DolbyVisionDetector
{
    /// <summary>Frames inspected; RPUs come with every frame, so a few are enough.</summary>
    public const int MaxSamples = 96;

    /// <summary>True when the track is worth scanning: HEVC, AV1 or H.264 video without a configuration record.</summary>
    public static bool NeedsCheck(VideoTrack video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return video.DolbyVisionRecord is null && video.Source is not null && !video.IsPending &&
               video.Format is "HEVC" or "AV1" or "H.264" or "HEVC Dolby Vision" or "AV1 Dolby Vision" or "H.264 Dolby Vision";
    }

    /// <summary>
    /// Scans the start of <paramref name="video"/>'s source track. Returns the rebuilt configuration when Dolby
    /// Vision RPUs are present, null otherwise.
    /// </summary>
    public static Task<DolbyVisionDetection?> DetectAsync(VideoTrack video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        var source = video.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        return Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            var track = demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId);
            return track is null ? null : Detect(track, video.Color, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Scans an open sample source (see <see cref="DetectAsync"/>).</summary>
    public static DolbyVisionDetection? Detect(ISampleSource track, ColorInfo color, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var config = track.Config;
        if (config.Codec is not (CodecType.Hevc or CodecType.Av1 or CodecType.H264))
            return null;

        track.Reset();
        var samples = new List<ReadOnlyMemory<byte>>();
        for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(sample.GetData());
        }

        track.Reset();
        var colour = config.Color.IsSpecified ? config.Color : color;
        var fps = config.FrameRate > 0 ? config.FrameRate : track.Duration.TotalSeconds > 0 && track.SampleCountHint > 0
            ? track.SampleCountHint / track.Duration.TotalSeconds
            : 24;

        if (config.Codec == CodecType.Av1)
        {
            var (found, av1Header) = DolbyVision.ScanAv1(samples);
            return found ? DolbyVision.Describe(CodecType.Av1, av1Header, false, config.Width, config.Height, fps, colour) : null;
        }

        if (config.Codec == CodecType.H264)
        {
            // avcC: lengthSizeMinusOne in the low bits of byte 4.
            var avcLength = config.Extradata is { Length: > 4 } avcc ? (avcc[4] & 3) + 1 : 4;
            var (avcHeader, avcRpu) = DolbyVision.ScanAvc(samples, avcLength);
            return avcRpu ? DolbyVision.Describe(CodecType.H264, avcHeader, false, config.Width, config.Height, fps, colour) : null;
        }

        var lengthSize = config.Extradata is { Length: > 21 } hvcc ? (hvcc[21] & 3) + 1 : 4;
        var (header, rpuSeen, elSeen) = DolbyVision.ScanHevc(samples, lengthSize);
        if (!rpuSeen || header is null)
            return null;
        return DolbyVision.Describe(CodecType.Hevc, header, elSeen, config.Width, config.Height, fps, colour);
    }
}
