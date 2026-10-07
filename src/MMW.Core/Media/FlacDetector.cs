using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Describes FLAC tracks from their STREAMINFO and vendor string (the codec configuration of any container): bit
/// depth, block size, encoder, and whether the audio's MD5 is stored.
/// </summary>
public static class FlacDetector
{
    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec == CodecType.Flac;

    /// <summary>True for a document's FLAC track that has not been described yet.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.Profile.Length == 0 && audio.Source is not null && !audio.IsPending &&
               (audio.Format == "FLAC" || audio.CodecId is "A_FLAC" or "fLaC");
    }

    /// <summary>The description of a FLAC configuration; empty for other codecs or without STREAMINFO.</summary>
    public static string Detect(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return CanScan(config) ? Flac.DescribeStream(config.Extradata) : string.Empty;
    }

    /// <summary>Describes a document's FLAC track from its source file and updates its profile.</summary>
    public static async Task<bool> DescribeAsync(AudioTrack audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var source = audio.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        var description = await Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is { } track ? Detect(track.Config) : string.Empty;
        }, cancellationToken).ConfigureAwait(false);
        if (description.Length == 0)
            return false;
        audio.Profile = description;
        return true;
    }
}
