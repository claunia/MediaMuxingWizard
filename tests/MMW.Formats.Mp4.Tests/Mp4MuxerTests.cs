using System.Buffers.Binary;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Media;
using MMW.TestSupport;

namespace MMW.Formats.Mp4.Tests;

/// <summary>Unit tests of the MP4 muxer (large files are simulated, never written).</summary>
public sealed class Mp4MuxerTests
{
    [Fact]
    public void Chunk_offsets_switch_to_co64_above_4_GB()
    {
        Assert.Equal("stco", SampleTable.BuildChunkOffsets([8, uint.MaxValue], force64: false).Type);
        Assert.Equal("co64", SampleTable.BuildChunkOffsets([8, (long)uint.MaxValue + 1], force64: false).Type);
        Assert.Equal("co64", SampleTable.BuildChunkOffsets([8], force64: true).Type);
    }

    [Fact]
    public void Media_data_over_4_GB_gets_a_large_mdat_and_co64()
    {
        const int sampleSize = 64 * 1024 * 1024;
        const int count = 70; // 4.375 GiB
        var stream = new SparseStream();
        var doc = new MediaDocument(null, ContainerKind.Mp4);
        var muxer = new Mp4Muxer(stream, new MuxerSettings { Document = doc, OutputPath = "large.m4a" });
        var track = muxer.AddTrack(new CodecConfig
        {
            Codec = CodecType.Aac,
            Kind = TrackKind.Audio,
            Timescale = 48000,
            SampleRate = 48000,
            Channels = 2,
            Extradata = Aac.BuildConfig(2, 48000, 2),
        }, new MuxTrackSettings());
        var zeros = new ZeroReader();
        for (var i = 0; i < count; i++)
            muxer.WriteSample(track, new MediaSample { Dts = i * 1024L, Duration = 1024, IsSync = true, Reader = zeros, Position = 0, StoredSize = sampleSize });
        muxer.Finish(TestContext.Current.CancellationToken);

        // Top level: ftyp, moov, free, then a 16-byte large-size mdat header.
        var head = stream.ReadBack(0, 64 * 1024);
        var boxes = new List<(string Type, long Offset, long Size)>();
        long pos = 0;
        while (pos + 16 <= head.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan((int)pos));
            var type = Box.Latin1.GetString(head, (int)pos + 4, 4);
            if (size == 1)
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan((int)pos + 8));
            boxes.Add((type, pos, size));
            if (type == "mdat")
                break;
            pos += size;
        }

        Assert.Equal(["ftyp", "moov", "free", "mdat"], boxes.Select(b => b.Type));
        var mdat = boxes[^1];
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan((int)mdat.Offset)));
        Assert.Equal((long)count * sampleSize + 16, mdat.Size);
        Assert.Equal(stream.Length, mdat.Offset + mdat.Size);

        var moovBox = boxes[1];
        var moov = BoxParser.ParseSingle(head.AsSpan((int)moovBox.Offset, (int)moovBox.Size));
        var stbl = moov.FindPath("trak/mdia/minf/stbl")!;
        Assert.NotNull(stbl.Find("co64"));
        Assert.Null(stbl.Find("stco"));
        var offsets = SampleTable.ChunkOffsets(stbl);
        Assert.Equal(count, offsets.Length);
        Assert.Equal(mdat.Offset + 16, offsets[0]);
        Assert.Equal(mdat.Offset + 16 + (count - 1L) * sampleSize, offsets[^1]);
        Assert.True(offsets[^1] > uint.MaxValue);
        Assert.Equal(count, SampleTable.Expand(stbl).Length);
    }

    [Fact]
    public async Task Use64BitOffsets_forces_co64_and_stays_readable()
    {
        Mp4Fixtures.RequireTools();
        var source = Mp4Fixtures.FastStart();
        var output = MediaProbe.TempPath(".mp4");
        try
        {
            var doc = await new Mp4Handler().ReadAsync(source, TestContext.Current.CancellationToken);
            await Remuxer.SaveAsync(doc, new SaveOptions { OutputPath = output, Use64BitOffsets = true, Use64BitTimes = true }, ContainerKind.Mp4,
                cancellationToken: TestContext.Current.CancellationToken);
            using (var fs = File.OpenRead(output))
            {
                var layout = Mp4Layout.Read(fs);
                Assert.All(layout.Moov.Loaded!.FindAll("trak"), t => Assert.NotNull(t.FindPath("mdia/minf/stbl/co64")));
                Assert.Equal(1, layout.Moov.Loaded!.Find("mvhd")!.Payload[0]);
            }

            Assert.Equal(Mp4Fixtures.PacketHashes(source), Mp4Fixtures.PacketHashes(output));
            Assert.Empty(Mp4Fixtures.DemuxErrors(output));
        }
        finally
        {
            MediaProbe.Delete(output);
        }
    }

    /// <summary>Payloads that are all zeros.</summary>
    private sealed class ZeroReader : ISampleDataReader
    {
        public void Read(long position, Span<byte> destination) => destination.Clear();
    }

    /// <summary>A seekable stream that only remembers small writes (headers), so huge outputs can be simulated.</summary>
    private sealed class SparseStream : Stream
    {
        private readonly List<(long Position, byte[] Data)> _writes = [];
        private long _length;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The sparse stream cannot be read back sequentially.");

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => _length + offset,
        };

        public override void SetLength(long value) => _length = value;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 1024 * 1024)
                _writes.Add((Position, buffer.AsSpan(offset, count).ToArray()));
            Position += count;
            _length = Math.Max(_length, Position);
        }

        public byte[] ReadBack(long position, int count)
        {
            var result = new byte[count];
            foreach (var (pos, data) in _writes)
            {
                var start = Math.Max(pos, position);
                var end = Math.Min(pos + data.Length, position + count);
                if (end > start)
                    data.AsSpan((int)(start - pos), (int)(end - start)).CopyTo(result.AsSpan((int)(start - position)));
            }

            return result;
        }
    }
}
