using System.Runtime.CompilerServices;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Finds Dolby Atmos in Dolby TrueHD and E-AC-3 tracks from their frames, whatever the container: TrueHD signals it in
/// its major syncs (the 16-channel presentation), E-AC-3 in the Joint Object Coding extension of its frames. Matroska
/// and MP4's TrueHD entry do not say. E-AC-3's channel count is taken from its frames too (independent and dependent
/// substreams), which an MP4 'dec3' can understate.
/// </summary>
public static class AtmosDetector
{
    /// <summary>Packets examined: TrueHD repeats a major sync at least every 128 access units.</summary>
    public const int MaxSamples = 256;

    /// <summary>Tracks already examined (Atmos or not), so they are read once.</summary>
    private static readonly ConditionalWeakTable<AudioTrack, object> s_checked = [];

    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec is CodecType.TrueHd or CodecType.Eac3;

    /// <summary>True for a document's TrueHD or E-AC-3 track that has not been examined (TrueHD already known to be Atmos is skipped).</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var eac3 = audio.Format == "E-AC-3" || audio.CodecId is "A_EAC3" or "ec-3";
        var trueHd = audio.Format == "TrueHD" || audio.CodecId is "A_TRUEHD" or "mlpa";
        return audio.Source is not null && !audio.IsPending && !s_checked.TryGetValue(audio, out _) && (eac3 || trueHd && !audio.IsAtmos);
    }

    /// <summary>Reads the first packets of an open source (rewound afterwards): true when they carry Atmos.</summary>
    public static bool Detect(ISampleSource track, CancellationToken cancellationToken = default) => Examine(track, cancellationToken).Atmos;

    /// <summary>
    /// Reads the first packets of an open source (rewound afterwards): whether they carry Atmos, and for E-AC-3 the
    /// channel count of the whole programme (0 when unknown).
    /// </summary>
    public static (bool Atmos, int Channels) Examine(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanScan(track.Config))
            return (false, 0);
        track.Reset();
        try
        {
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = sample.GetData().Span;
                if (track.Config.Codec == CodecType.Eac3)
                {
                    // One access unit holds the independent substream and its dependent ones (the extra channels).
                    var frames = Ac3.ParseAccessUnit(data);
                    if (frames.Count == 0)
                        continue;
                    var (channels, _, _) = Ac3.Describe(Ac3.BuildDec3(frames), eac3: true);
                    return (track.Config.IsAtmos || frames.Any(f => f.JocExtension), channels);
                }

                // A Matroska block may hold several TrueHD access units.
                while (data.Length > 0 && TrueHd.Parse(data) is { Length: > 0 } unit)
                {
                    if (unit.IsMajorSync)
                        return (track.Config.IsAtmos || unit.HasAtmos, 0);
                    data = data[Math.Min(unit.Length, data.Length)..];
                }
            }

            return (track.Config.IsAtmos, 0);
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>
    /// Examines a document's track from its source file: sets <see cref="AudioTrack.IsAtmos"/>, and E-AC-3's channel
    /// count from its frames. True when something changed.
    /// </summary>
    public static async Task<bool> DescribeAsync(AudioTrack audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var source = audio.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        var (atmos, channels) = await Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is { } track ? Examine(track, cancellationToken) : (false, 0);
        }, cancellationToken).ConfigureAwait(false);
        s_checked.AddOrUpdate(audio, true);
        var changed = false;
        if (atmos && !audio.IsAtmos)
        {
            audio.IsAtmos = true;
            changed = true;
        }

        if (channels > 0 && channels != audio.Channels)
        {
            audio.Channels = channels;
            audio.ChannelLayout = TrackConversions.ChannelName(channels);
            changed = true;
        }

        if (changed)
            TrackDetails.Refresh(audio);
        return changed;
    }
}
