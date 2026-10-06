using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.TestSupport;
using static MMW.Media.Remux.Tests.RemuxFixtures;

namespace MMW.Media.Remux.Tests;

/// <summary>
/// Dolby TrueHD in MP4 per "Dolby TrueHD (MLP) bitstreams within the ISO base media file format": mlpa sample entry
/// with a 32-bit sampling rate, dmlp from the first major sync, media timescale = sampling rate, one access unit
/// per sample, stss on major syncs. Payloads (including the Atmos substream) must be copied unchanged.
/// </summary>
public sealed class TrueHdTests
{
    /// <summary>Matroska with a TrueHD stereo track encoded by ffmpeg (FBA syntax), 3 seconds.</summary>
    private static string MkvTrueHd()
    {
        MediaProbe.RequireFfmpeg();
        return Fixtures.Get("remux-truehd.mkv", "ffmpeg",
            "-v error -y -f lavfi -i sine=f=440:d=3:sample_rate=48000 -ac 2 -c:a truehd -strict -2 -f matroska {out}");
    }

    private static byte[] RawStream(string path)
    {
        var raw = MediaProbe.TempPath(".thd");
        Fixtures.Run("ffmpeg", $"-v error -y -i {Fixtures.Quote(path)} -map 0:a:0 -c copy -f truehd {Fixtures.Quote(raw)}");
        try
        {
            return File.ReadAllBytes(raw);
        }
        finally
        {
            File.Delete(raw);
        }
    }

    [Fact]
    public void Parser_walks_every_access_unit_and_rejects_damage()
    {
        var data = RawStream(MkvTrueHd());
        int pos = 0, count = 0, majors = 0, substreams = 0;
        TrueHdAccessUnit? first = null;
        while (pos < data.Length)
        {
            var au = TrueHd.Parse(data.AsSpan(pos), substreams);
            Assert.NotNull(au);
            first ??= au;
            if (au.IsMajorSync)
            {
                majors++;
                substreams = au.Substreams;
            }

            pos += au.Length;
            count++;
        }

        Assert.Equal(data.Length, pos);
        Assert.True(first!.IsMajorSync);
        Assert.Equal(48000, first.SampleRate);
        Assert.Equal(40, first.SamplesPerAccessUnit);
        Assert.InRange(count, 3 * 1200 - 2, 3 * 1200 + 2);
        Assert.True(majors >= count / 128);

        // A flipped bit in mlp_sync breaks the check nibble.
        var damaged = data.ToArray();
        damaged[3] ^= 0x10;
        Assert.Null(TrueHd.Parse(damaged, substreams));

        // DVD-Audio MLP (FBB syntax) is not Dolby TrueHD.
        var fbb = data.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(fbb.AsSpan(4), TrueHd.FormatSyncFbb);
        Assert.Null(TrueHd.Parse(fbb));

        var dmlp = TrueHd.BuildDmlp(first);
        Assert.Equal(10, dmlp.Length);
        Assert.Equal((first.FormatInfo, first.PeakDataRate), TrueHd.ParseDmlp(dmlp));
    }

