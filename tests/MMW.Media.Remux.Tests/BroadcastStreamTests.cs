using System.Text;
using MMW.Core.Media;
using MMW.Core.Model;
using MMW.Formats.MpegTs;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Broadcast and disc streams: teletext subtitles decoded to text, DVB subtitles (passthrough and OCR), DTS products
/// (DTS-HD, DTS:X) in every container, raw DTS files, and AVS2 video stored in Matroska and MP4.
/// </summary>
public sealed class BroadcastStreamTests
{
    // ------------------------------------------------------------------ teletext (synthetic)

    private static readonly byte[] s_hamming = [0x15, 0x02, 0x49, 0x5E, 0x64, 0x73, 0x38, 0x2F, 0xD0, 0xC7, 0x8C, 0x9B, 0xA1, 0xB6, 0xFD, 0xEA];

    private static byte Reverse(byte b)
    {
        var r = 0;
        for (var i = 0; i < 8; i++)
            if ((b & (1 << i)) != 0)
                r |= 0x80 >> i;
        return (byte)r;
    }

    private static byte Parity(int c) => (byte)(int.PopCount(c) % 2 == 0 ? c | 0x80 : c);

    /// <summary>One EBU teletext data unit (44 bytes, bit-reversed as DVB carries it).</summary>
    private static byte[] Unit(int magazine, int row, byte[] data)
    {
        var address = (magazine & 7) | row << 3;
        byte[] packet = [0x00, 0xE4, s_hamming[address & 0x0F], s_hamming[address >> 4], .. data];
        return [0x03, 0x2C, .. packet.Select(Reverse)];
    }

    private static byte[] Header(int page, bool erase, int charset)
    {
        var data = new byte[40];
        data[0] = s_hamming[page & 0x0F];
        data[1] = s_hamming[page >> 4];
        data[2] = s_hamming[0];
        data[3] = s_hamming[erase ? 8 : 0]; // C4
        data[4] = s_hamming[0];
        data[5] = s_hamming[0];
        data[6] = s_hamming[0];
        data[7] = s_hamming[charset << 1]; // C12–C14
        for (var i = 8; i < 40; i++)
            data[i] = Parity(' ');
        return data;
    }

    private static byte[] Row(string boxed)
    {
        var chars = new List<int> { 0x0B, 0x0B };
        chars.AddRange(boxed.Select(c => (int)c));
        chars.AddRange([0x0A, 0x0A]);
        while (chars.Count < 40)
            chars.Add(' ');
        return [.. chars.Select(Parity)];
    }

    private static Pes TeletextPes(long pts, params byte[][] units) => new(pts, null, [0x10, .. units.SelectMany(u => u)], false);

    [Fact]
    public void Teletext_subtitle_pages_become_text_cues()
    {
        // Subtitle page 888 (magazine 8 is coded as 0), German national subset: '~' is ß, '{' is ä.
        var info = new TsStreamInfo(0x157F, 0x06, [(0x56, [.. "deu"u8, 0x10, 0x88])]);
        var page = Assert.Single(TeletextStream.SubtitlePages(info));
        Assert.Equal((8, 0x88, "deu", false), page);
        var stream = new TeletextStream(info, 8, 0x88, "deu", false);
        var output = new Queue<MediaSample>();

        stream.OnPes(TeletextPes(0, Unit(0, 0, Header(0x88, true, 4)), Unit(0, 20, Row("Hallo Stra~e"))), output);
        stream.OnPes(TeletextPes(180000, Unit(0, 0, Header(0x88, true, 4)), Unit(0, 21, Row("Sp{ter"))), output);
        // Another page of the same magazine in between must not disturb page 888.
        stream.OnPes(TeletextPes(270000, Unit(0, 0, Header(0x00, true, 4)), Unit(0, 1, Row("Index"))), output);
        stream.OnPes(TeletextPes(360000, Unit(0, 0, Header(0x88, true, 4))), output); // erased: the subtitle goes away
        stream.Flush(output);

        var cues = output.Select(s => (s.Pts, s.Duration, Encoding.UTF8.GetString(s.GetData().Span))).ToList();
        Assert.Equal([(0L, 180000L, "Hallo Straße"), (180000L, 180000L, "Später")], cues);
        var config = stream.Describe();
        Assert.Equal(CodecType.TextUtf8, config.Codec);
        Assert.Equal("de", config.Language);
        Assert.Equal("Teletext 888", config.Name);
    }

    // ------------------------------------------------------------------ corpus (MMW_CORPUS)

    private static string Sample(params string[] parts)
    {
        var path = Corpus.Directory is { } dir ? Path.Combine([dir, .. parts]) : string.Empty;
        Corpus.Require(File.Exists(path) ? path : string.Empty);
        MediaProbe.RequireFfmpeg();
        return path;
    }

