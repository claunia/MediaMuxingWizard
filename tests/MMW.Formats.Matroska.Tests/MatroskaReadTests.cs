using MMW.Core.Metadata;
using MMW.Core.Model;

namespace MMW.Formats.Matroska.Tests;

public sealed class MatroskaReadTests
{
    private static readonly MatroskaHandler s_handler = new();

    [Fact]
    public async Task Read_MatchesMkvmergeIdentification()
    {
        var path = MkvFixtures.Full();
        var doc = await s_handler.ReadAsync(path, TestContext.Current.CancellationToken);
        var id = MkvFixtures.Identify(path);

        Assert.False(doc.IsDirty);
        Assert.Equal(ContainerKind.Matroska, doc.Container);
        var tracks = id.GetProperty("tracks").EnumerateArray().ToList();
        Assert.Equal(tracks.Count, doc.Tracks.Count);

        foreach (var t in tracks)
        {
            var p = t.GetProperty("properties");
            var number = p.GetProperty("number").GetUInt32();
            var track = Assert.Single(doc.Tracks, x => x.Id == number);
            var expectedKind = t.GetProperty("type").GetString() switch
            {
                "video" => TrackKind.Video,
                "audio" => TrackKind.Audio,
                "subtitles" => TrackKind.Subtitle,
                _ => TrackKind.Other,
            };
            Assert.Equal(expectedKind, track.Kind);
            Assert.Equal(p.GetProperty("codec_id").GetString(), track.CodecId);
            Assert.Equal(p.GetProperty("language_ietf").GetString(), track.Language);
            Assert.Equal(p.TryGetProperty("track_name", out var name) ? name.GetString() : string.Empty, track.Name);
            Assert.Equal(p.GetProperty("default_track").GetBoolean(), track.IsDefault);
            Assert.Equal(p.GetProperty("forced_track").GetBoolean(), track.IsForced);
            Assert.Equal(p.GetProperty("enabled_track").GetBoolean(), track.Enabled);
            Assert.Equal(new TrackSource(path, ContainerKind.Matroska, number), track.Source);
        }

        var durationNs = id.GetProperty("container").GetProperty("properties").GetProperty("duration").GetInt64();
        Assert.InRange((doc.Duration - TimeSpan.FromTicks(durationNs / 100)).Duration().TotalMilliseconds, 0, 1);

        var video = Assert.IsType<VideoTrack>(doc.Tracks[0]);
        Assert.Equal("Video", video.Name);
        Assert.Equal(320, video.PixelWidth);
        Assert.Equal(240, video.PixelHeight);
        Assert.StartsWith("320×240", video.FormatDetails, StringComparison.Ordinal);
        Assert.Equal(25, video.FrameRate, 1);

        var audio = Assert.IsType<AudioTrack>(doc.Tracks[1]);
        Assert.Equal("French audio", audio.Name);
        Assert.Equal("fr", audio.Language);
        Assert.Equal("AAC", audio.Format);
        Assert.False(audio.IsDefault);
        Assert.Equal("Mono, 44.1 kHz", audio.FormatDetails);

        var subtitle = Assert.IsType<SubtitleTrack>(doc.Tracks[2]);
        Assert.Equal("SRT", subtitle.Format);
        Assert.Equal("es", subtitle.Language);
        Assert.True(subtitle.IsForced);
        Assert.Contains(MediaCharacteristics.ForcedOnly, subtitle.MediaCharacteristics);
        Assert.Contains(MediaCharacteristics.TranscribesSpokenDialog, subtitle.MediaCharacteristics);

        Assert.Equal("Full fixture", doc.Metadata.GetString(TagId.Name));
        Assert.Equal("Someone", doc.Metadata.GetString(TagId.Artist));
        Assert.Equal("keep me", doc.Metadata.CustomItems["50/X_UNKNOWN_THING"]);

        Assert.Equal(2, doc.Chapters.Count);
        Assert.Equal("Intro", doc.Chapters[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Chapters[1].Start);
        Assert.Equal("Main", doc.Chapters[1].Title);

        var artwork = Assert.Single(doc.Metadata.Artworks);
        Assert.Equal(ArtworkFormat.Jpeg, artwork.Format);
        Assert.Equal("cover.jpg", artwork.FileName);
    }

    [Fact]
    public async Task Read_FfmpegFile_UsesInfoTitleAndLegacyLanguage()
    {
        var path = MkvFixtures.Basic();
        var doc = await s_handler.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("Hello", doc.Metadata.GetString(TagId.Name));
        Assert.Equal(2, doc.Tracks.Count);
        Assert.Equal("fr", doc.Tracks[1].Language);
        Assert.InRange(doc.Duration.TotalSeconds, 1.9, 2.2);
    }

    [Fact]
    public async Task Read_HdrColour()
    {
        var path = MkvFixtures.Hdr();
        var doc = await s_handler.ReadAsync(path, TestContext.Current.CancellationToken);
        var video = Assert.IsType<VideoTrack>(doc.Tracks[0]);
        Assert.Equal(new ColorInfo(9, 16, 9, false), video.Color);
        var hdr = Assert.IsType<HdrInfo>(video.Hdr);
        Assert.Equal(1000, hdr.MaxCll);
        Assert.Equal(400, hdr.MaxFall);
        Assert.Equal(1000, hdr.MaxLuminance);
        Assert.Equal(0.005, hdr.MinLuminance!.Value, 6);
        Assert.Equal(0.708, hdr.DisplayPrimaries![0].X, 6);
        Assert.Equal(0.3290, hdr.WhitePoint!.Value.Y, 6);
    }

    [Fact]
    public async Task Read_WebM()
    {
        var path = MkvFixtures.WebM();
        var doc = await s_handler.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("VP9", doc.Tracks[0].Format);
        Assert.Equal("Opus", doc.Tracks[1].Format);
        Assert.Equal("WebTitle", doc.Metadata.GetString(TagId.Name));
    }

    [Fact]
    public async Task Read_RejectsNonMatroska()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmw-tests", Guid.NewGuid().ToString("N") + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[64], TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => s_handler.ReadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
