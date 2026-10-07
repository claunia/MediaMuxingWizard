using System.Runtime.CompilerServices;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion.Interop;

namespace MMW.Media.Conversion;

/// <summary>A decoded bitmap subtitle (one rectangle of a subtitle event), ready for OCR.</summary>
/// <param name="Start">Presentation time at which the bitmap appears.</param>
/// <param name="End">Presentation time at which it disappears.</param>
/// <param name="X">Left edge on the canvas.</param>
/// <param name="Y">Top edge on the canvas.</param>
/// <param name="Width">Bitmap width in pixels.</param>
/// <param name="Height">Bitmap height in pixels.</param>
/// <param name="Rgba">Pixels, 4 bytes per pixel (R, G, B, A, straight alpha), rows top to bottom without padding.</param>
/// <param name="Forced">The subtitle is flagged as forced (VobSub forced start / PGS forced object).</param>
/// <param name="CanvasWidth">Width of the video canvas the position refers to (0 when unknown).</param>
/// <param name="CanvasHeight">Height of the canvas (0 when unknown).</param>
public sealed record SubtitleBitmap(TimeSpan Start, TimeSpan End, int X, int Y, int Width, int Height, byte[] Rgba, bool Forced, int CanvasWidth, int CanvasHeight)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Alpha of the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte AlphaAt(int x, int y) => Rgba[((y * Width) + x) * 4 + 3];
}

/// <summary>
/// Decodes PGS (hdmv_pgs_subtitle), VobSub (dvd_subtitle, palette and size from the .idx header in
/// <see cref="CodecConfig.Extradata"/>) and DVB subtitle tracks into RGBA bitmaps with display times — the input of
/// OCR. Decoding is streamed: one batch of samples at a time on the thread pool.
/// </summary>
/// <remarks>
/// End times come from the event itself (VobSub stop command, DVB timeout); otherwise from the next event of the
/// track (PGS clears the screen with an empty display set), the sample duration, or at the end of the track a default
/// of five seconds (capped to the track duration).
/// </remarks>
public static class BitmapSubtitleDecoder
{
    private const int BatchSize = 64;
    private static readonly TimeSpan s_defaultDuration = TimeSpan.FromSeconds(5);

    /// <summary>True when FFmpeg is available and <paramref name="config"/> is a bitmap subtitle format it decodes.</summary>
    public static bool CanDecode(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Codec is CodecType.Pgs or CodecType.VobSub or CodecType.DvbSub or CodecType.Xsub && FFmpegLoader.IsAvailable &&
               AvUtil.HasDecoder(CodecMapping.DecoderId(config));
    }

    /// <summary>Decodes a subtitle track of a file opened with the registered demuxers (<see cref="MediaFormatRegistry"/>).</summary>
    /// <exception cref="NotSupportedException">The file format or the codec is not supported, or FFmpeg is unavailable.</exception>
    /// <exception cref="InvalidDataException">The track does not exist.</exception>
    public static async IAsyncEnumerable<SubtitleBitmap> DecodeAsync(string path, uint trackId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var demuxer = await Task.Run(() => MediaFormatRegistry.OpenDemuxer(path), cancellationToken);
        var source = demuxer.Tracks.FirstOrDefault(t => t.TrackId == trackId) ??
                     throw new InvalidDataException($"Track {trackId} was not found in '{Path.GetFileName(path)}'.");
        await foreach (var bitmap in DecodeAsync(source, cancellationToken))
            yield return bitmap;
    }

    /// <summary>Decodes every sample of <paramref name="source"/> (rewound first) into bitmaps, in display order.</summary>
    /// <exception cref="NotSupportedException">The codec is not a supported bitmap format, or FFmpeg is unavailable.</exception>
    public static async IAsyncEnumerable<SubtitleBitmap> DecodeAsync(ISampleSource source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Config.Kind != TrackKind.Subtitle || source.Config.Codec is not (CodecType.Pgs or CodecType.VobSub or CodecType.DvbSub))
            throw new NotSupportedException($"{source.Config.FormatName} is not a bitmap subtitle format.");
        FFmpegLoader.EnsureAvailable();