    public static TheoryData<string, string> DtsFiles() => new()
    {
        { "{DTS Discrete 6.1 - M2TS} DTS Sparks Sprite (Sound Effects).m2ts", "DTS-ES" },
        { "{DTS-HD HRA 7.1 - Matroska} DTS Logo (Orchestra).mkv", "DTS-HD HRA" },
        { "{DTS-HD MA 7.1 96KHz - MP4} DTS Logo (Orchestra).mp4", "DTS-HD MA" },
        { "{DTS-X - Matroska} Sound Unbound.mkv", "DTS:X" },
        { "{DTS-X - MP4} Sound Unbound.mp4", "DTS:X" },
        { "{DTS-X - MPEG-TS} Sound Unbound.ts", "DTS:X" },
        { "{DTS-X IMAX - Matroska} All Around Us.mkv", "DTS:X IMAX" },
        { "{DTS 5.1 - Matroska} DTS Digital Theater Sound.mkv", "" },
    };

    [Theory]
    [MemberData(nameof(DtsFiles))]
    public async Task Dts_products_are_detected_in_every_container(string name, string product)
    {
        var path = Sample("Multichannel audio", name);
        var dts = (await TrackImporter.InspectAsync(path, ContainerKind.Matroska, Ct)).First(t => t.Config.Codec == CodecType.Dts);
        Assert.Equal(product, dts.Config.AudioProfile);
        var ffprobe = Fixtures.Run("ffprobe", $"-v error -select_streams a:0 -show_entries stream=channels,sample_rate -of csv=p=0 {Fixtures.Quote(path)}").Trim().Split('\n')[0].Trim().Split(',');
        Assert.Equal((int.Parse(ffprobe[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(ffprobe[0], System.Globalization.CultureInfo.InvariantCulture)),
            (dts.Config.Channels, dts.Config.SampleRate)); // the whole stream's, not the core's

        if (Path.GetExtension(path) is ".mkv" or ".mp4")
        {
            // Documents: detected from the bitstream after opening.
            var doc = await (path.EndsWith(".mkv", StringComparison.Ordinal) ? Mkv.ReadAsync(path, Ct) : Mp4.ReadAsync(path, Ct));
            var audio = doc.Tracks.OfType<AudioTrack>().First(DtsDetector.NeedsCheck);
            Assert.True(await DtsDetector.DescribeAsync(audio, Ct));
            Assert.Equal(product, audio.Profile);
        }
    }

    [Fact]
    public async Task Raw_dts_files_are_imported_with_their_product()
    {
        var source = Sample("Multichannel audio", "{DTS-X - Matroska} Sound Unbound.mkv");
        var raw = MediaProbe.TempPath(".dts");
        var output = MediaProbe.TempPath(".mkv");
        try
        {
            Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(source)} -map 0:a:0 -c copy -f dts {Fixtures.Quote(raw)}");
            var tracks = await TrackImporter.InspectAsync(raw, ContainerKind.Matroska, Ct);
            var dts = Assert.Single(tracks);
            Assert.Equal("DTS:X", dts.Config.AudioProfile);
            Assert.Contains("DTS:X", dts.Details, StringComparison.Ordinal);
            var doc = new MediaDocument(null, ContainerKind.Matroska);
            TrackImporter.AddToDocument(doc, tracks);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, null, Ct);
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:a:0")[0], MediaProbe.PacketHashes(output, "-map 0:a:0")[0]);
        }
        finally
        {
            MediaProbe.Delete(raw, output);
        }
    }

    [Fact]
    public async Task Dvb_subtitles_pass_to_matroska_and_ocr_to_text_without_repeating_lines()
    {
        var source = Sample("Subtitles", "Embedded", "{DVB subtitles - MPEG-TS} BBC One News.ts");
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Mp4, Ct);
        var dvb = tracks.Single(t => t.Config.Codec == CodecType.DvbSub);
        Assert.Equal("en", dvb.Language);
        Assert.Equal(5, dvb.Config.Extradata!.Length);

