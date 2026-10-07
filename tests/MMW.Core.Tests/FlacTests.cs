using System.Buffers.Binary;
using System.Text;
using MMW.Core.Media.Codecs;
using MMW.Core.Metadata;

namespace MMW.Core.Tests;

/// <summary>FLAC frame headers, STREAMINFO, encoder names and Vorbis comment tags.</summary>
public sealed class FlacTests
{
    /// <summary>A frame header: fixed block size, 4096 samples, 44.1 kHz, stereo, 16-bit, frame number 5.</summary>
    private static byte[] Header(int number = 5)
    {
        byte[] h = [0xFF, 0xF8, 0xC9, 0x18, (byte)number, 0];
        h[^1] = Flac.Crc8(h.AsSpan(0, 5));
        return h;
    }

    [Fact]
    public void Parses_a_frame_header_checked_by_its_crc()
    {
        var header = Assert.IsType<FlacFrameHeader>(Flac.ParseFrameHeader(Header()));
        Assert.Equal((4096, 5L, false, 6), (header.Samples, header.Number, header.VariableBlockSize, header.HeaderLength));
        var damaged = Header();
        damaged[4] ^= 1;
        Assert.Null(Flac.ParseFrameHeader(damaged)); // CRC-8 mismatch
        Assert.Null(Flac.ParseFrameHeader([0xFF, 0xF8, 0x0F, 0x18, 0, 0])); // reserved block size code
    }

    [Fact]
    public void Crc16_of_a_frame_with_its_crc_is_zero()
    {
        var frame = new List<byte>(Header());
        frame.AddRange(Encoding.ASCII.GetBytes("subframes"));
        var crc = Flac.Crc16(frame.ToArray());
        frame.Add((byte)(crc >> 8));
        frame.Add((byte)crc);
        Assert.Equal(0, Flac.Crc16(frame.ToArray()));
    }

    [Theory]
    [InlineData("reference libFLAC 1.4.3 20230623", "libFLAC 1.4.3")]
    [InlineData("Lavf61.7.100", "Lavf61.7.100")]
    [InlineData("", "")]
    public void Names_the_encoder(string vendor, string expected) => Assert.Equal(expected, Flac.EncoderName(vendor));

    [Fact]
    public void Describes_streaminfo_and_vendor()
    {
        var streamInfo = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo, 4096);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(2), 4096);
        // 96000 Hz, 6 channels, 24 bits: rate(20) channels-1(3) bits-1(5) total(36).
        var packed = (96000UL << 44) | (5UL << 41) | (23UL << 36) | 480000UL;
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10), packed);
        var vendor = Encoding.UTF8.GetBytes("reference libFLAC 1.4.3 20230623");
        var comment = new byte[8 + vendor.Length];
        BinaryPrimitives.WriteInt32LittleEndian(comment, vendor.Length);
        vendor.CopyTo(comment, 4);
        byte[] blocks = [0, 0, 0, 34, .. streamInfo, 0x84, 0, 0, (byte)comment.Length, .. comment];

        var info = Assert.IsType<FlacStreamInfo>(Flac.ParseStreamInfo(blocks));
        Assert.Equal((96000, 6, 24, 480000L, false), (info.SampleRate, info.Channels, info.BitsPerSample, info.TotalSamples, info.HasMd5));
        Assert.Equal("24-bit, 4096-sample blocks, libFLAC 1.4.3, no MD5", Flac.DescribeStream(blocks));
    }

    [Fact]
    public void Maps_vorbis_comments_and_pictures()
    {
        KeyValuePair<string, string>[] fields =
        [
            new("TITLE", "Song"), new("artist", "A"), new("ARTIST", "B"), new("ALBUM", "Record"), new("DATE", "2001"),
            new("TRACKNUMBER", "3"), new("TRACKTOTAL", "12"), new("DISCNUMBER", "1/2"), new("BPM", "fast"), new("COMPILATION", "1"),
        ];
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var picture = new List<byte>();
        void U32(int v) => picture.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U32(3);
        U32(10);
        picture.AddRange("image/jpeg"u8.ToArray());
        U32(0);
        U32(1);
        U32(1);
        U32(24);
        U32(0);
        U32(jpeg.Length);
        picture.AddRange(jpeg);

        var metadata = VorbisComments.ToMetadata(fields, [picture.ToArray()]);
        Assert.Equal("Song", metadata.GetString(TagId.Name));
        Assert.Equal("A; B", metadata.GetString(TagId.Artist)); // repeated fields are joined
        Assert.Equal("Record", metadata.GetString(TagId.Album));
        Assert.Equal(new IntPair(3, 12), metadata.GetPair(TagId.TrackNumber));
        Assert.Equal(new IntPair(1, 2), metadata.GetPair(TagId.DiskNumber));
        Assert.False(metadata.Contains(TagId.Tempo)); // not a number
        Assert.True(metadata.GetBool(TagId.Compilation));
        Assert.Equal(jpeg, Assert.Single(metadata.Artworks).Data);

        // Ogg streams carry pictures as comments: a base64 PICTURE block, or the older base64 image.
        var ogg = VorbisComments.ToMetadata(
        [
            new("METADATA_BLOCK_PICTURE", Convert.ToBase64String(picture.ToArray())),
            new("COVERART", Convert.ToBase64String([0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2])),
            new("COVERART", "not base64!"),
        ]);
        Assert.Equal([ArtworkFormat.Jpeg, ArtworkFormat.Png], ogg.Artworks.Select(a => a.Format));
    }
}
