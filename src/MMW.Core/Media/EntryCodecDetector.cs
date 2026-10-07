using MMW.Core.Media.Codecs;
using MMW.Core.Model;

namespace MMW.Core.Media;

/// <summary>
/// Describes AC-4, MPEG-H 3D Audio and AMR tracks: the AC-4 table of contents (layout, presentations), the MPEG-H
/// profile and reference speaker layout, the AMR bit rates. Containers say none of it.
/// </summary>
public static class EntryCodecDetector
{
    /// <summary>Samples examined (AMR switches modes; the others describe themselves in the first one).</summary>
    public const int MaxSamples = 50;

    public static bool CanScan(CodecConfig config) =>
        config.Kind == TrackKind.Audio && config.Codec is CodecType.Ac4 or CodecType.MpegH or CodecType.AmrNb or CodecType.AmrWb;

    /// <summary>True for a document's track of these codecs that has not been described yet.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.Profile.Length == 0 && audio.Source is not null && !audio.IsPending && audio.Format is "AC-4" or "MPEG-H" or "AMR-NB" or "AMR-WB";
    }

    /// <summary>Reads the first samples of an open source (rewound afterwards); empty when there is nothing to say.</summary>
    public static string Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var config = track.Config;
        if (!CanScan(config))
            return string.Empty;
        if (config.Codec == CodecType.MpegH && MpegH.EntryConfig(config.Extradata) is { } stored)
            return MpegHDescription(stored.Parsed);
        track.Reset();
        try
        {
            var samples = new List<ReadOnlyMemory<byte>>();
            for (var i = 0; i < (config.Codec is CodecType.AmrNb or CodecType.AmrWb ? MaxSamples : 1) && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Add(sample.GetData().ToArray());
            }

            if (samples.Count == 0)
                return string.Empty;
            switch (config.Codec)
            {
                case CodecType.Ac4:
                    return Ac4.Parse(samples[0].Span) is { } info ? Ac4.Describe(info) : string.Empty;
                case CodecType.MpegH:
                {
                    MpegHConfig? found = null;
                    MpegH.ForEachPacket(samples[0].Span, (type, _, payload) =>
                    {
                        if (type == MpegH.PacketConfig && found is null)
                            found = MpegH.ParseConfig(payload);
                    });
                    return found is null ? string.Empty : MpegHDescription(found);
                }

                default:
                    return Amr.Describe(samples, config.Codec == CodecType.AmrWb);
            }
        }
        finally
        {
            track.Reset();
        }
    }

    private static string MpegHDescription(MpegHConfig config)
    {
        var parts = new List<string>();
        if (MpegH.ProfileLevel(config.ProfileLevel) is { Length: > 0 } profile)
            parts.Add(profile);
        if (MpegH.Layout(config.Cicp) is { Length: > 0 } layout)
            parts.Add(layout);
        return string.Join(", ", parts);
    }

    /// <summary>Describes a document's track from its source file and updates its profile.</summary>
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
