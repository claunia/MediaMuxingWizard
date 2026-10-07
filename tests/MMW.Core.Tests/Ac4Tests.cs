using System.Buffers.Binary;
using MMW.Core.Media.Codecs;
using MMW.TestSupport;

namespace MMW.Core.Tests;

/// <summary>AC-4 sync frames, TOC parsing and the 'dac4' decoder specific information.</summary>
public sealed class Ac4Tests
{
    [Fact]
    public void Parses_a_sync_frame_header()
    {
        byte[] frame = [0xAC, 0x40, 0x00, 0x03, 0x11, 0x22, 0x33, 0xAC];
        Assert.Equal(4, Ac4.SyncFrameHeaderLength(frame, out var length, out var crc));
        Assert.Equal((3, false), (length, crc));
        Assert.Equal(7, Ac4.SyncFrameLength(frame));
        Assert.Equal([0x11, 0x22, 0x33], Ac4.RawFrame(frame));

        Assert.Equal(0, Ac4.SyncFrameHeaderLength([0xAC, 0x42, 0x00, 0x03], out _, out _)); // not a sync word
        Assert.Equal(0, Ac4.SyncFrameHeaderLength([0xAC, 0x40, 0x00], out _, out _)); // truncated
        Assert.Null(Ac4.RawFrame([0xAC, 0x40, 0x00, 0x05, 0x11])); // frame larger than the data
        Assert.Equal(7, Ac4.FindSync([0, 1, 2, 0xAC, 3, 0xAC, 0xAC, 0xAC, 0x41, 0]));
        Assert.Equal(-1, Ac4.FindSync([0xAC, 0x42, 0x40]));
    }

    [Fact]
    public void Parses_an_extended_frame_size_with_crc()
    {
        // 0xAC41 (CRC follows) with frame_size 0xFFFF escaping to a 24-bit size of 0x000102 (258) bytes.
        var frame = new byte[7 + 258 + 2];
        byte[] header = [0xAC, 0x41, 0xFF, 0xFF, 0x00, 0x01, 0x02];
        header.CopyTo(frame, 0);
        for (var i = 0; i < 258; i++)
            frame[7 + i] = (byte)i;
        Assert.Equal(7, Ac4.SyncFrameHeaderLength(frame, out var length, out var crc));
        Assert.Equal((258, true), (length, crc));
        Assert.Equal(frame.Length, Ac4.SyncFrameLength(frame));
        Assert.Equal(frame.AsSpan(7, 258).ToArray(), Ac4.RawFrame(frame));
        Assert.Equal(0, Ac4.SyncFrameHeaderLength(frame.AsSpan(0, 6), out _, out _)); // extended size cut
    }

    /// <summary>The first sync frame of Dolby's stereo "Audio ID" test signal (AC-4 Online Delivery Kit 1.5).</summary>
    private static readonly byte[] StereoSyncFrame = Convert.FromBase64String(
        "rEH//wABp7/O5ZhABNEBLiwgMGBAXIRY0KDAYBO1g1TLYTkSFEsCMr6FS0gAAvJv/T7K10iJsKKiWwG7URGOWxaS1hNLdVmEeBAB" +
        "6sqoMTJBBhByVpsAnpK3BDGNDMtJV5gBPoI90X3woDiywFMlYHtzaT3ork4rF5S34JwBda7y/01NplzdeNUNgx1Oz5oMxeduhK1y" +
        "eEQbaM3Jer4cGb8QmWsWdswqtn+C5FLheQQAR/17wkCBakm50yVotW+dB71lw7Q1Z8lcWXaoeinF0ZYrLlhUbvs3pdJNnq+Q2Gd2" +
        "/mc6KqZYm0QjnGXk/Oc1Cdc3fGCaS1CnYr1PS2/W/CtdJ/jmD1almuc1i/bJW8U5lV98G/8mmFqze/ltiaZWbfmGKveOfx3/fVbL" +
        "y7Vnbbru47y1m/Xtbu83a3Vuray2yw81oqNO8uQzX94eNneSlG0khcMJa9lfz9SBAldmxjsFTncvxroEpSk3zPFy8zO6gj78u99g" +
        "dbXawnvM5osfzMLJJOkXZ9duycXFpjv5dLraIyrs0Q9q2jq4PsoBAAAB5GAA8jABIKPBNLX/AIym");

    [Fact]
    public void Builds_the_dac4_of_a_stereo_frame()
    {
        Assert.Equal(7, Ac4.SyncFrameHeaderLength(StereoSyncFrame, out var length, out var crc));
        Assert.Equal((423, true), (length, crc));
        Assert.Equal(StereoSyncFrame.Length, Ac4.SyncFrameLength(StereoSyncFrame));

        var raw = Ac4.RawFrame(StereoSyncFrame)!;
        var info = Assert.IsType<Ac4Info>(Ac4.Parse(raw));
        Assert.Equal((2, 1, 2, true), (info.BitstreamVersion, info.FsIndex, info.FrameRateIndex, info.IFrame));
        Assert.Equal((48000, 1920, 48000, 2), (info.SampleRate, info.SampleDuration, info.MediaTimescale, info.ChannelCount));
        var presentation = Assert.Single(info.Presentations);
        Assert.Equal((1, true, 1, 1), (presentation.Version, presentation.ChannelCoded, presentation.ChannelMode, presentation.ChannelGroups));
        Assert.True(presentation.DialogueEnhancement);
        Assert.Equal("2.0", Ac4.Describe(info));

        // As written by Dolby's tools and by GPAC (MP4Box) for this stream.
        Assert.Equal("20A401400000001FFFFFFFE0010FF88000004200000250100000030080", Convert.ToHexString(Ac4.BuildDsi(raw)!));
    }

