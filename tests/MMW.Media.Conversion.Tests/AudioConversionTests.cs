using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Media.Remux;
using MMW.TestSupport;
using static MMW.Media.Conversion.Tests.ConversionFixtures;

namespace MMW.Media.Conversion.Tests;

/// <summary>Audio conversions through the import and remux pipeline, checked with ffprobe/ffmpeg.</summary>
public sealed class AudioConversionTests
{
    private static async Task<string> ImportAndSaveAsync(string source, ContainerKind target, Func<ImportableTrack, ImportChoice?> choose)
    {
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        foreach (var t in tracks)
        {
            if (choose(t) is { } c)
                t.Choice = c;
        }

        var extension = target == ContainerKind.Mp4 ? ".mp4" : ".mkv";
        var output = TempPath(extension);
        var doc = new MediaDocument(null, target);
        TrackImporter.AddToDocument(doc, tracks.Where(t => t.Action != ImportAction.Skip));
        IContainerHandler handler = target == ContainerKind.Mp4 ? Mp4 : Mkv;
        await handler.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
        return output;
    }

    private static ImportChoice Pick(ImportableTrack t, ImportAction action, AudioMixdown? mixdown = null) =>
        t.Choices.First(c => c.Action == action && (mixdown is null || c.Mixdown == mixdown));

