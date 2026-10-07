using System.Runtime.CompilerServices;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Finds Dolby Atmos in Dolby TrueHD and E-AC-3 tracks from their frames, whatever the container: TrueHD signals it in
/// its major syncs (the 16-channel presentation), E-AC-3 in the Joint Object Coding extension of its frames. Matroska
/// and MP4's TrueHD entry do not say.
/// </summary>
public static class AtmosDetector
{
    /// <summary>Packets examined: TrueHD repeats a major sync at least every 128 access units.</summary>
    public const int MaxSamples = 256;

    /// <summary>Tracks already examined (Atmos or not), so they are read once.</summary>
    private static readonly ConditionalWeakTable<AudioTrack, object> s_checked = [];

    public static bool CanScan(CodecConfig config) => config.Kind == TrackKind.Audio && config.Codec is CodecType.TrueHd or CodecType.Eac3;

    /// <summary>True for a document's TrueHD or E-AC-3 track that is not known to be Atmos and has not been examined.</summary>
    public static bool NeedsCheck(AudioTrack audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return !audio.IsAtmos && audio.Source is not null && !audio.IsPending && !s_checked.TryGetValue(audio, out _) &&
               (audio.Format is "TrueHD" or "E-AC-3" || audio.CodecId is "A_TRUEHD" or "A_EAC3" or "mlpa" or "ec-3");
    }

    /// <summary>Reads the first packets of an open source (rewound afterwards): true when they carry Atmos.</summary>
    public static bool Detect(ISampleSource track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanScan(track.Config))
            return false;
        if (track.Config.IsAtmos)
            return true;
        track.Reset();
        try
        {
            for (var i = 0; i < MaxSamples && track.ReadNext() is { } sample; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = sample.GetData().Span;
                if (track.Config.Codec == CodecType.Eac3)
                {
                    if (Ac3.ParseAccessUnit(data).Any(f => f.JocExtension))
                        return true;
                    continue;
                }

                // A Matroska block may hold several TrueHD access units.
                while (data.Length > 0 && TrueHd.Parse(data) is { Length: > 0 } unit)
                {
                    if (unit.IsMajorSync)
                        return unit.HasAtmos;
                    data = data[Math.Min(unit.Length, data.Length)..];
                }
            }

            return false;
        }
        finally
        {
            track.Reset();
        }
    }

    /// <summary>Examines a document's track from its source file and sets <see cref="AudioTrack.IsAtmos"/>; true when it is Atmos.</summary>
    public static async Task<bool> DescribeAsync(AudioTrack audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var source = audio.Source ?? throw new InvalidOperationException(Strings.Error_NoSourceFile);
        var atmos = await Task.Run(() =>
        {
            using var demuxer = MediaFormatRegistry.OpenDemuxer(source.Path, new DemuxOptions());
            return demuxer.Tracks.FirstOrDefault(t => t.TrackId == source.TrackId) is { } track && Detect(track, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        s_checked.AddOrUpdate(audio, true);
        if (!atmos)
            return false;
        audio.IsAtmos = true;
        return true;
    }
}
