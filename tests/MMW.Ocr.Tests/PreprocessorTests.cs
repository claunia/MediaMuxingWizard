namespace MMW.Ocr.Tests;

/// <summary>Preparation of subtitle bitmaps for Tesseract (synthetic bitmaps).</summary>
public sealed class PreprocessorTests
{
    /// <summary>A white bar with a 2-pixel black outline on a transparent background.</summary>
    private static RgbaImage OutlinedBar(int width, int height, int barWidth, int barHeight, int x = 10, int y = 10)
    {
        var image = RgbaImage.Blank(width, height);
        image.Fill(x - 2, y - 2, barWidth + 4, barHeight + 4, 0, 0, 0);
        image.Fill(x, y, barWidth, barHeight, 255, 255, 255);
        return image;
    }

    [Fact]
    public void Transparent_and_empty_bitmaps_are_blank()
    {
        Assert.True(OcrPreprocessor.Prepare(RgbaImage.Blank(50, 20).Pixels, 50, 20).IsBlank);
        Assert.True(OcrPreprocessor.Prepare([], 0, 0).IsBlank);

        // Only a black outline: no bright ink.
        var outline = RgbaImage.Blank(50, 20).Fill(5, 5, 30, 8, 0, 0, 0);
        Assert.True(OcrPreprocessor.Prepare(outline.Pixels, 50, 20).IsBlank);
    }

    [Fact]
    public void Buffer_smaller_than_the_dimensions_is_rejected() =>
        Assert.Throws<ArgumentException>(() => OcrPreprocessor.Prepare(new byte[10], 5, 5));

    [Fact]
    public void Bright_text_becomes_dark_on_white_cropped_padded_and_enlarged()
    {
        // 30×10 white bar (a 10 px high "line") inside a 100×40 bitmap: ×3 enlargement, outline dropped.
        var image = OutlinedBar(100, 40, 30, 10, x: 20, y: 15);
        var prepared = OcrPreprocessor.Prepare(image.Pixels, image.Width, image.Height);

        Assert.False(prepared.IsBlank);
        Assert.Equal(1, prepared.Lines);
        Assert.Equal(OcrLayout.SingleLine, prepared.Layout);
        const int scale = 3;
        var pad = Math.Max(10, 10 * scale / 3);
        Assert.Equal((30 * scale) + (2 * pad), prepared.Width);
        Assert.Equal((10 * scale) + (2 * pad), prepared.Height);

        // Text is black, the margin and the (dropped) outline are white.
        Assert.Equal(0, prepared.At(prepared.Width / 2, prepared.Height / 2));
        Assert.Equal(255, prepared.At(1, 1));
        Assert.Equal(255, prepared.At(pad - 2, prepared.Height / 2));
        Assert.Equal(prepared.Width * prepared.Height, prepared.Pixels.Length);
    }

    [Fact]
    public void Contrast_is_normalised_and_the_noise_floor_removed()
    {
        // Mid-grey text (128) with a faint (20) halo: the text becomes full black, the halo disappears.
        var image = RgbaImage.Blank(60, 50);
        image.Fill(5, 5, 50, 40, 20, 20, 20);
        image.Fill(10, 10, 40, 30, 128, 128, 128);
        var prepared = OcrPreprocessor.Prepare(image.Pixels, image.Width, image.Height);

        Assert.Equal(1, prepared.Lines);
        Assert.Equal(2, OcrPreprocessor.ScaleFor(30)); // 30 px line → ×2
        Assert.Equal(0, prepared.At(prepared.Width / 2, prepared.Height / 2));

        // Cropped to the text only (the halo is below the noise floor): 40×30 enlarged ×2 plus the margins.
        var pad = Math.Max(10, 30 * 2 / 3);
        Assert.Equal((40 * 2) + (2 * pad), prepared.Width);
    }

    [Fact]
    public void Semi_transparent_ink_is_composited_over_black()
    {
        var opaque = RgbaImage.Blank(40, 20).Fill(5, 5, 30, 10, 255, 255, 255);
        var faint = RgbaImage.Blank(40, 20).Fill(5, 5, 30, 10, 255, 255, 255, 16);
        Assert.False(OcrPreprocessor.Prepare(opaque.Pixels, 40, 20).IsBlank);

        // 255 × 16/255 = 16 < the minimum ink: nothing to read.
        Assert.True(OcrPreprocessor.Prepare(faint.Pixels, 40, 20).IsBlank);
    }

    [Fact]
    public void Two_lines_use_block_layout_and_accents_join_their_line()
    {
        var image = RgbaImage.Blank(80, 70);
        image.Fill(10, 5, 60, 20, 255, 255, 255);
        image.Fill(10, 40, 60, 20, 255, 255, 255);
        var two = OcrPreprocessor.Prepare(image.Pixels, image.Width, image.Height);
        Assert.Equal(2, two.Lines);
        Assert.Equal(OcrLayout.Block, two.Layout);

        // A small mark (an accent or the dot of an i) above one line is not a line of its own.
        var accented = RgbaImage.Blank(80, 50);
        accented.Fill(30, 4, 4, 3, 255, 255, 255);
        accented.Fill(10, 10, 60, 20, 255, 255, 255);
        var one = OcrPreprocessor.Prepare(accented.Pixels, accented.Width, accented.Height);
        Assert.Equal(1, one.Lines);
        Assert.Equal(OcrLayout.SingleLine, one.Layout);
    }