        using var session = new Session(source);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ready, finished) = await Task.Run(() => session.DecodeBatch(cancellationToken), cancellationToken);
            foreach (var bitmap in ready)
                yield return bitmap;
            if (finished)
                yield break;
        }
    }

    /// <summary>
    /// Decodes every sample of <paramref name="source"/> (rewound first) into bitmaps, in display order, on the
    /// calling thread (for consumers that are themselves synchronous, such as a converting
    /// <see cref="ISampleSource"/>). Decoding is lazy: one batch of samples per step of the enumeration, and the
    /// decoder is released when the enumeration is disposed.
    /// </summary>
    /// <exception cref="NotSupportedException">The codec is not a supported bitmap format, or FFmpeg is unavailable.</exception>
    public static IEnumerable<SubtitleBitmap> Decode(ISampleSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Config.Kind != TrackKind.Subtitle || source.Config.Codec is not (CodecType.Pgs or CodecType.VobSub or CodecType.DvbSub))
            throw new NotSupportedException($"{source.Config.FormatName} is not a bitmap subtitle format.");
        FFmpegLoader.EnsureAvailable();
        return DecodeIterator(source, cancellationToken);
    }

    private static IEnumerable<SubtitleBitmap> DecodeIterator(ISampleSource source, CancellationToken cancellationToken)
    {
        using var session = new Session(source);
        while (true)
        {
            var (ready, finished) = session.DecodeBatch(cancellationToken);
            foreach (var bitmap in ready)
                yield return bitmap;
            if (finished)
                yield break;
        }
    }

    /// <summary>Decoding state of one track.</summary>
    private sealed class Session : IDisposable
    {
        private readonly ISampleSource _source;
        private readonly SubtitleDecoderContext _decoder;
        private readonly double _timescale;
        /// <summary>Bitmaps on screen whose end is not known yet, with the end implied by their sample duration (or zero).</summary>
        private readonly List<(SubtitleBitmap Bitmap, TimeSpan FallbackEnd)> _pending = [];
        private byte[] _buffer = new byte[64 * 1024];

        public Session(ISampleSource source)
        {
            _source = source;
            _timescale = Math.Max(1u, source.Config.Timescale);
            _decoder = new SubtitleDecoderContext(source.Config);
            source.Reset();
        }

        public (List<SubtitleBitmap> Ready, bool Finished) DecodeBatch(CancellationToken cancellationToken)
        {
            var ready = new List<SubtitleBitmap>();
            for (var n = 0; n < BatchSize; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = _source.ReadNext();
                if (sample is null)
                {
                    // End of track: events without an end last until the fallback end.
                    var trackEnd = _source.Duration > TimeSpan.Zero ? _source.StartOffset + _source.Duration : TimeSpan.MaxValue;
                    foreach (var (p, fallback) in _pending)
                    {
                        var last = fallback > p.Start ? fallback : p.Start + s_defaultDuration;
                        if (last > trackEnd && trackEnd > p.Start)
                            last = trackEnd;
                        ready.Add(p with { End = last });
                    }

                    _pending.Clear();
                    return (ready, true);
                }

                var size = sample.Size;
                if (_buffer.Length < size)
                    _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
                sample.CopyTo(_buffer.AsSpan(0, size));
                var time = _source.StartOffset + TimeSpan.FromSeconds((sample.Pts - _source.MediaStart) / _timescale);
                var decoded = _decoder.Decode(_buffer.AsSpan(0, size), sample.Pts);
                if (decoded is null)
                    continue;

                var start = time + TimeSpan.FromMilliseconds(decoded.StartMs);

                // Any new event (including an empty "clear" one) ends the bitmaps still on screen.
                foreach (var (p, fallback) in _pending)
                {
                    var cleared = fallback > p.Start && fallback < start ? fallback : start;
                    ready.Add(p with { End = cleared > p.Start ? cleared : p.Start + TimeSpan.FromMilliseconds(1) });
                }

                _pending.Clear();

                // An event's own end (VobSub stop command, DVB page time-out) is an upper bound: the next event replaces
                // the screen if it comes first (DVB live subtitles update the page word by word).
                TimeSpan? end = decoded.EndMs is { } e ? time + TimeSpan.FromMilliseconds(e) : null;
                var sampleEnd = sample.Duration > 0 ? time + TimeSpan.FromSeconds(sample.Duration / _timescale) : TimeSpan.Zero;
                foreach (var r in decoded.Rects)
                {
                    var bitmap = new SubtitleBitmap(start, end ?? start, r.X, r.Y, r.Width, r.Height, r.Rgba, r.Forced, _decoder.CanvasWidth,
                        _decoder.CanvasHeight);
                    _pending.Add((bitmap, end ?? sampleEnd));
                }
            }

            return (ready, false);
        }

        public void Dispose() => _decoder.Dispose();
    }
}
