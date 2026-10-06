using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.TestSupport;

namespace MMW.Formats.Elementary.Tests;

public sealed class SubtitleFileTests
{
    [Fact]
    public void SubRip_cues_are_parsed_and_sorted()
    {
        const string srt = "﻿2\r\n00:00:03,000 --> 00:00:04,500\r\n<i>Second</i>\r\nline two\r\n\r\n1\r\n00:00:01,000 --> 00:00:02,000\r\nFirst\r\n";
        var file = SubtitleFiles.ParseSrt(srt);
        Assert.Equal(CodecType.TextUtf8, file.Config.Codec);
        Assert.Equal(1000u, file.Config.Timescale);
        Assert.Equal([(1000L, 2000L, "First"), (3000L, 4500L, "<i>Second</i>\nline two")], file.Cues);
    }

    [Fact]
    public void Ass_events_become_matroska_blocks()
    {
        const string ass = """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize
            Style: Default,Arial,20

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.50,0:00:04.00,Default,,0,0,0,,{\i1}Hello{\i0}, world
            Comment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,ignored
            Dialogue: 1,0:00:01.00,0:00:02.00,Default,Bob,0,0,0,,First\Nline
            """;
        var file = SubtitleFiles.ParseAss(ass);
        Assert.Equal(CodecType.Ass, file.Config.Codec);
        var header = System.Text.Encoding.UTF8.GetString(file.Config.Extradata!);
        Assert.Contains("[V4+ Styles]", header, StringComparison.Ordinal);
        Assert.Contains("Format: Layer, Start, End", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Dialogue", header, StringComparison.Ordinal);
        Assert.Equal(2, file.Cues.Count);
        Assert.Equal((1000L, 2000L, "1,1,Default,Bob,0,0,0,,First\\Nline"), file.Cues[0]);
        Assert.Equal((2500L, 4000L, "0,0,Default,,0,0,0,,{\\i1}Hello{\\i0}, world"), file.Cues[1]);

        var styled = SubtitleText.FromAss(SubtitleText.AssBlockText(file.Cues[1].Text, ssa: false));
        Assert.Equal("Hello, world", styled.Text);
        Assert.Equal([new StyleRun(0, 5, TextStyle.Italic)], styled.Runs);
    }

    [Fact]
    public void WebVtt_cues_and_header()
    {
        const string vtt = "WEBVTT\n\nSTYLE\n::cue { color: red }\n\nid1\n00:01.000 --> 00:02.000 line:0\n<b>Bold</b> &amp; plain\n\n00:00:03.000 --> 00:00:04.000\nNext\n";
        var file = SubtitleFiles.ParseWebVtt(vtt);
        Assert.Equal(CodecType.WebVtt, file.Config.Codec);
        Assert.StartsWith("WEBVTT", System.Text.Encoding.UTF8.GetString(file.Config.Extradata!), StringComparison.Ordinal);
        Assert.Equal(2, file.Cues.Count);
        Assert.Equal((1000L, 2000L, "<b>Bold</b> &amp; plain"), file.Cues[0]);
        var styled = SubtitleText.FromWebVtt(file.Cues[0].Text);
        Assert.Equal("Bold & plain", styled.Text);
        Assert.Equal([new StyleRun(0, 4, TextStyle.Bold)], styled.Runs);
    }

    [Fact]
    public void Tx3g_round_trip_keeps_styles()
    {
        var text = SubtitleText.FromSrt("<b>Bold</b> and <i>italic é</i>");
        var sample = SubtitleText.ToTx3g(text, 18);
        var back = SubtitleText.FromTx3g(sample);
        Assert.Equal(text.Text, back.Text);
        Assert.Equal(text.Runs, back.Runs);
        Assert.Equal("<b>Bold</b> and <i>italic é</i>", SubtitleText.ToSrt(back));
    }

    [Fact]
    public void Timeline_merges_overlaps_and_fills_gaps()
    {
        var timeline = new SubtitleTimeline();
        var output = new List<SubtitleCue>();
        output.AddRange(timeline.Add(new SubtitleCue(1000, 3000, new StyledText("A", []))));
        output.AddRange(timeline.Add(new SubtitleCue(2000, 4000, new StyledText("B", []))));
        output.AddRange(timeline.Add(new SubtitleCue(5000, 6000, new StyledText("C", []))));
        output.AddRange(timeline.Complete());
        Assert.Equal(
            [(0L, 1000L, ""), (1000L, 2000L, "A"), (2000L, 3000L, "A\nB"), (3000L, 4000L, "B"), (4000L, 5000L, ""), (5000L, 6000L, "C")],
            output.Select(c => (c.Start, c.End, c.Text.Text)));
    }
}

public sealed class ElementaryStreamTests
{
    private static string Make(string name, string args)
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get(name, "ffmpeg", "-v error -y " + args + " {out}");
    }

