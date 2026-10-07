using MMW.Core.Media.Subtitles;

namespace MMW.Core.Tests;

/// <summary>Text subtitle formats through the common model: what each keeps of styles, layout and karaoke.</summary>
public sealed class SubtitleFormatTests
{
    private const string AssHeader = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 1280
        PlayResY: 720

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,2,2,20,20,30,1
        Style: Sign,@MS Gothic,40,&H0000FFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        """;

    private static SubtitleScript Ass() => AssFormat.ReadHeader(AssHeader);

    [Fact]
    public void Ass_header_gives_canvas_and_styles()
    {
        var script = Ass();
        Assert.Equal((1280, 720), (script.Width, script.Height));
        var sign = script.Style("Sign");
        Assert.True(sign.Vertical);
        Assert.Equal("MS Gothic", sign.Style.Font);
        Assert.Equal(new SubtitleColor(255, 255, 0), sign.Style.Primary); // &H0000FFFF is BGR yellow
        Assert.True(sign.Style.Bold);
        Assert.Equal(8, sign.Alignment);
        Assert.Equal(new SubtitleColor(0, 0, 0, 127), script.Style("Default").Style.Back);
    }

    [Fact]
    public void Ass_overrides_become_runs_position_and_karaoke()
    {
        var script = Ass();
        var e = AssFormat.ParseBlock(@"3,0,Default,Bob,0,0,0,,{\an7\pos(100,200)}{\k50}Hel{\kf30\b1\c&H0000FF&}lo\N{\fnTimes\fs60\i1}world", script, 1000, 3000);
        Assert.Equal("Hello\nworld", e.Text);
        Assert.Equal(7, e.Alignment);
        Assert.Equal((100.0, 200.0), e.Position);
        Assert.Equal("Bob", e.Speaker);
        Assert.Equal(2, e.Karaoke.Count);
        Assert.Equal(new KaraokeSyllable(0, 3, 0, 500, KaraokeKind.Fill), e.Karaoke[0]);
        Assert.Equal(KaraokeKind.Sweep, e.Karaoke[1].Kind);
        var lo = script.StyleAt(e, 3);
        Assert.True(lo.Bold);
        Assert.Equal(new SubtitleColor(255, 0, 0), lo.Primary);
        var world = script.StyleAt(e, 6);
        Assert.Equal(("Times", 60.0, true), (world.Font, world.Size!.Value, world.Italic!.Value));
    }

    [Fact]
    public void Ass_round_trips_through_its_writer()
    {
        var script = Ass();
        var block = @"0,0,Sign,,0,0,0,,{\an9\pos(640,100)}{\k25}ka{\k25}ra{\u1\4c&H00FF00&}oke";
        var e = AssFormat.ParseBlock(block, script, 0, 1000);
        script.Events.Add(e);
        var header = AssFormat.WriteHeader(script, ssa: false);
        var again = AssFormat.ReadHeader(header);
        var e2 = AssFormat.ParseBlock(AssFormat.WriteBlock(e, 0, script, ssa: false), again, 0, 1000);
        Assert.Equal(e.Text, e2.Text);
        Assert.Equal(e.Alignment, e2.Alignment);
        Assert.Equal(e.Position, e2.Position);
        Assert.Equal(e.Karaoke, e2.Karaoke);
        for (var i = 0; i < e.Text.Length; i++)
            Assert.Equal(script.StyleAt(e, i), again.StyleAt(e2, i));
        Assert.True(again.Style("Sign").Vertical);
    }

    [Fact]
    public void Ssa_uses_legacy_alignment_and_decimal_colours()
    {
        var script = Ass();
        var header = AssFormat.WriteHeader(script, ssa: true);
        Assert.Contains("[V4 Styles]", header, StringComparison.Ordinal);
        Assert.Contains("Style: Sign,@MS Gothic,40,65535,", header, StringComparison.Ordinal); // yellow as BGR decimal
        var again = AssFormat.ReadHeader(header);
        Assert.Equal(8, again.Style("Sign").Alignment); // written as legacy 6
        var e = AssFormat.ParseBlock(@"0,0,Default,,0,0,0,,{\a6}top", again, 0, 1);
        Assert.Equal(8, e.Alignment);
        Assert.Contains(@"{\a6}", AssFormat.WriteText(e with { Alignment = 8 }, again, ssa: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Srt_keeps_tags_colours_fonts_and_alignment()
    {
        var e = SrtFormat.Parse("{\\an8}<b>Bold</b> <font color=\"#ff0000\" face=\"Courier New\">red</font> <s>gone</s>", new SubtitleEvent());
        Assert.Equal("Bold red gone", e.Text);
        Assert.Equal(8, e.Alignment);
        var script = new SubtitleScript();
        script.Styles.Add(new SubtitleStyleSheet());
        Assert.True(script.StyleAt(e, 0).Bold);
        Assert.Equal((new SubtitleColor(255, 0, 0), "Courier New"), (script.StyleAt(e, 5).Primary!.Value, script.StyleAt(e, 5).Font));
        Assert.True(script.StyleAt(e, 9).Strikeout);
        var written = SrtFormat.Write(e, script);
        Assert.Equal("{\\an8}<b>Bold</b> <font color=\"#ff0000\" face=\"Courier New\">red</font> <s>gone</s>", written);
    }

    [Fact]
    public void WebVtt_reads_style_blocks_settings_and_karaoke()
    {
        var header = WebVttFormat.ReadHeader("WEBVTT\n\nSTYLE\n::cue { font-family: Verdana; color: #ffff00; }\n::cue(.red) { color: red; text-decoration: line-through; }");
        Assert.Equal(("Verdana", new SubtitleColor(255, 255, 0)), (header.Script.Style(null).Style.Font, header.Script.Style(null).Style.Primary!.Value));
        var e = WebVttFormat.Parse("<v Ann>Hi <c.red>there</c> <00:00:01.500>sing</v>", "vertical:rl line:10% position:20% align:start", header, 1000, 3000);
        Assert.Equal("Hi there sing", e.Text);
        Assert.Equal("Ann", e.Speaker);
        Assert.True(e.Vertical);
        Assert.Equal(7, e.Alignment);
        Assert.Equal((384.0, 108.0), e.Position);
        var red = header.Script.StyleAt(e, 3);
        Assert.Equal((new SubtitleColor(255, 0, 0), true), (red.Primary!.Value, red.Strikeout!.Value));
        Assert.Contains(e.Karaoke, k => k.Start == 9 && k.StartMs == 500 && k.DurationMs == 1500);
    }

    [Fact]
    public void WebVtt_writes_classes_for_what_it_has_no_tag_for()
    {
        var script = Ass();
        var e = AssFormat.ParseBlock(@"0,0,Default,,0,0,0,,{\an7\pos(128,72)}plain {\c&H0000FF&\fnCourier}red", script, 0, 2000);
        script.Events.Add(e);
        script.Rescale(WebVttFormat.CanvasWidth, WebVttFormat.CanvasHeight);
        e = script.Events[0];
        var classes = WebVttFormat.Classes(script);
        var header = WebVttFormat.WriteHeader(script, classes);
        Assert.Contains("::cue(.s1) { font-family: \"Courier\"; color: #ff0000; }", header, StringComparison.Ordinal);
        Assert.Equal("plain <c.s1>red</c>", WebVttFormat.WriteText(e, script, classes));
        Assert.Equal("position:10%,line-left line:10%,start align:start", WebVttFormat.WriteSettings(e, script));
    }

    [Fact]
    public void Tx3g_keeps_fonts_colours_karaoke_box_vertical_and_forced()
    {
        var script = Ass();
        var e = AssFormat.ParseBlock(@"0,0,Default,,0,0,0,,{\an7\pos(100,50)}{\kf40}Sing {\kf60\fnCourier\c&H00FF00&\b1}along", script, 0, 2000) with { Forced = true };
        script.Events.Add(e);
        script.Events.Add(AssFormat.ParseBlock(@"0,0,Sign,,0,0,0,,{\fn@MS Gothic}縦書き", script, 2000, 3000));
        var d = Tx3gFormat.Describe(script);
        Assert.Equal(1, d.Horizontal);
        Assert.Equal(-1, d.Vertical);
        Assert.NotEqual(0u, d.DisplayFlags & Tx3gFormat.ContinuousKaraoke);
        Assert.Contains("Courier", d.Fonts.Values);
        Assert.Contains("MS Gothic", d.Fonts.Values);

        var entry = Tx3gFormat.WriteDescription(d);
        var (read, readDescription) = Tx3gFormat.ReadDescription(entry, script.Width, script.Height);
        var sample = Tx3gFormat.WriteSample(e, script, d);
        var back = Tx3gFormat.ReadSample(sample, readDescription, read, 0, 2000, 1000);
        Assert.Equal("Sing along", back.Text);
        Assert.True(back.Forced);
        Assert.Equal(2, back.Karaoke.Count);
        Assert.Equal((0L, 400L, 400L, 600L), (back.Karaoke[0].StartMs, back.Karaoke[0].DurationMs, back.Karaoke[1].StartMs, back.Karaoke[1].DurationMs));
        var along = read.StyleAt(back, 6);
        Assert.Equal(("Courier", true), (along.Font, along.Bold!.Value));
        Assert.Equal(script.StyleAt(e, 6).Secondary, along.Secondary); // unsung colour drawn, sung colour highlighted
        Assert.NotNull(back.Position); // a text box places the top-left cue
    }

    [Fact]
    public void Rescaling_moves_positions_and_sizes()
    {
        var script = Ass();
        script.Events.Add(AssFormat.ParseBlock(@"0,0,Default,,0,0,0,,{\pos(640,360)}mid", script, 0, 1));
        script.Rescale(1920, 1080);
        Assert.Equal((960.0, 540.0), script.Events[0].Position);
        Assert.Equal(72, script.Style(null).Style.Size);
    }
}
