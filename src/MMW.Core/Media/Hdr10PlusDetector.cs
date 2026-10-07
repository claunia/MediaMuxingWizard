using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

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
        if (config.Kind != TrackKind.Video || config.Codec is not (CodecType.Hevc or CodecType.H264 or CodecType.Vvc or CodecType.Evc or CodecType.Av1 or CodecType.Av2 or CodecType.Vp9))
            return false;

        var lengthSize = HdrVividDetector.NalLengthSize(config);
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

/// <summary>Finds HDR Vivid and other dynamic metadata (ST 2094-10, SL-HDR) in the first frames of a video track.</summary>
public static class HdrVividDetector
{
    /// <summary>Frames inspected; HDR Vivid metadata comes with (almost) every frame.</summary>
    public const int MaxSamples = 48;

    public static bool CanScan(CodecConfig config) =>
        config.Kind == TrackKind.Video && config.Codec is CodecType.Hevc or CodecType.H264 or CodecType.Vvc or CodecType.Evc or CodecType.Av1 or
            CodecType.Av2 or CodecType.Avs2 or CodecType.Avs3;

    /// <summary>True when the track is worth scanning: a video track of the document's own file not known to carry HDR Vivid.</summary>
    public static bool NeedsCheck(VideoTrack video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return !video.HdrVivid && video.OtherDynamicHdr == DynamicHdrFormats.None && video.Source is not null && !video.IsPending;
    }

    /// <summary>Scans an open sample source; the source is rewound afterwards.</summary>
    public static bool Detect(ISampleSource track, CancellationToken cancellationToken = default) => DetectAll(track, cancellationToken).HdrVivid;

    /// <summary>HDR Vivid and the other dynamic formats in the first frames; the source is rewound afterwards.</summary>
    public static (bool HdrVivid, DynamicHdrFormats Other) DetectAll(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var config = track.Config;
        if (!CanScan(config))
            return (config.HdrVivid, config.OtherDynamicHdr);

        var lengthSize = NalLengthSize(config);
        var vivid = config.HdrVivid;
        var other = config.OtherDynamicHdr;
        track.Reset();
        try
        {
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = sample.GetData().Span;
                vivid |= HdrVivid.InSample(config.Codec, data, lengthSize);
                other |= DynamicHdr.InSample(config.Codec, data, lengthSize);
            }

            return (vivid, other);
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>NAL unit length field size of a length-prefixed track (4 when its configuration does not say).</summary>
    internal static int NalLengthSize(CodecConfig config) => config.Codec switch
    {
        CodecType.Hevc when config.Extradata is { Length: > 21 } hvcc => (hvcc[21] & 3) + 1,
        CodecType.H264 when config.Extradata is { Length: > 4 } avcc => (avcc[4] & 3) + 1,
        CodecType.Vvc when config.Extradata is { Length: > 0 } vvcc => ((vvcc[0] >> 1) & 3) + 1,
        CodecType.Evc when config.Extradata is { } evcc => Evc.LengthSize(evcc),
        _ => 4,
    };
}

/// <summary>What a scan of a video track's first frames found (see <see cref="VideoBitstreamScan"/>).</summary>
public sealed record VideoScanResult(
    DolbyVisionDetection? MissingDolbyVision, bool Hdr10Plus, VideoStreamInfo? StreamInfo = null, bool HdrVivid = false,
    DynamicHdrFormats OtherDynamicHdr = DynamicHdrFormats.None);

/// <summary>Scans a document's video track once for Dolby Vision RPUs the container does not signal and for HDR10+.</summary>
public static class VideoBitstreamScan
{
    public static bool NeedsScan(VideoTrack video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return DolbyVisionDetector.NeedsCheck(video) || Hdr10PlusDetector.NeedsCheck(video) || HdrVividDetector.NeedsCheck(video) ||
               video.StreamInfo is null && video.Source is not null && !video.IsPending;
    }

    public static Task<VideoScanResult> ScanAsync(VideoTrack video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        var source = video.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        var checkDolbyVision = DolbyVisionDetector.NeedsCheck(video);
        var checkHdr10Plus = Hdr10PlusDetector.NeedsCheck(video);
        var checkHdrVivid = HdrVividDetector.NeedsCheck(video);
        var checkStream = video.StreamInfo is null;
        var color = video.Color;
        return Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            if (demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is not { } track)
                return new VideoScanResult(null, false);
            var dv = checkDolbyVision ? DolbyVisionDetector.Detect(track, color, cancellationToken) : null;
            var plus = checkHdr10Plus && Hdr10PlusDetector.Detect(track, cancellationToken);
            var stream = checkStream ? VideoStreamInfoScanner.Scan(track, cancellationToken) : null;
            var (vivid, other) = checkHdrVivid ? HdrVividDetector.DetectAll(track, cancellationToken) : (false, DynamicHdrFormats.None);
            return new VideoScanResult(dv, plus, stream, vivid, other);
        }, cancellationToken);
    }
}