    [Fact]
    public void Rejects_frames_it_cannot_parse()
    {
        Assert.Null(Ac4.Parse([]));
        Assert.Null(Ac4.Parse([0x40, 0x00])); // bitstream_version 1 is not supported
        Assert.Null(Ac4.BuildDsi([0x40, 0x00]));
    }

    [Fact]
    public void Describes_presentations()
    {
        static Ac4Info Info(params Ac4Presentation[] presentations) => new() { Presentations = presentations, ChannelCount = 6 };

        Assert.Equal("AC-4", Ac4.Describe(Info()));
        Assert.Equal("2.0", Ac4.Describe(Info(new Ac4Presentation { Version = 1, ChannelCoded = true, ChannelMode = 1, ChannelGroups = 1 })));
        Assert.Equal("5.1", Ac4.Describe(Info(new Ac4Presentation { Version = 1, ChannelCoded = true, ChannelMode = 4, ChannelGroups = 0x47 })));
        // 7.1.4 ch_mode without back channels and with Tfl/Tfr/Tbl/Tbr: 5.1.4
        Assert.Equal("5.1.4", Ac4.Describe(Info(new Ac4Presentation { Version = 1, ChannelCoded = true, ChannelMode = 12, ChannelGroups = 0x77 })));
        Assert.Equal("Immersive Stereo, 2 presentations", Ac4.Describe(Info(new Ac4Presentation { Version = 2, ChannelCoded = true, ChannelMode = 1 }, new Ac4Presentation { Version = 1 })));
        Assert.Equal("Object based (6 ch)", Ac4.Describe(Info(new Ac4Presentation { Version = 1 })));
    }

    public static TheoryData<string, string, int, string> CorpusPairs => new()
    {
        { "{AC-4 2.0 - Raw} Dolby Audio ID.ac4", "{AC-4 2.0 - MP4} Dolby Audio ID.mp4", 2, "2.0" },
        { "{AC-4 5.1 - Raw} Dolby Audio ID.ac4", "{AC-4 5.1 - MP4} Dolby Audio ID.mp4", 6, "5.1" },
        { "{AC-4 5.1.4 - Raw} Dolby Audio ID.ac4", "{AC-4 5.1.4 - MP4} Dolby Audio ID.mp4", 10, "5.1.4" },
        { "{AC-4 Immersive Stereo - Raw} Dolby Audio ID.ac4", "{AC-4 Immersive Stereo - MP4} Dolby Audio ID.mp4", 2, "Immersive Stereo" },
    };

    /// <summary>Dolby's own MP4 files carry the reference 'dac4'; the raw elementary streams the same audio.</summary>
    [Theory]
    [MemberData(nameof(CorpusPairs))]
    public void Builds_the_dac4_of_dolbys_mp4(string raw, string mp4, int channels, string description)
    {
        var dir = Corpus.Directory is { } d ? Path.Combine(d, "Multichannel audio") : string.Empty;
        var rawPath = Path.Combine(dir, raw);
        var mp4Path = Path.Combine(dir, mp4);
        Corpus.Require(dir.Length > 0 && File.Exists(rawPath) && File.Exists(mp4Path) ? rawPath : string.Empty);

        var es = File.ReadAllBytes(rawPath);
        var container = File.ReadAllBytes(mp4Path);
        var frames = RawFrames(es).Take(8).ToList();
        Assert.NotEmpty(frames);

        var info = Assert.IsType<Ac4Info>(Ac4.Parse(frames[0]));
        Assert.True(info.IFrame);
        Assert.Equal((2, 48000, channels), (info.BitstreamVersion, info.SampleRate, info.ChannelCount));
        Assert.Equal(25, info.FrameRate);
        Assert.Equal(1920, info.SamplesPerFrame);
        Assert.Equal(description, Ac4.Describe(info));

        Assert.Equal(Convert.ToHexString(Dac4(container)), Convert.ToHexString(Ac4.BuildDsi(frames[0])!));

        // Every frame describes the same stream; the MP4 samples are the raw frames of the elementary stream.
        Assert.All(frames, f => Assert.Equal(Ac4.BuildDsi(frames[0]), Ac4.BuildDsi(f)));
        Assert.All(frames.Take(3), f => Assert.True(container.AsSpan().IndexOf(f) >= 0));
    }

    private static IEnumerable<byte[]> RawFrames(byte[] es)
    {
        var pos = 0;
        while (pos < es.Length)
        {
            var length = Ac4.SyncFrameLength(es.AsSpan(pos));
            if (length == 0 || pos + length > es.Length)
                yield break;
            yield return Ac4.RawFrame(es.AsSpan(pos, length))!;
            pos += length;
        }
    }

    /// <summary>The payload of the first 'dac4' box of an MP4 file.</summary>
    private static byte[] Dac4(byte[] file)
    {
        var span = file.AsSpan();
        var at = 4;
        while (true)
        {
            var i = span[at..].IndexOf("dac4"u8);
            Assert.True(i >= 0, "no dac4 box");
            at += i;
            var size = BinaryPrimitives.ReadInt32BigEndian(span[(at - 4)..]);
            if (size is > 8 and < 512)
                return span.Slice(at + 4, size - 8).ToArray();
            at += 4;
        }
    }
}
