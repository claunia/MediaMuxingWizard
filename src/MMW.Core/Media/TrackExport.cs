using System.Globalization;
using MMW.Core.Media.Export;
using MMW.Core.Resources;

namespace MMW.Core.Media;

/// <summary>
/// Writes one track of a source file as a standalone raw / elementary file in the conventional format of its codec,
/// as mkvextract does (H.264 Annex B, ADTS AAC, IVF, Ogg Opus, SubRip…).
/// </summary>
/// <remarks>
/// <para>
/// The samples are written as they are, in decoding order: decoder pre-roll and encoder padding marked by
/// <see cref="MediaSample.TrimEnd"/> or <see cref="ISampleSource.MediaStart"/> are kept, since raw formats cannot express
/// them (Ogg is the exception: its end granule position trims the last Opus packet). Formats that carry times (IVF,
/// Ogg, subtitles) use the samples' presentation times, subtitles on the output timeline
/// (<see cref="ISampleSource.MediaStart"/> at <see cref="ISampleSource.StartOffset"/>).
/// </para>
/// <para>Extensions by codec:</para>
/// <list type="table">
/// <item><term>H.264, HEVC, VVC</term><description>.h264, .h265, .h266 (Annex B, parameter sets from the configuration record repeated on key frames that lack them)</description></item>
/// <item><term>EVC</term><description>.evc (4-byte NAL unit lengths)</description></item>
/// <item><term>AV1, AV2, VP8, VP9</term><description>.ivf</description></item>
/// <item><term>MPEG-1/2/4 video, VC-1, AVS1/2/3, Dirac, DNxHD, H.263, Motion JPEG</term><description>.m1v, .m2v, .m4v, .vc1, .avs, .avs2, .avs3, .drc, .dnxhd, .h263, .mjpg</description></item>
/// <item><term>AAC</term><description>.aac (ADTS)</description></item>
/// <item><term>AC-3, E-AC-3, AC-4, DTS, TrueHD, MLP, MPEG audio, MPEG-H</term><description>.ac3, .eac3, .ac4, .dts, .thd, .mlp, .mp1/.mp2/.mp3, .mhas</description></item>
/// <item><term>FLAC, ALAC, AMR</term><description>.flac, .caf, .amr / .awb</description></item>
/// <item><term>PCM, ACM audio</term><description>.wav</description></item>
/// <item><term>Opus, Vorbis</term><description>.opus, .ogg</description></item>
/// <item><term>Text subtitles</term><description>.srt (SubRip and tx3g), .ass, .ssa, .vtt</description></item>
/// <item><term>PGS, VobSub</term><description>.sup; .idx (with the .sub next to it)</description></item>
/// </list>
/// </remarks>
public static class TrackExport
{
    /// <summary>The file extension (with dot, e.g. ".h264") of the raw format the track is exported to; null when it cannot be exported.</summary>
    public static string? Extension(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ExportWriters.Extension(config);
    }

    /// <summary>True when the track can be exported (<see cref="Extension"/> is not null).</summary>
    public static bool CanExport(CodecConfig config) => Extension(config) is not null;

    /// <summary>
    /// Writes the track to <paramref name="outputPath"/> (overwriting it). The file is written next to the destination
    /// first and moved into place when complete. A VobSub track also writes the .sub file next to the .idx.
    /// </summary>
    /// <param name="source">The track; it is rewound before and after.</param>
    /// <param name="outputPath">The destination file.</param>
    /// <param name="progress">Progress from 0 to 1, by duration.</param>
    /// <param name="cancellationToken">Cancels the export (the destination is left untouched).</param>
    /// <exception cref="NotSupportedException">The track's codec cannot be exported.</exception>
    public static Task ExportAsync(ISampleSource source, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (Extension(source.Config) is null)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_CannotExportRaw, source.Config.FormatName));
        return Task.Run(() => Export(source, Path.GetFullPath(outputPath), progress, cancellationToken), cancellationToken);
    }

    private static void Export(ISampleSource source, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? ".";
        var temp = Path.Combine(directory, "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        var companionExtension = ExportWriters.CompanionExtension(source.Config);
        var companion = companionExtension is null ? null : Path.ChangeExtension(outputPath, companionExtension);
        var companionTemp = companionExtension is null ? null : temp + companionExtension;
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var companionStream = companionTemp is null ? null : new FileStream(companionTemp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                var context = new ExportContext(source, stream, companionStream);
                var writer = ExportWriters.Create(context);
                source.Reset();
                writer.Start();
                var total = source.Duration.TotalSeconds;
                var count = source.SampleCountHint;
                long written = 0;
                long? first = null;
                var lastReport = -1.0;
                while (source.ReadNext() is { } sample)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(sample);
                    written++;
                    first ??= sample.Dts;
                    if (progress is null)
                        continue;
                    var fraction = total > 0 && source.Config.Timescale > 0
                        ? (sample.Dts - first.Value) / (double)source.Config.Timescale / total
                        : count > 0 ? written / (double)count : 0;
                    fraction = Math.Clamp(fraction, 0, 1);
                    if (fraction - lastReport >= 0.01)
                    {
                        lastReport = fraction;
                        progress.Report(fraction);
                    }
                }

                writer.Finish();
                stream.Flush();
                companionStream?.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, outputPath, overwrite: true);
            if (companion is not null)
                File.Move(companionTemp!, companion, overwrite: true);
            progress?.Report(1.0);
        }
        finally
        {
            source.Reset();
            TryDelete(temp);
            if (companionTemp is not null)
                TryDelete(companionTemp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