    [Fact]
    public void Adts_frames_are_unwrapped()
    {
        var path = Make("elem-adts.aac", "-f lavfi -i sine=f=440:d=2 -ac 2 -c:a aac -f adts");
        using var demuxer = ElementaryFormat.Open(path);
        var track = Assert.Single(demuxer.Tracks);
        Assert.Equal(CodecType.Aac, track.Config.Codec);
        Assert.Equal(2, track.Config.Channels);
        Assert.Equal(44100, track.Config.SampleRate);
        Assert.Equal(new AacConfig(2, 44100, 2, 0, 1024), Aac.ParseConfig(track.Config.Extradata));

        // ffmpeg's ADTS demuxer keeps the headers; aac_adtstoasc removes them like the importer does.
        var hashes = MediaProbe.PacketHashes(path, "-map 0:a -bsf:a aac_adtstoasc")[0];
        var ours = new List<string>();
        long expected = 0;
        while (track.ReadNext() is { } s)
        {
            Assert.Equal(expected, s.Dts);
            expected += 1024;
            ours.Add(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(s.GetData().Span)));
        }

        Assert.Equal(hashes, ours);
    }

    [Theory]
    [InlineData("ac3", "elem.ac3", CodecType.Ac3)]
    [InlineData("eac3", "elem.eac3", CodecType.Eac3)]
    public void Dolby_frames_and_configuration(string encoder, string name, CodecType codec)
    {
        var path = Make(name, $"-f lavfi -i sine=f=440:d=2 -af \"pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0\" -ar 48000 -c:a {encoder} -f {encoder}");
        using var demuxer = ElementaryFormat.Open(path);
        var track = Assert.Single(demuxer.Tracks);
        Assert.Equal(codec, track.Config.Codec);
        Assert.Equal(6, track.Config.Channels);
        Assert.Equal(48000, track.Config.SampleRate);
        var (channels, rate, _) = Ac3.Describe(track.Config.Extradata, codec == CodecType.Eac3);
        Assert.Equal(6, channels);
        Assert.Equal(48000, rate);

        var hashes = MediaProbe.PacketHashes(path, "-map 0:a")[0];
        var count = 0;
        while (track.ReadNext() is { } s)
        {
            Assert.Equal(hashes[count++], Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(s.GetData().Span)));
            Assert.Equal(1536, s.Duration);
        }

        Assert.Equal(hashes.Count, count);
    }

    [Fact]
    public void H264_annex_b_access_units_and_reordering()
    {
        var path = Make("elem-bframes.264", "-f lavfi -i testsrc=duration=2:size=160x120:rate=25 -c:v libx264 -preset medium -bf 3 -g 12 -bsf:v h264_mp4toannexb -f h264");
        using var demuxer = ElementaryFormat.Open(path);
        Assert.False(ElementaryFormat.RequiresFrameRate(demuxer));
        var track = Assert.Single(demuxer.Tracks);
        Assert.Equal(CodecType.H264, track.Config.Codec);
        Assert.Equal(160, track.Config.Width);
        Assert.Equal(120, track.Config.Height);
        Assert.Equal(25000u, track.Config.Timescale);
        Assert.Equal(1000, track.Config.DefaultSampleDuration);

        var samples = new List<MediaSample>();
        while (track.ReadNext() is { } s)
            samples.Add(s);
        Assert.Equal(50, samples.Count);
        Assert.True(samples[0].IsSync);

        // Presentation times are a permutation of the frame grid, and B-frames are reordered.
        var pts = samples.Select(s => (s.Pts - samples.Min(x => x.Pts)) / 1000).Order().ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), pts);
        Assert.Contains(samples, s => s.CtsOffset != samples[0].CtsOffset);

        // Samples are length-prefixed NAL units without AUDs or repeated parameter sets.
        foreach (var s in samples)
        {
            var nals = NalUnits.SplitLengthPrefixed(s.Data.Span, 4);
            Assert.DoesNotContain(nals, r => NalUnits.H264Type(s.Data.Span[r]) is H264.NalAud);
        }
    }
}
