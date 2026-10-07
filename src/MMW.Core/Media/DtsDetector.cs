using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Describes DTS tracks from their first access units, whatever the container: the product (DTS-ES, DTS 96/24, DTS-HD
/// High Resolution / Master Audio, DTS Express, DTS:X, DTS:X IMAX) and the extension's channels, sample rate and bit
/// depth (a DTS-HD stream's core is only 5.1 / 48 kHz). Containers rarely say more than "DTS".
/// </summary>
public static class DtsDetector
{
    /// <summary>Access units examined: the DTS:X marker is carried by XLL frames, not necessarily the first.</summary>
    public const int MaxSamples = 16;

    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec == CodecType.Dts;

    /// <summary>True for a document's DTS track that has not been described yet.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.Profile.Length == 0 && audio.Source is not null && !audio.IsPending &&
               (audio.Format == "DTS" || audio.CodecId is "A_DTS" or "dtsc" or "dtsh" or "dtsl" or "dtse" or "dtsx");
    }

    /// <summary><paramref name="config"/> with the stream's description.</summary>
    public static CodecConfig Apply(CodecConfig config, DtsHeader header)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(header);
        return config with
        {
            AudioProfile = Dts.ProductName(header.Product),
            Channels = header.OutputChannels,
            SampleRate = header.OutputSampleRate,
            BitsPerSample = header.OutputBitsPerSample,
        };
    }

    /// <summary>Reads the first access units of an open source (rewound afterwards); null when it is not DTS.</summary>
    public static DtsHeader? Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanScan(track.Config))
            return null;
        track.Reset();
        try
        {
            var samples = new List<ReadOnlyMemory<byte>>();
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Add(sample.GetData());
            }

            return Dts.Describe(samples);
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>Describes a document's DTS track from its source file and updates it (profile, channels, sample rate).</summary>
    public static async Task<bool> DescribeAsync(AudioTrack audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var source = audio.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        var header = await Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is { } track ? Detect(track, cancellationToken) : null;
        }, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return false;
        audio.Profile = Dts.ProductName(header.Product);
        if (audio.Channels != header.OutputChannels || audio.SampleRate != header.OutputSampleRate)
        {
            audio.Channels = header.OutputChannels;
            audio.SampleRate = header.OutputSampleRate;
            audio.FormatDetails = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{audio.Channels} ch, {audio.SampleRate} Hz");
        }

        return true;
    }
}