        var mkv = MediaProbe.TempPath(".mkv");
        var mp4 = MediaProbe.TempPath(".mp4");
        try
        {
            var mkvTracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
            var doc = new MediaDocument(null, ContainerKind.Matroska);
            TrackImporter.AddToDocument(doc, [mkvTracks.Single(t => t.Config.Codec == CodecType.DvbSub)]);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = mkv }, ContainerKind.Matroska, null, Ct);
            int Packets(string path) => int.Parse(Fixtures.Run("ffprobe",
                $"-v error -select_streams s:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 {Fixtures.Quote(path)}").Trim().Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(Packets(source), Packets(mkv));

            var ocr = dvb.Choices.FirstOrDefault(c => c.Ocr);
            if (ocr is null)
            {
                Assert.Skip("OCR (Tesseract and FFmpeg) is not available.");
                return;
            }

            dvb.Choice = ocr;
            var ocrDoc = new MediaDocument(null, ContainerKind.Mp4);
            TrackImporter.AddToDocument(ocrDoc, [dvb]);
            await Remuxer.SaveAsync(ocrDoc, new SaveOptions { OutputPath = mp4 }, ContainerKind.Mp4, null, Ct);
            var srt = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(mp4)} -map 0:s -f srt -");
            Assert.Contains("video link", srt, StringComparison.Ordinal);
            // Live subtitles update the page word by word: each cue is the page as shown, never several pages stacked.
            var cues = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(c => string.Join('\n', c.Split('\n').Skip(2))).ToList();
            Assert.All(cues, c => Assert.True(c.Split('\n').Length <= 3, c));
        }
        finally
        {
            MediaProbe.Delete(mkv, mp4);
        }
    }

    [Fact]
    public async Task Teletext_and_dvb_subtitles_of_the_main_program_of_a_multiplex()
    {
        var source = Sample("Subtitles", "Embedded", "{Teletext + DVB subtitles - MPEG-TS} BBC Two HD multiplex.ts");
        var tracks = await TrackImporter.InspectAsync(source, ContainerKind.Matroska, Ct);
        Assert.Contains(tracks, t => t.Config.Codec == CodecType.H264 && t.Config.Width == 1920); // BBC Two HD, not the first program
        var teletext = tracks.Single(t => t.Config.Codec == CodecType.TextUtf8);
        Assert.Equal(("Teletext 888", "en"), (teletext.Name, teletext.Language));
        Assert.Contains(tracks, t => t.Config.Codec == CodecType.DvbSub);

        var output = MediaProbe.TempPath(".mkv");
        try
        {
            var doc = new MediaDocument(null, ContainerKind.Matroska);
            TrackImporter.AddToDocument(doc, [teletext]);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Matroska, null, Ct);
            var srt = Fixtures.Run("ffmpeg", $"-v error -i {Fixtures.Quote(output)} -map 0:s -f srt -");
            Assert.Contains("able to cover their face", srt, StringComparison.Ordinal);
            Assert.Contains("teaching in school", srt, StringComparison.Ordinal);
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    public async Task Avs2_video_is_stored_unchanged(string extension)
    {
        var source = Sample("Video codecs", "AVS2.m2ts");
        var target = extension == ".mkv" ? ContainerKind.Matroska : ContainerKind.Mp4;
        var tracks = await TrackImporter.InspectAsync(source, target, Ct);
        var video = tracks.Single(t => t.Kind == TrackKind.Video);
        Assert.Equal((CodecType.Avs2, 1280, 720, TrackSupportLevel.Passthrough), (video.Config.Codec, video.Config.Width, video.Config.Height, video.Support.Level));
        var output = MediaProbe.TempPath(extension);
        var extracted = MediaProbe.TempPath(".avs2");
        try
        {
            var doc = new MediaDocument(null, target);
            TrackImporter.AddToDocument(doc, [video]);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, target, null, Ct);
            Fixtures.Run("ffmpeg", $"-v quiet -y -i {Fixtures.Quote(output)} -map 0:v -c copy -f data {Fixtures.Quote(extracted)}");
            Assert.Equal(ElementaryStream(source, 0x1011), File.ReadAllBytes(extracted));
            Assert.Equal(extension == ".mkv" ? "avs2" : "avst",
                Fixtures.Run("ffprobe", $"-v error -select_streams v -show_entries stream={(extension == ".mkv" ? "codec_name" : "codec_tag_string")} -of csv=p=0 {Fixtures.Quote(output)}").Trim());
        }
        finally
        {
            MediaProbe.Delete(output, extracted);
        }
    }

    /// <summary>The PES payloads of one PID of an M2TS file, concatenated.</summary>
    private static byte[] ElementaryStream(string path, int pid)
    {
        var data = File.ReadAllBytes(path);
        var pes = new List<List<byte>>();
        for (var i = 0; i + 192 <= data.Length; i += 192)
        {
            var p = data.AsSpan(i + 4, 188);
            if (p[0] != 0x47 || ((p[1] & 0x1F) << 8 | p[2]) != pid || ((p[3] >> 4) & 1) == 0)
                continue;
            var offset = ((p[3] >> 4) & 2) != 0 ? 5 + p[4] : 4;
            if ((p[1] & 0x40) != 0)
                pes.Add([]);
            pes.LastOrDefault()?.AddRange(p[offset..].ToArray());
        }

        return [.. pes.SelectMany(x => x.Skip(9 + x[8]))];
    }
}
