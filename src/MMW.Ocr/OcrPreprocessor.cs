namespace MMW.Ocr;

/// <summary>How the "ink" (text) of a subtitle bitmap is derived from its colours.</summary>
public enum InkMode
{
    /// <summary>
    /// Brightness composited over black: bright text with a dark outline (the usual DVD/Blu-ray style) keeps the text
    /// and drops the outline.
    /// </summary>
    Luminance,

    /// <summary>Darkness of the opaque pixels: dark text with a light outline.</summary>
    DarkLuminance,

    /// <summary>Opacity only: the silhouette of text and outline (a fallback for unusual palettes).</summary>
    Alpha,
}

/// <summary>
/// Turns a subtitle bitmap (straight-alpha RGBA) into the grayscale image Tesseract reads best: ink isolated from the
/// outline and background, contrast normalised, cropped to the text, dark text on white, small text enlarged and a
/// white margin added.
/// </summary>
public static class OcrPreprocessor
{
    /// <summary>
    /// Ink up to this fraction of the brightest ink is background: outline, drop shadow, the grey "emboss" colour of
    /// DVD palettes and the outer half of anti-aliasing.
    /// </summary>
    private const double NoiseFloor = 0.5;

    /// <summary>Normalised ink below this value is dropped (faint residue just above the floor).</summary>
    private const int MinimumNormalisedInk = 16;

    /// <summary>Ink values (0–255) under which a bitmap is considered empty.</summary>
    private const int MinimumInk = 24;

    /// <summary>Line runs shorter than this fraction of the tallest line are accents/dots and merged into a neighbour.</summary>
    private const double MinimumLineFraction = 0.4;

    /// <summary>Prepares <paramref name="rgba"/> (<paramref name="width"/>×<paramref name="height"/>, 4 bytes per pixel) for OCR.</summary>
    /// <exception cref="ArgumentException">The buffer is smaller than the dimensions.</exception>
    public static OcrImage Prepare(ReadOnlySpan<byte> rgba, int width, int height, InkMode mode = InkMode.Luminance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException("The RGBA buffer is smaller than the bitmap dimensions.", nameof(rgba));
        if (width == 0 || height == 0)
            return Blank();

        var ink = ExtractInk(rgba, width, height, mode);
        if (!Normalize(ink))
            return Blank();

        var (left, top, right, bottom) = BoundingBox(ink, width, height);
        if (right < left)
            return Blank();

        var cropW = right - left + 1;
        var cropH = bottom - top + 1;
        var lines = FindLines(ink, width, left, top, cropW, cropH);
        if (lines.Count == 0)
            return Blank();

        var lineHeight = lines.Max(l => l.End - l.Start);
        var scale = ScaleFor(lineHeight);
        var scaledW = cropW * scale;
        var scaledH = cropH * scale;
        var pad = Math.Max(10, (lineHeight * scale) / 3);
        var outW = scaledW + (2 * pad);
        var outH = scaledH + (2 * pad);
        var output = new byte[outW * outH];
        Array.Fill(output, (byte)255);
        for (var y = 0; y < scaledH; y++)
        {
            var row = (y + pad) * outW + pad;
            for (var x = 0; x < scaledW; x++)
                output[row + x] = (byte)(255 - Sample(ink, width, left, top, cropW, cropH, x, y, scale));
        }

        return new OcrImage(outW, outH, output, lines.Count, lines.Count == 1 ? OcrLayout.SingleLine : OcrLayout.Block);
    }

