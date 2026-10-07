using MMW.Core.Media;
using MMW.Core.Model;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>Adding tracks from other files (elementary streams, subtitles, containers) and saving.</summary>
public sealed class ImportTests
{
    private const string Ass = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 320
        PlayResY: 240

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.50,0:00:01.50,Default,,0,0,0,,{\b1}Bold{\b0} words
        Dialogue: 0,0:00:02.00,0:00:02.90,Default,,0,0,0,,Second\Nline
        """;

    private const string Vtt = "WEBVTT\n\n00:00.500 --> 00:01.500\nFirst <i>cue</i>\n\n00:02.000 --> 00:02.900\nSecond cue\n";

    [Fact]
    public async Task Elementary_audio_and_subtitles_are_added_to_an_mp4()
    {
        var path = Fixtures.CopyToTemp(Mp4Full());
        try
        {
            var before = MediaProbe.PacketHashes(path);
            var doc = await Mp4.ReadAsync(path, Ct);

            var aac = await TrackImporter.InspectAsync(Adts(), ContainerKind.Mp4, Ct);
            var ac3 = await TrackImporter.InspectAsync(Ac3(), ContainerKind.Mp4, Ct);
            var srt = await TrackImporter.InspectAsync(Text("import.srt", Srt), ContainerKind.Mp4, Ct);
            Assert.Equal(TrackSupportLevel.Passthrough, Assert.Single(aac).Support.Level);
            Assert.Equal(TrackSupportLevel.Passthrough, Assert.Single(ac3).Support.Level);
            var srtTrack = Assert.Single(srt);
            Assert.Equal(TrackSupportLevel.Converted, srtTrack.Support.Level);
            Assert.Equal(ImportAction.ConvertToTx3g, srtTrack.Action);
            srtTrack.Language = "de";

            var added = TrackImporter.AddToDocument(doc, [.. aac, .. ac3, .. srt]);
            Assert.Equal(3, added.Count);
            Assert.All(added, t => Assert.True(t.IsPending));
            Assert.IsType<ChapterTrack>(doc.Tracks[^1]);

            await Mp4.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);

            var streams = MediaProbe.Streams(path);
            Assert.Equal(["h264", "aac", "aac", "mov_text", "aac", "ac3", "mov_text"], streams.Where(s => s.Type != "data").Select(s => s.Codec));
            Assert.Equal("deu", streams.Where(s => s.Codec == "mov_text").Last().Language);
            Assert.Empty(MediaProbe.DemuxErrors(path));

            // Original and imported packets are untouched.
            var after = MediaProbe.PacketHashes(path);
            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[1], after[1]);
            Assert.Equal(before[2], after[2]);
            Assert.Equal(MediaProbe.PacketHashes(Adts(), "-map 0:a -bsf:a aac_adtstoasc")[0], after[3]);
            Assert.Equal(MediaProbe.PacketHashes(Ac3())[0], after[4]);

            Assert.All(doc.Tracks, t => Assert.False(t.IsPending));
            Assert.Equal(path, doc.Tracks.OfType<AudioTrack>().Last().Source!.Path);
            Assert.False(doc.IsDirty);
            Assert.Equal(2, doc.Chapters.Count);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }

    [Fact]
    public async Task Text_subtitles_and_ac3_are_added_to_a_matroska_file()
    {
        CrossContainerTests.MkvToolsRequired();
        var path = Fixtures.CopyToTemp(MkvH264AacSrt());
        try
        {
            var before = MediaProbe.PacketHashes(path);
            var doc = await Mkv.ReadAsync(path, Ct);
            foreach (var file in new[] { Ac3(), Text("import.ass", Ass), Text("import.vtt", Vtt), Text("import.srt", Srt) })
            {
                var tracks = await TrackImporter.InspectAsync(file, ContainerKind.Matroska, Ct);
                Assert.All(tracks, t => Assert.Equal(TrackSupportLevel.Passthrough, t.Support.Level));
                TrackImporter.AddToDocument(doc, tracks);
            }

            await Mkv.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);

            var id = MediaProbe.MkvIdentify(path);
            Assert.Empty(id.GetProperty("errors").EnumerateArray());
            Assert.Empty(id.GetProperty("warnings").EnumerateArray());
            Assert.Equal(
                ["V_MPEG4/ISO/AVC", "A_AAC", "S_TEXT/UTF8", "A_AC3", "S_TEXT/ASS", "D_WEBVTT/SUBTITLES", "S_TEXT/UTF8"],
                id.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("properties").GetProperty("codec_id").GetString()!));

            var after = MediaProbe.PacketHashes(path);
            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[1], after[1]);
            Assert.Equal(MediaProbe.PacketHashes(Ac3())[0], after[2]);

            // The ASS track round-trips through ffmpeg's decoder.
            var ass = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:s:1 -f ass -");
            Assert.Contains("{\\b1}Bold{\\b0} words", ass, StringComparison.Ordinal);
            Assert.Contains("PlayResX: 320", ass, StringComparison.Ordinal);
            var vtt = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(path)} -map 0:s:2 -f webvtt -");
            Assert.Contains("First <i>cue</i>", vtt, StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task Raw_h264_is_imported_in_presentation_order(string extension)
    {
        var raw = RawH264();
        var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Mp4, Ct);
        var track = Assert.Single(tracks);
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.False(track.RequiresFrameRate);
        Assert.Equal(30000 / 1001.0, track.Config.FrameRate, 3);
        await AssertRawImportAsync(raw, tracks, extension, 60);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task Raw_hevc_is_imported_in_presentation_order(string extension)
    {
        var raw = RawHevc();
        var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Mp4, Ct);
        var track = Assert.Single(tracks);
        Assert.Equal("HEVC", track.Format);
        if (track.RequiresFrameRate)
            track.FrameRate = 25;
        await AssertRawImportAsync(raw, tracks, extension, 50);
    }

    private static async Task AssertRawImportAsync(string raw, IReadOnlyList<ImportableTrack> tracks, string extension, int frames)
    {
        var output = MediaProbe.TempPath(extension);
        try
        {
            var doc = new MediaDocument(null, ContainerKinds.FromPath(output));
            TrackImporter.AddToDocument(doc, tracks);
            var handler = extension == ".mp4" ? (IContainerHandler)Mp4 : Mkv;
            await handler.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);

            var decodedRaw = DecodedFrameHashes(raw);
            var decodedOut = DecodedFrameHashes(output);
            Assert.Equal(frames, decodedOut.Count);
            Assert.Equal(decodedRaw, decodedOut);
            Assert.Empty(MediaProbe.DemuxErrors(output));
            if (extension == ".mp4")
                Assert.Contains(Fixtures.Run("ffprobe", $"-v error -show_entries stream=codec_tag_string -of csv=p=0 {Fixtures.Quote(output)}").Trim(), new[] { "avc1", "hvc1" });
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Tracks_from_another_matroska_file_are_imported_and_reported()
    {
        var mkv = MkvHevcAc3Eac3();
        var forMp4 = await TrackImporter.InspectAsync(mkv, ContainerKind.Mp4, Ct);
        Assert.Equal(["HEVC", "AC-3", "E-AC-3"], forMp4.Select(t => t.Format));
        Assert.All(forMp4, t => Assert.Equal(TrackSupportLevel.Passthrough, t.Support.Level));
        Assert.Equal("5.1, 48 kHz", forMp4[1].Details);

        var path = Fixtures.CopyToTemp(Mp4Full());
        try
        {
            var doc = await Mp4.ReadAsync(path, Ct);
            TrackImporter.AddToDocument(doc, forMp4.Skip(1));
            await Mp4.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);
            var streams = MediaProbe.Streams(path);
            Assert.Contains(streams, s => s.Codec == "eac3" && s.Channels == 6);
            Assert.Contains(streams, s => s.Codec == "ac3" && s.ChannelLayout == "5.1(side)");
            Assert.Equal(MediaProbe.PacketHashes(mkv, "-map 0:a")[1], MediaProbe.PacketHashes(path, "-map 0:a")[3]);
        }
        finally
        {
            MediaProbe.Delete(path);
        }
    }

    [Fact]
    public async Task Check_lists_track_support_and_skipped_tracks_are_dropped()
    {
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(MkvH264AacSrt(), Ct);
            var check = await Remuxer.CheckAsync(doc, ContainerKind.Mp4, Ct);
            Assert.Equal([TrackSupportLevel.Passthrough, TrackSupportLevel.Passthrough, TrackSupportLevel.Converted], check.Select(c => c.Support.Level));

            var subtitle = doc.Tracks.OfType<SubtitleTrack>().Single();
            subtitle.Source = subtitle.Source! with { Import = new TrackImportOptions { Action = ImportAction.Skip } };
            await Mkv.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
            Assert.Equal(["h264", "aac"], MediaProbe.Streams(output).Where(s => s.Type != "data").Select(s => s.Codec));
            Assert.Equal(2, doc.Tracks.Count);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Codecs_that_need_conversion_are_reported_for_mp4()
    {
        MediaProbe.RequireFfmpeg();
        // 8-bit PCM is unsigned in Matroska; MP4's 'ipcm' holds 16, 24 and 32-bit signed integers only.
        var mkv = Fixtures.Get("remux-vorbis-pcm8.mkv", "ffmpeg",
            "-v error -y -f lavfi -i sine=d=1 -f lavfi -i sine=d=1 -map 0:a -map 1:a -ac 2 -c:a:0 vorbis -strict experimental -c:a:1 pcm_u8 {out}");
        var forMp4 = await TrackImporter.InspectAsync(mkv, ContainerKind.Mp4, Ct);
        var canConvert = MediaFormatRegistry.AvailableAudioConverter is not null;
        Assert.All(forMp4, t =>
        {
            Assert.True(t.ConversionRequired);
            Assert.NotNull(t.Support.Reason);
            Assert.Equal(canConvert, t.CanConvert);
            Assert.Equal(canConvert ? ImportAction.ConvertToAac : ImportAction.Skip, t.Action);
            Assert.Equal(canConvert, t.Selected);
            Assert.Equal(ImportAction.Skip, t.Choices[^1].Action);
        });
        var forMkv = await TrackImporter.InspectAsync(mkv, ContainerKind.Matroska, Ct);
        Assert.All(forMkv, t => Assert.Equal(TrackSupportLevel.Passthrough, t.Support.Level));

        // With FFmpeg the tracks are converted to AAC; without it, the save fails cleanly.
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = new MediaDocument(null, ContainerKind.Mp4);
            foreach (var t in forMp4)
                t.Action = ImportAction.ConvertToAac;
            TrackImporter.AddToDocument(doc, forMp4);
            if (canConvert)
            {
                await Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
                Assert.Equal(["aac", "aac"], MediaProbe.Streams(output).Select(s => s.Codec));
            }
            else
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct));
                Assert.False(File.Exists(output));
            }
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }
}
