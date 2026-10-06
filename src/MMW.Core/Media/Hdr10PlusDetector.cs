using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>Finds HDR10+ dynamic metadata in the first frames of a video track (bitstream or Matroska BlockAdditions).</summary>
public static class Hdr10PlusDetector
{
    /// <summary>Frames inspected; HDR10+ comes with (almost) every frame.</summary>
    public const int MaxSamples = 48;

    /// <summary>True when the track is worth scanning: a video track of the document's own file not known to carry HDR10+.</summary>
    public static bool NeedsCheck(VideoTrack video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return !video.Hdr10Plus && video.Source is not null && !video.IsPending;
    }

    /// <summary>Scans an open sample source; the source is rewound afterwards.</summary>
    public static bool Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var config = track.Config;
        if (config.Hdr10Plus || config.Hdr10PlusInBlockAdditions)
            return true;
        if (config.Kind != TrackKind.Video || config.Codec is not (CodecType.Hevc or CodecType.H264 or CodecType.Av1 or CodecType.Vp9))
            return false;

        var lengthSize = config.Codec switch
        {
            CodecType.Hevc when config.Extradata is { Length: > 21 } hvcc => (hvcc[21] & 3) + 1,
            CodecType.H264 when config.Extradata is { Length: > 4 } avcc => (avcc[4] & 3) + 1,
            _ => 4,
        };
        track.Reset();
        try
        {
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Hdr10Plus.InSample(config.Codec, sample.GetData().Span, lengthSize, sample.Additions))
                    return true;
            }

            return false;
        }
        finally
        {
            track.Reset();
        }
    }
}

/// <summary>What a scan of a video track's first frames found (see <see cref="VideoBitstreamScan"/>).</summary>
public sealed record VideoScanResult(DolbyVisionDetection? MissingDolbyVision, bool Hdr10Plus);

/// <summary>Scans a document's video track once for Dolby Vision RPUs the container does not signal and for HDR10+.</summary>
public static class VideoBitstreamScan
{
    public static bool NeedsScan(VideoTrack video) => DolbyVisionDetector.NeedsCheck(video) || Hdr10PlusDetector.NeedsCheck(video);

    public static Task<VideoScanResult> ScanAsync(VideoTrack video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        var source = video.Source ?? throw new InvalidOperationException("The track has no source file.");
        var checkDolbyVision = DolbyVisionDetector.NeedsCheck(video);
        var checkHdr10Plus = Hdr10PlusDetector.NeedsCheck(video);
        var color = video.Color;
        return Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            if (demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is not { } track)
                return new VideoScanResult(null, false);
            var dv = checkDolbyVision ? DolbyVisionDetector.Detect(track, color, cancellationToken) : null;
            var plus = checkHdr10Plus && Hdr10PlusDetector.Detect(track, cancellationToken);
            return new VideoScanResult(dv, plus);
        }, cancellationToken);
    }
}
