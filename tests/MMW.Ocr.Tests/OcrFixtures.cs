using System.Text;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Conversion;
using MMW.Media.Remux;
using MMW.Ocr.Interop;
using MMW.TestSupport;

namespace MMW.Ocr.Tests;

/// <summary>An RGBA image (straight alpha, 4 bytes per pixel).</summary>
internal sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public static RgbaImage Blank(int width, int height) => new(width, height, new byte[width * height * 4]);

    public void Set(int x, int y, byte r, byte g, byte b, byte a)
    {
        var p = ((y * Width) + x) * 4;
        Pixels[p] = r;
        Pixels[p + 1] = g;
        Pixels[p + 2] = b;
        Pixels[p + 3] = a;
    }

    /// <summary>Fills a rectangle.</summary>
    public RgbaImage Fill(int x, int y, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        for (var yy = y; yy < y + h; yy++)
        {
            for (var xx = x; xx < x + w; xx++)
                Set(xx, yy, r, g, b, a);
        }

        return this;
    }

    public SubtitleBitmap ToBitmap(double start, double end, bool forced = false, int x = 0, int y = 0) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), x, y, Width, Height, Pixels, forced, 720, 480);
}

/// <summary>An in-memory track.</summary>
internal sealed class MemorySampleSource(CodecConfig config, IReadOnlyList<MediaSample> samples) : ISampleSource
{
    private int _next;

    public uint TrackId => 1;

    public CodecConfig Config { get; } = config;

    public TimeSpan StartOffset => TimeSpan.Zero;

    public long MediaStart => 0;

    public TimeSpan Duration { get; init; }

    public long SampleCountHint => samples.Count;

    public MediaSample? ReadNext() => _next < samples.Count ? samples[_next++].Clone() : null;

    public void Reset() => _next = 0;
}

/// <summary>
/// An OCR engine that answers from a function of the image (and records the calls), for testing the pipeline without
/// Tesseract.
/// </summary>
internal sealed class FakeOcrEngine(Func<OcrImage, int, OcrResult> answer) : IOcrEngine
{
    public List<OcrImage> Images { get; } = [];

    public bool Disposed { get; private set; }

    public string Languages => "eng";

    public OcrResult Recognize(OcrImage image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);
        Images.Add(image);
        return answer(image, Images.Count - 1);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Real-OCR prerequisites, rendered text, synthetic VobSub tracks and files.</summary>
internal static class OcrFixtures
{
    static OcrFixtures() => MediaRemux.EnsureRegistered();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lazy<string?> s_englishDirectory = new(FindOrDownloadEnglish);

    /// <summary>Skips the test unless libtesseract is loaded and an English model is available.</summary>
    public static string RequireTesseract()
    {
        if (!TesseractLoader.IsAvailable)
            Assert.Skip($"libtesseract is not available: {TesseractLoader.Error}");
        return s_englishDirectory.Value ?? throw SkipException("No eng.traineddata found and it could not be downloaded.");
    }

    /// <summary>Skips the test unless FFmpeg is loaded.</summary>
    public static void RequireFfmpeg()
    {
        if (!MediaConversion.IsAvailable)
            Assert.Skip($"FFmpeg libraries not available: {MediaConversion.Error}");
    }

    private static InvalidOperationException SkipException(string reason)
    {
        Assert.Skip(reason);
        return new InvalidOperationException(reason);
    }