    /// <summary>
    /// Guesses whether the text of a bitmap is bright with a dark outline (<see cref="InkMode.Luminance"/>, the usual
    /// case) or dark with a light outline (<see cref="InkMode.DarkLuminance"/>, e.g. some DVD palettes): an outline
    /// surrounds the fill, so the opaque pixels touching transparency are compared with the enclosed ones.
    /// </summary>
    public static InkMode DetectInkMode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4)
            return InkMode.Luminance;
        long edgeSum = 0, edgeCount = 0, innerSum = 0, innerCount = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = ((y * width) + x) * 4;
                if (rgba[p + 3] < 128)
                    continue;
                var lum = ((77 * rgba[p]) + (150 * rgba[p + 1]) + (29 * rgba[p + 2])) >> 8;
                var edge = x == 0 || y == 0 || x == width - 1 || y == height - 1 ||
                           rgba[p - 4 + 3] < 128 || rgba[p + 4 + 3] < 128 || rgba[p - (width * 4) + 3] < 128 || rgba[p + (width * 4) + 3] < 128;
                if (edge)
                {
                    edgeSum += lum;
                    edgeCount++;
                }
                else
                {
                    innerSum += lum;
                    innerCount++;
                }
            }
        }

        if (edgeCount == 0 || innerCount == 0)
            return InkMode.Luminance;
        return (innerSum / (double)innerCount) + 16 < edgeSum / (double)edgeCount ? InkMode.DarkLuminance : InkMode.Luminance;
    }

    /// <summary>Text lines (row ranges, relative to the crop) of an ink image, merging accents and dots into their line.</summary>
    internal static List<(int Start, int End)> FindLines(byte[] ink, int stride, int left, int top, int width, int height)
    {
        var runs = new List<(int Start, int End)>();
        var start = -1;
        for (var y = 0; y <= height; y++)
        {
            var has = false;
            if (y < height)
            {
                var row = (top + y) * stride + left;
                for (var x = 0; x < width && !has; x++)
                    has = ink[row + x] != 0;
            }

            if (has && start < 0)
                start = y;
            else if (!has && start >= 0)
            {
                // A one-row gap is anti-aliasing between touching lines or a broken glyph, not a line break.
                if (runs.Count > 0 && start - runs[^1].End <= 1)
                    runs[^1] = (runs[^1].Start, y);
                else
                    runs.Add((start, y));
                start = -1;
            }
        }

        // Short runs (diacritics above capitals, dots, underlines, stray specks) belong to the nearest line.
        while (runs.Count > 1)
        {
            var tallest = runs.Max(r => r.End - r.Start);
            var index = runs.FindIndex(r => r.End - r.Start < tallest * MinimumLineFraction);
            if (index < 0)
                break;
            var r = runs[index];
            var prevGap = index > 0 ? r.Start - runs[index - 1].End : int.MaxValue;
            var nextGap = index + 1 < runs.Count ? runs[index + 1].Start - r.End : int.MaxValue;
            if (prevGap <= nextGap)
            {
                runs[index - 1] = (runs[index - 1].Start, r.End);
                runs.RemoveAt(index);
            }
            else
            {
                runs[index + 1] = (r.Start, runs[index + 1].End);
                runs.RemoveAt(index);
            }
        }

        return runs;
    }

    /// <summary>Integer enlargement for a text line height: ×3 under 20 px, ×2 under 40 px.</summary>
    internal static int ScaleFor(int lineHeight) => lineHeight switch
    {
        < 20 => 3,
        < 40 => 2,
        _ => 1,
    };

    private static OcrImage Blank() => new(0, 0, [], 0, OcrLayout.Block);

    private static byte[] ExtractInk(ReadOnlySpan<byte> rgba, int width, int height, InkMode mode)
    {
        var ink = new byte[width * height];
        for (int i = 0, p = 0; i < ink.Length; i++, p += 4)
        {
            int a = rgba[p + 3];
            if (a == 0)
                continue;

            // Rec. 601 luma in 8.8 fixed point.
            var lum = ((77 * rgba[p]) + (150 * rgba[p + 1]) + (29 * rgba[p + 2])) >> 8;
            ink[i] = mode switch
            {
                InkMode.Luminance => (byte)(lum * a / 255),
                InkMode.DarkLuminance => (byte)((255 - lum) * a / 255),
                _ => (byte)a,
            };
        }

        return ink;
    }

    /// <summary>Removes the noise floor and stretches the ink to 0–255; false when there is no ink.</summary>
    private static bool Normalize(byte[] ink)
    {
        // The brightest ink, ignoring the top 0.1 % (isolated bright specks).
        Span<int> histogram = stackalloc int[256];
        var count = 0;
        foreach (var v in ink)
        {
            if (v == 0)
                continue;
            histogram[v]++;
            count++;
        }

        if (count == 0)
            return false;
        var skip = count / 1000;
        var max = 255;
        for (var acc = 0; max > 0; max--)
        {
            acc += histogram[max];
            if (acc > skip)
                break;
        }

        if (max < MinimumInk)
            return false;

        var floor = (int)(max * NoiseFloor);
        var range = Math.Max(1, max - floor);
        for (var i = 0; i < ink.Length; i++)
        {
            var v = ink[i];
            var n = v <= floor ? 0 : Math.Min(255, (v - floor) * 255 / range);
            ink[i] = n < MinimumNormalisedInk ? (byte)0 : (byte)n;
        }

        return true;
    }

    private static (int Left, int Top, int Right, int Bottom) BoundingBox(byte[] ink, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (ink[row + x] == 0)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return (left, top, right, bottom);
    }

    /// <summary>Bilinear sample of the crop at output pixel (<paramref name="x"/>, <paramref name="y"/>) of a ×<paramref name="scale"/> enlargement.</summary>
    private static int Sample(byte[] ink, int stride, int left, int top, int width, int height, int x, int y, int scale)
    {
        if (scale == 1)
            return ink[(top + y) * stride + left + x];

        // Pixel centres: output (x + 0.5) / scale - 0.5 in source coordinates.
        var sx = ((x * 2) + 1 - scale) / (2.0 * scale);
        var sy = ((y * 2) + 1 - scale) / (2.0 * scale);
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        double At(int px, int py)
        {
            px = Math.Clamp(px, 0, width - 1);
            py = Math.Clamp(py, 0, height - 1);
            return ink[(top + py) * stride + left + px];
        }

        var v = (At(x0, y0) * (1 - fx) * (1 - fy)) + (At(x0 + 1, y0) * fx * (1 - fy)) + (At(x0, y0 + 1) * (1 - fx) * fy) + (At(x0 + 1, y0 + 1) * fx * fy);
        return (int)Math.Round(v);
    }
}