    [Fact]
    public async Task Flac_stereo_converts_to_aac_in_mp4()
    {
        var output = await ImportAndSaveAsync(FlacStereoMkv(), ContainerKind.Mp4, t => Pick(t, ImportAction.ConvertToAac, AudioMixdown.Stereo));
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal("aac", s.Codec);
            Assert.Equal(2, s.Channels);
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Flac_51_downmixes_to_dolby_pro_logic_ii_stereo()
    {
        var output = await ImportAndSaveAsync(Flac51Mkv(), ContainerKind.Mp4, t => Pick(t, ImportAction.ConvertToAac, AudioMixdown.DolbyProLogicII));
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Theory]
    [InlineData(AudioMixdown.Multichannel, 6, "5.1")]
    [InlineData(AudioMixdown.DolbyProLogic, 2, "stereo")]
    [InlineData(AudioMixdown.Mono, 1, "mono")]
    public async Task Flac_51_converts_with_each_mixdown(AudioMixdown mixdown, int channels, string layout)
    {
        var output = await ImportAndSaveAsync(Flac51Mkv(), ContainerKind.Mp4, t => Pick(t, ImportAction.ConvertToAac, mixdown));
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", channels), (s.Codec, s.Channels));
            Assert.StartsWith(layout, s.ChannelLayout, StringComparison.Ordinal);
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Vorbis_in_matroska_is_converted_to_aac_by_default()
    {
        var tracks = await TrackImporter.InspectAsync(VorbisMkv(), ContainerKind.Mp4, Ct);
        var vorbis = Assert.Single(tracks);
        Assert.True(vorbis.ConversionRequired);
        Assert.True(vorbis.CanConvert);
        Assert.True(vorbis.Selected);
        Assert.Equal(ImportAction.ConvertToAac, vorbis.Action);
        Assert.Equal("AAC - Stereo", vorbis.Choice!.DisplayName);
        Assert.DoesNotContain(vorbis.Choices, c => c.Action == ImportAction.Passthrough);

        var output = await ImportAndSaveAsync(VorbisMkv(), ContainerKind.Mp4, _ => null);
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(3.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
            Assert.Equal("44100", ProbeStreams(output)[0].GetProperty("sample_rate").GetString());
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Dts_gets_aac_plus_passthru_with_a_fallback_reference()
    {
        var tracks = await TrackImporter.InspectAsync(Dts51Mkv(), ContainerKind.Mp4, Ct);
        var dts = Assert.Single(tracks);
        Assert.Equal(ImportAction.AacPlusPassthrough, dts.Action);
        Assert.Contains(dts.Choices, c => c.DisplayName == "AAC + AC3");
        Assert.Contains(dts.Choices, c => c.DisplayName == "Passthru");
        Assert.Contains(dts.Choices, c => c.DisplayName == "AAC - Dolby Pro Logic II");

        var doc = new MediaDocument(null, ContainerKind.Mp4);
        var added = TrackImporter.AddToDocument(doc, tracks);
        Assert.Equal(2, added.Count);
        var aacTrack = Assert.IsType<AudioTrack>(doc.Tracks[0]);
        var dtsTrack = Assert.IsType<AudioTrack>(doc.Tracks[1]);
        Assert.Equal("AAC", aacTrack.Format);
        Assert.Same(aacTrack, dtsTrack.Fallback);
        Assert.True(aacTrack.Enabled);
        Assert.False(dtsTrack.Enabled);

        var output = TempPath(".mp4");
        try
        {
            await Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
            var streams = MediaProbe.Streams(output);
            Assert.Equal(["aac", "dts"], streams.Select(s => s.Codec));
            Assert.Equal(2, streams[0].Channels);
            Assert.Equal(6, streams[1].Channels);
            AssertClose(3.0, streams[0].Duration, 0.05);
            Assert.Empty(DecodeErrors(output));

            var saved = await Mp4.ReadAsync(output, Ct);
            var savedAac = Assert.IsType<AudioTrack>(saved.Tracks[0]);
            var savedDts = Assert.IsType<AudioTrack>(saved.Tracks[1]);
            Assert.Same(savedAac, savedDts.Fallback);
            Assert.True(savedAac.Enabled);
            Assert.False(savedDts.Enabled);
            Assert.Equal(savedAac.AlternateGroup, savedDts.AlternateGroup);
            Assert.True(savedAac.AlternateGroup > 0);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Dts_converts_to_aac_plus_ac3()
    {
        var output = await ImportAndSaveAsync(Dts51Mkv(), ContainerKind.Mp4, t => Pick(t, ImportAction.AacPlusAc3));
        try
        {
            var streams = MediaProbe.Streams(output);
            Assert.Equal(["aac", "ac3"], streams.Select(s => s.Codec));
            Assert.Equal(6, streams[1].Channels);
            Assert.Equal("640000", ProbeStreams(output)[1].GetProperty("bit_rate").GetString());
            AssertClose(3.0, streams[1].Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
            Assert.True(File.ReadAllBytes(output).AsSpan().IndexOf("fall"u8) >= 0, "The AC-3 track has no fallback reference.");
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Theory]
    [InlineData("truehd")]
    [InlineData("pcm")]
    public async Task Lossless_matroska_audio_converts_to_aac(string kind)
    {
        var source = kind == "truehd" ? TrueHd51Mkv() : Pcm96kMkv();
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Mp4, Ct);
        Assert.Equal(ImportAction.ConvertToAac, Assert.Single(tracks).Action);
        var output = await ImportAndSaveAsync(source, ContainerKind.Mp4, _ => null);
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(3.0, s.Duration, 0.05);
            Assert.Equal("48000", ProbeStreams(output)[0].GetProperty("sample_rate").GetString());
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Flac_51_encodes_to_ac3()
    {
        var output = await ImportAndSaveAsync(Flac51Mkv(), ContainerKind.Mp4, t => Pick(t, ImportAction.ConvertToAc3));
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("ac3", 6), (s.Codec, s.Channels));
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Equal("640000", ProbeStreams(output)[0].GetProperty("bit_rate").GetString());
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Ac3_defaults_to_passthru_and_converts_with_drc()
    {
        var tracks = await TrackImporter.InspectAsync(Ac351Mkv(), ContainerKind.Mp4, Ct);
        var ac3 = Assert.Single(tracks);
        Assert.Equal(ImportAction.Passthrough, ac3.Action);
        Assert.Contains(ac3.Choices, c => c.Action == ImportAction.AacPlusPassthrough);
        Assert.DoesNotContain(ac3.Choices, c => c.Action is ImportAction.ConvertToAc3 or ImportAction.AacPlusAc3);

        ac3.Choice = Pick(ac3, ImportAction.ConvertToAac, AudioMixdown.Stereo);
        ac3.Conversion = ac3.Conversion! with { Drc = 2, BitratePerChannel = 128 };
        var doc = new MediaDocument(null, ContainerKind.Mp4);
        TrackImporter.AddToDocument(doc, tracks);
        var output = TempPath(".mp4");
        try
        {
            await Mp4.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(3.0, s.Duration, 0.05);
            var bitrate = int.Parse(ProbeStreams(output)[0].GetProperty("bit_rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(bitrate, 150_000, 330_000);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Opus_offers_passthru_but_converts_by_default_for_mp4()
    {
        var tracks = await TrackImporter.InspectAsync(OpusMkv(), ContainerKind.Mp4, Ct);
        var opus = Assert.Single(tracks);
        Assert.Equal(ImportAction.ConvertToAac, opus.Action);
        Assert.Contains(opus.Choices, c => c.Action == ImportAction.Passthrough);
        var forMkv = Assert.Single(await TrackImporter.InspectAsync(OpusMkv(), ContainerKind.Matroska, Ct));
        Assert.Equal(ImportAction.Passthrough, forMkv.Action);

        // Opus has an 80 ms pre-roll (pre-skip) that must not shift or lengthen the converted track.
        var output = await ImportAndSaveAsync(OpusMkv(), ContainerKind.Mp4, _ => null);
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal("aac", s.Codec);
            AssertClose(3.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Flac_converts_to_aac_inside_matroska()
    {
        var output = await ImportAndSaveAsync(FlacStereoMkv(), ContainerKind.Matroska, t => Pick(t, ImportAction.ConvertToAac, AudioMixdown.Stereo));
        try
        {
            var s = Assert.Single(MediaProbe.Streams(output));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Existing_track_can_be_converted_on_save()
    {
        var path = Fixtures.CopyToTemp(Flac51Mkv());
        var output = TempPath(".mp4");
        try
        {
            var doc = await Mkv.ReadAsync(path, Ct);
            var flac = Assert.Single(Audio(doc));
            Assert.False(flac.IsPending);
            var check = await Remuxer.CheckAsync(doc, ContainerKind.Mp4, Ct);
            Assert.Equal(TrackSupportLevel.Passthrough, Assert.Single(check).Support.Level);

            // Same container: the conversion alone forces the rebuild.
            Assert.Empty(TrackConversions.SetAction(doc, flac, ImportAction.ConvertToAac, ConversionDefaults.Settings with { Mixdown = AudioMixdown.Stereo }));
            Assert.True(RemuxPolicy.HasImportedTracks(doc));
            check = await Remuxer.CheckAsync(doc, ContainerKind.Matroska, Ct);
            Assert.Equal(TrackSupportLevel.Converted, Assert.Single(check).Support.Level);
            await Mkv.SaveAsync(doc, new SaveOptions(), cancellationToken: Ct);
            var s = Assert.Single(MediaProbe.Streams(path));
            Assert.Equal(("aac", 2), (s.Codec, s.Channels));
            AssertClose(4.0, s.Duration, 0.05);
            Assert.Empty(DecodeErrors(path));
            Assert.Null(Assert.Single(Audio(doc)).Source!.Import);

            // "AAC + Passthru" on an existing track when saving as MP4.
            var added = TrackConversions.SetAction(doc, Audio(doc)[0], ImportAction.AacPlusPassthrough,
                ConversionDefaults.Settings with { Mixdown = AudioMixdown.Mono });
            Assert.Single(added);
            await Mkv.SaveAsync(doc, new SaveOptions { OutputPath = output }, cancellationToken: Ct);
            var streams = MediaProbe.Streams(output);
            Assert.Equal([("aac", 1), ("aac", 2)], streams.Select(x => (x.Codec, x.Channels)));
            Assert.Empty(DecodeErrors(output));
            Assert.Equal(2, Audio(doc).Count);
        }
        finally
        {
            MediaProbe.Delete(path, output);
        }
    }

    [Fact]
    public async Task Converter_source_times_samples_from_zero_with_the_encoder_delay_as_pre_roll()
    {
        RequireFfmpeg();
        using var demuxer = Core.Media.MediaFormatRegistry.OpenDemuxer(FlacStereoMkv());
        var source = demuxer.Tracks[0];
        using var converter = (AudioConverterSource)MediaFormatRegistry.AudioConverter!.Create(source, AudioConversionTarget.Aac, ConversionDefaults.Settings);
        Assert.Equal(CodecType.Aac, converter.Config.Codec);
        Assert.Equal(48000u, converter.Config.Timescale);
        Assert.Equal(1024, converter.Config.DefaultSampleDuration);
        Assert.NotNull(converter.Config.Extradata);
        Assert.Equal(0x11, converter.Config.Extradata![0]); // AAC LC (2 << 3), 48 kHz (index 3)
        Assert.Equal(1024, converter.MediaStart);
        Assert.Equal(TimeSpan.Zero, converter.StartOffset);

        long expected = 0, total = 0;
        var count = 0;
        while (converter.ReadNext() is { } sample)
        {
            Assert.Equal(expected, sample.Dts);
            Assert.True(sample.IsSync);
            expected += sample.Duration;
            total += sample.Duration;
            count++;
        }

        Assert.Equal(4 * 48000 + 1024, total);
        Assert.InRange(count, 188, 190);

        // Reset replays the same stream.
        converter.Reset();
        Assert.Equal(0, converter.ReadNext()!.Dts);
    }
}