    /// <summary>A directory with eng.traineddata: installed (system/bundled), else downloaded into a cache folder.</summary>
    private static string? FindOrDownloadEnglish()
    {
        var system = new TessdataManager(Path.Combine(Path.GetTempPath(), "mmw-ocr-tests", "tessdata"));
        if (system.ResolveDataDirectory("eng") is { } dir)
            return dir;
        try
        {
            system.DownloadAsync("eng").GetAwaiter().GetResult();
            return system.ResolveDataDirectory("eng");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> as a subtitle (white DejaVu Sans with a black outline on a transparent
    /// background) with ImageMagick; skips the test when ImageMagick is not installed.
    /// </summary>
    public static RgbaImage RenderText(string text, int pointSize = 28, string fill = "white", string stroke = "black")
    {
        var tool = Fixtures.HasTool("magick") ? "magick" : Fixtures.HasTool("convert") ? "convert" : null;
        if (tool is null)
            Assert.Skip("ImageMagick is not installed.");
        var lines = text.Split('\n');
        var width = (int)(pointSize * 0.75 * lines.Max(l => l.Length)) + 40;
        var height = (int)(pointSize * 1.5 * lines.Length) + 16;
        var file = Path.Combine(Path.GetTempPath(), "mmw-ocr-" + Guid.NewGuid().ToString("N") + ".rgba");
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        try
        {
            Fixtures.Run(tool, $"-size {width}x{height} xc:none -font DejaVu-Sans -pointsize {pointSize} -gravity center " +
                               $"-fill {fill} -stroke {stroke} -strokewidth 3 -annotate +0+0 \"{escaped}\" -stroke none -annotate +0+0 \"{escaped}\" " +
                               $"-depth 8 rgba:{Fixtures.Quote(file)}");
            return new RgbaImage(width, height, File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------ VobSub

    /// <summary>.idx header: 720×480, palette 1 = white (fill), 2 = black (outline), 3 = grey (anti-aliasing).</summary>
    public static byte[] VobSubIdx()
    {
        var palette = string.Join(", ", Enumerable.Range(0, 16).Select(i => i switch { 1 => "ffffff", 3 => "808080", _ => "000000" }));
        return Encoding.ASCII.GetBytes($"# VobSub index file, v7 (do not modify this line!)\nsize: 720x480\npalette: {palette}\n");
    }

    public static CodecConfig VobSubConfig(string language = "en") => new()
    {
        Codec = CodecType.VobSub, Kind = TrackKind.Subtitle, Timescale = 1000, SourceCodecId = "S_VOBSUB", Extradata = VobSubIdx(),
        SubtitleWidth = 720, SubtitleHeight = 480, Language = language,
    };

    /// <summary>Quantises an RGBA image to the 4 VobSub colours (0 transparent, 1 fill, 2 outline, 3 anti-aliasing).</summary>
    public static byte[] Quantise(RgbaImage image)
    {
        var indexes = new byte[image.Width * image.Height];
        for (var i = 0; i < indexes.Length; i++)
        {
            var p = image.Pixels.AsSpan(i * 4, 4);
            if (p[3] < 96)
                continue;
            var lum = ((77 * p[0]) + (150 * p[1]) + (29 * p[2])) >> 8;
            indexes[i] = lum > 170 ? (byte)1 : lum < 85 ? (byte)2 : (byte)3;
        }

        return indexes;
    }

    /// <summary>A DVD subpicture showing <paramref name="image"/> at (<paramref name="x"/>, <paramref name="y"/>) for <paramref name="durationMs"/>.</summary>
    public static byte[] Spu(RgbaImage image, int x, int y, int durationMs, bool forced)
    {
        var indexes = Quantise(image);
        var top = new NibbleWriter();
        for (var line = 0; line < image.Height; line += 2)
            EncodeLine(top, indexes.AsSpan(line * image.Width, image.Width));
        var bottom = new NibbleWriter();
        for (var line = 1; line < image.Height; line += 2)
            EncodeLine(bottom, indexes.AsSpan(line * image.Width, image.Width));
        var pixels = new List<byte>();
        pixels.AddRange(top.Bytes);
        var bottomOffset = 4 + pixels.Count;
        pixels.AddRange(bottom.Bytes);

        var ctrl = 4 + pixels.Count;
        var x2 = x + image.Width - 1;
        var y2 = y + image.Height - 1;
        var seq1 = new List<byte> { 0, 0, 0, 0 };
        if (forced)
            seq1.Add(0x00); // forced start display
        seq1.Add(0x01); // start display
        seq1.AddRange([0x03, 0x32, 0x10]);
        seq1.AddRange([0x04, 0xFF, 0xF0]);
        seq1.AddRange([0x05, (byte)(x >> 4), (byte)(((x & 0xF) << 4) | (x2 >> 8)), (byte)x2, (byte)(y >> 4), (byte)(((y & 0xF) << 4) | (y2 >> 8)), (byte)y2]);
        seq1.AddRange([0x06, 0x00, 0x04, (byte)(bottomOffset >> 8), (byte)bottomOffset]);
        seq1.Add(0xFF);
        var seq2Offset = ctrl + seq1.Count;
        seq1[2] = (byte)(seq2Offset >> 8);
        seq1[3] = (byte)seq2Offset;
        var date = (int)Math.Round(durationMs * 90.0 / 1024);
        List<byte> seq2 = [(byte)(date >> 8), (byte)date, (byte)(seq2Offset >> 8), (byte)seq2Offset, 0x02, 0xFF];

        var total = ctrl + seq1.Count + seq2.Count;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException("The subpicture is too large.");
        var spu = new List<byte> { (byte)(total >> 8), (byte)total, (byte)(ctrl >> 8), (byte)ctrl };
        spu.AddRange(pixels);
        spu.AddRange(seq1);
        spu.AddRange(seq2);
        return [.. spu];
    }

    private static void EncodeLine(NibbleWriter w, ReadOnlySpan<byte> line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var n = 1;
            while (i + n < line.Length && line[i + n] == c && n < 255)
                n++;
            if (i + n == line.Length)
            {
                // Run to the end of the line.
                w.Put(0);
                w.Put(0);
                w.Put(0);
                w.Put(c);
            }
            else if (n < 4)
            {
                w.Put((n << 2) | c);
            }
            else if (n < 16)
            {
                w.Put(n >> 2);
                w.Put(((n & 3) << 2) | c);
            }
            else if (n < 64)
            {
                w.Put(0);
                w.Put(n >> 2);
                w.Put(((n & 3) << 2) | c);
            }
            else
            {
                w.Put(0);
                w.Put(n >> 6);
                w.Put((n >> 2) & 0xF);
                w.Put(((n & 3) << 2) | c);
            }

            i += n;
        }

        w.Align();
    }

    private sealed class NibbleWriter
    {
        private bool _half;

        public List<byte> Bytes { get; } = [];

        public void Put(int nibble)
        {
            if (_half)
                Bytes[^1] |= (byte)(nibble & 0xF);
            else
                Bytes.Add((byte)((nibble & 0xF) << 4));
            _half = !_half;
        }

        public void Align() => _half = false;
    }

    public static MediaSample Sample(long ms, byte[] data) => new() { Dts = ms, Data = data, IsSync = true };

    /// <summary>
    /// Writes a Matroska file with one VobSub track showing each (text, start, duration, forced) cue, with the
    /// application's own muxer.
    /// </summary>
    public static string WriteVobSubMkv(IReadOnlyList<(string Text, int StartMs, int DurationMs, bool Forced)> cues, string language = "en")
    {
        var path = Path.Combine(Path.GetTempPath(), "mmw-tests", "ocr-" + Guid.NewGuid().ToString("N") + ".mks");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var factory = MediaFormatRegistry.GetMuxer(ContainerKind.Matroska)!;
        var document = new MediaDocument(null, ContainerKind.Matroska) { Duration = TimeSpan.FromMilliseconds(cues.Max(c => c.StartMs + c.DurationMs)) };
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var muxer = factory.Create(stream, new MuxerSettings { Document = document, OutputPath = path }))
        {
            var model = new SubtitleTrack { Language = language, Name = "DVD subtitles" };
            var index = muxer.AddTrack(VobSubConfig(language), new MuxTrackSettings { Model = model });
            foreach (var (text, start, duration, forced) in cues)
            {
                var image = RenderText(text);
                muxer.WriteSample(index, new MediaSample { Dts = start, Duration = duration, IsSync = true, Data = Spu(image, 40, 380, duration, forced) });
            }

            muxer.Finish(Ct);
        }

        return path;
    }
}