    [Fact]
    public void One_pixel_gaps_do_not_split_lines()
    {
        var image = RgbaImage.Blank(40, 40);
        image.Fill(5, 5, 30, 10, 255, 255, 255);
        image.Fill(5, 16, 30, 10, 255, 255, 255); // one empty row between
        Assert.Equal(1, OcrPreprocessor.Prepare(image.Pixels, 40, 40).Lines);
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(19, 3)]
    [InlineData(20, 2)]
    [InlineData(39, 2)]
    [InlineData(40, 1)]
    [InlineData(120, 1)]
    public void Small_text_is_enlarged(int lineHeight, int expected) => Assert.Equal(expected, OcrPreprocessor.ScaleFor(lineHeight));

    [Fact]
    public void Dark_text_with_a_light_outline_is_read_in_dark_luminance_mode()
    {
        // Black bar with a white outline: luminance mode sees the outline ring; dark mode sees the bar.
        var image = RgbaImage.Blank(60, 40);
        image.Fill(8, 8, 44, 24, 255, 255, 255);
        image.Fill(10, 10, 40, 20, 0, 0, 0);
        var dark = OcrPreprocessor.Prepare(image.Pixels, 60, 40, InkMode.DarkLuminance);
        Assert.Equal(0, dark.At(dark.Width / 2, dark.Height / 2));

        var bright = OcrPreprocessor.Prepare(image.Pixels, 60, 40);
        Assert.Equal(255, bright.At(bright.Width / 2, bright.Height / 2)); // hollow: the centre is background

        var alpha = OcrPreprocessor.Prepare(image.Pixels, 60, 40, InkMode.Alpha);
        Assert.Equal(0, alpha.At(alpha.Width / 2, alpha.Height / 2));
    }

    [Fact]
    public void Vobsub_four_colour_bitmaps_keep_the_fill()
    {
        // Typical DVD palette: white fill, black outline, grey anti-aliasing between them.
        var image = RgbaImage.Blank(70, 30);
        image.Fill(3, 3, 64, 24, 0, 0, 0);
        image.Fill(5, 5, 60, 20, 128, 128, 128);
        image.Fill(6, 6, 58, 18, 255, 255, 255);
        var prepared = OcrPreprocessor.Prepare(image.Pixels, 70, 30);

        // The grey ring (at the 50 % floor) and the outline are dropped: only the 58×18 fill remains, enlarged ×3.
        Assert.Equal(0, prepared.At(prepared.Width / 2, prepared.Height / 2));
        var pad = Math.Max(10, 18 * 3 / 3);
        Assert.Equal((58 * 3) + (2 * pad), prepared.Width);
        Assert.Equal((18 * 3) + (2 * pad), prepared.Height);
    }

    [Fact]
    public void Ink_polarity_is_detected_from_the_outline()
    {
        // White fill, black outline (Blu-ray / most DVDs).
        var bright = RgbaImage.Blank(40, 20);
        bright.Fill(2, 2, 36, 16, 0, 0, 0);
        bright.Fill(4, 4, 32, 12, 255, 255, 255);
        Assert.Equal(InkMode.Luminance, OcrPreprocessor.DetectInkMode(bright.Pixels, 40, 20));

        // Black fill, white outline (some DVD palettes).
        var dark = RgbaImage.Blank(40, 20);
        dark.Fill(2, 2, 36, 16, 255, 255, 255);
        dark.Fill(4, 4, 32, 12, 0, 0, 0);
        Assert.Equal(InkMode.DarkLuminance, OcrPreprocessor.DetectInkMode(dark.Pixels, 40, 20));

        // No outline: bright text stays in luminance mode; degenerate input too.
        Assert.Equal(InkMode.Luminance, OcrPreprocessor.DetectInkMode(RgbaImage.Blank(40, 20).Fill(4, 4, 32, 12, 255, 255, 255).Pixels, 40, 20));
        Assert.Equal(InkMode.Luminance, OcrPreprocessor.DetectInkMode([], 0, 0));
    }

    [Fact]
    public void Pgm_export_has_a_header_and_the_pixels()
    {
        var prepared = OcrPreprocessor.Prepare(OutlinedBar(60, 30, 20, 8).Pixels, 60, 30);
        var pgm = prepared.ToPgm();
        Assert.StartsWith($"P5\n{prepared.Width} {prepared.Height}\n255\n", System.Text.Encoding.ASCII.GetString(pgm, 0, 20), StringComparison.Ordinal);
        Assert.Equal(prepared.Pixels.Length, pgm.Length - pgm.AsSpan().IndexOf("255\n"u8) - 4);
    }
}