    [Fact]
    public async Task Matroska_truehd_is_stored_in_mp4_as_dolby_specifies()
    {
        var source = MkvTrueHd();
        var output = MediaProbe.TempPath(".m4v");
        try
        {
            var doc = await Mkv.ReadAsync(source, Ct);
            var check = await Remuxer.CheckAsync(doc, ContainerKind.Mp4, Ct);
            Assert.All(check, c => Assert.Equal(TrackSupportLevel.Passthrough, c.Support.Level));
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            AssertDolbyCompliant(output, out var majors, out var samples);
            Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:a")[0], MediaProbe.PacketHashes(output, "-map 0:a")[0]);
            Assert.Empty(MediaProbe.DemuxErrors(output));

            // stss lists exactly the access units with a major sync.
            var data = RawStream(output);
            Assert.Equal(CountMajorSyncs(data), majors);
            Assert.True(samples > majors);

            // And back to Matroska: still the same access units.
            var back = MediaProbe.TempPath(".mkv");
            try
            {
                var mp4 = await Mp4.ReadAsync(output, Ct);
                await Remuxer.SaveAsync(mp4, new SaveOptions { OutputPath = back }, ContainerKind.Matroska, null, Ct);
                Assert.Equal(MediaProbe.PacketHashes(source, "-map 0:a")[0], MediaProbe.PacketHashes(back, "-map 0:a")[0]);
            }
            finally
            {
                MediaProbe.Delete(back);
            }
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    [Fact]
    public async Task Truehd_offers_passthru_and_aac_plus_passthru_for_mp4()
    {
        var tracks = await TrackImporter.InspectAsync(MkvTrueHd(), ContainerKind.Mp4, Ct);
        var truehd = Assert.Single(tracks);
        Assert.True(truehd.Support.CanMux);
        Assert.Contains(truehd.Choices, c => c.Action == ImportAction.Passthrough);
        if (truehd.CanConvert)
            Assert.Contains(truehd.Choices, c => c.Action == ImportAction.AacPlusPassthrough);
    }

    public static IEnumerable<TheoryDataRow<string, string>> CorpusPairs()
    {
        var pairs = Corpus.Files(".mkv")
            .Where(f => Path.GetFileName(f).Contains("TrueHD", StringComparison.Ordinal) && f.Contains("- Matroska}", StringComparison.Ordinal))
            .Select(f => (Mkv: f, Mp4: f.Replace("- Matroska}", "- MP4}", StringComparison.Ordinal).Replace(".mkv", ".mp4", StringComparison.Ordinal)))
            .Where(p => File.Exists(p.Mp4))
            .Select(p => new TheoryDataRow<string, string>(p.Mkv, p.Mp4))
            .ToList();
        return pairs.Count > 0 ? pairs : [new TheoryDataRow<string, string>(string.Empty, string.Empty)];
    }

    /// <summary>Corpus files that exist as Matroska and as a reference MP4 (e.g. Atmos, 7.1 at 96 kHz).</summary>
    [Theory]
    [MemberData(nameof(CorpusPairs))]
    public async Task Corpus_truehd_matches_the_reference_mp4(string mkv, string reference)
    {
        Corpus.Require(mkv);
        MediaProbe.RequireFfmpeg();
        var output = MediaProbe.TempPath(".m4v");
        try
        {
            var doc = await Mkv.ReadAsync(mkv, Ct);
            foreach (var t in doc.Tracks.Where(t => t is not AudioTrack and not ChapterTrack).ToList())
                doc.Tracks.Remove(t);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output }, ContainerKind.Mp4, null, Ct);

            var ours = Describe(output);
            var theirs = Describe(reference);
            Assert.Equal(theirs.Dmlp, ours.Dmlp);
            Assert.Equal(theirs.SampleRateField, ours.SampleRateField);
            Assert.Equal(theirs.Timescale, ours.Timescale);
            Assert.Equal(theirs.Stts, ours.Stts);
            Assert.Equal(theirs.SyncSamples, ours.SyncSamples);
            Assert.Equal(MediaProbe.PacketHashes(reference, "-map 0:a")[0], MediaProbe.PacketHashes(output, "-map 0:a")[0]);
            Assert.Equal(AudioProfile(reference), AudioProfile(output)); // e.g. "Dolby TrueHD + Dolby Atmos"
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    private static string AudioProfile(string path) =>
        Fixtures.Run("ffprobe", $"-v error -select_streams a:0 -show_entries stream=profile -of csv=p=0 {Fixtures.Quote(path)}").Trim();

    private sealed record Mp4TrueHd(string Dmlp, uint SampleRateField, uint Timescale, string Stts, int SyncSamples);

    private static Mp4TrueHd Describe(string path)
    {
        var moov = Mp4Layout.Read(path).Moov.Loaded!;
        var trak = moov.FindAll("trak").First(t => t.FindPath("mdia/minf/stbl/stsd")?.Find("mlpa") is not null);
        var entry = trak.FindPath("mdia/minf/stbl/stsd")!.Find("mlpa")!;
        var stbl = trak.FindPath("mdia/minf/stbl")!;
        var stts = stbl.Find("stts")!.Payload;
        var stss = stbl.Find("stss");
        return new Mp4TrueHd(
            Convert.ToHexString(entry.Find("dmlp")!.Payload),
            BinaryPrimitives.ReadUInt32BigEndian(entry.Payload.AsSpan(24)),
            HeaderBoxes.MdhdTimescale(trak.FindPath("mdia/mdhd")!),
            Convert.ToHexString(stts.AsSpan(4)),
            stss is null ? -1 : (int)BinaryPrimitives.ReadUInt32BigEndian(stss.Payload.AsSpan(4)));
    }

    private static void AssertDolbyCompliant(string path, out int majors, out int samples)
    {
        var moov = Mp4Layout.Read(path).Moov.Loaded!;
        var trak = Assert.Single(moov.FindAll("trak"), t => t.FindPath("mdia/minf/stbl/stsd")?.Find("mlpa") is not null);
        Assert.Equal("soun", HeaderBoxes.HdlrType(trak.FindPath("mdia/hdlr")!));
        Assert.NotNull(trak.FindPath("mdia/minf/smhd"));

        var entry = trak.FindPath("mdia/minf/stbl/stsd")!.Find("mlpa")!;
        var rate = BinaryPrimitives.ReadUInt32BigEndian(entry.Payload.AsSpan(24));
        Assert.Equal(48000u, rate); // 32-bit integer, not 16.16
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(entry.Payload.AsSpan(16))); // ChannelCount = 2
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(entry.Payload.AsSpan(18))); // SampleSize = 16
        var dmlp = entry.Find("dmlp")!.Payload;
        Assert.Equal(10, dmlp.Length);
        Assert.Equal(0, dmlp[5] & 1); // reserved bit
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(dmlp.AsSpan(6)));

        Assert.Equal(rate, HeaderBoxes.MdhdTimescale(trak.FindPath("mdia/mdhd")!));
        var stbl = trak.FindPath("mdia/minf/stbl")!;
        var stts = stbl.Find("stts")!.Payload;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(4))); // every sample has the same duration…
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12))); // …of one access unit (40 at 48 kHz)
        samples = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(8));
        var stss = stbl.Find("stss")!.Payload;
        majors = (int)BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(8))); // the track starts with a major sync
    }

    private static int CountMajorSyncs(byte[] data)
    {
        int pos = 0, majors = 0, substreams = 0;
        while (pos < data.Length && TrueHd.Parse(data.AsSpan(pos), substreams) is { } au)
        {
            if (au.IsMajorSync)
            {
                majors++;
                substreams = au.Substreams;
            }

            pos += au.Length;
        }

        return majors;
    }
}
