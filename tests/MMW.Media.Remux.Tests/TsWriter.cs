namespace MMW.Media.Remux.Tests;

/// <summary>Writes MPEG transport streams by hand, for mappings no available tool produces.</summary>
internal static class TsWriter
{
    /// <summary>A PES packet with a PTS (private stream 1 unless <paramref name="streamId"/> says otherwise).</summary>
    public static byte[] Pes(byte[] payload, long pts, byte streamId = 0xBD)
    {
        var length = 3 + 5 + payload.Length;
        return
        [
            0, 0, 1, streamId, (byte)(length > 0xFFFF ? 0 : length >> 8), (byte)(length > 0xFFFF ? 0 : length), 0x84, 0x80, 5,
            (byte)(0x21 | ((pts >> 29) & 0x0E)), (byte)(pts >> 22), (byte)(((pts >> 14) & 0xFE) | 1), (byte)(pts >> 7), (byte)(((pts << 1) & 0xFE) | 1),
            .. payload,
        ];
    }

    /// <summary>Cuts <paramref name="data"/> into 188-byte packets of <paramref name="pid"/>, stuffing the last one.</summary>
    public static void Packets(List<byte> o, Dictionary<int, int> counters, int pid, byte[] data, bool randomAccess)
    {
        var first = true;
        var at = 0;
        while (first || at < data.Length)
        {
            var counter = counters.GetValueOrDefault(pid);
            counters[pid] = (counter + 1) & 15;
            var remaining = data.Length - at;
            var adaptation = (first && randomAccess) || remaining < 184;
            var room = adaptation ? 182 : 184;
            var take = Math.Min(room, remaining);
            o.AddRange([0x47, (byte)((first ? 0x40 : 0) | (pid >> 8)), (byte)pid, (byte)((adaptation ? 0x30 : 0x10) | counter)]);
            if (adaptation)
            {
                o.Add((byte)(1 + room - take));
                o.Add((byte)(first && randomAccess ? 0x40 : 0));
                o.AddRange(Enumerable.Repeat((byte)0xFF, room - take));
            }

            o.AddRange(data.AsSpan(at, take).ToArray());
            at += take;
            first = false;
        }
    }

    /// <summary>CRC-32/MPEG-2 of a PSI section, big-endian.</summary>
    public static byte[] Crc(byte[] section)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in section)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    /// <summary>PAT and a PMT (PID 0x1000, PCR on 0x100) with one stream on PID 0x100.</summary>
    public static void Psi(List<byte> o, Dictionary<int, int> counters, byte streamType, byte[] descriptors)
    {
        byte[] pat = [0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00];
        byte[] body = [0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00, streamType, 0xE1, 0x00, (byte)(0xF0 | (descriptors.Length >> 8)), (byte)descriptors.Length, .. descriptors];
        byte[] pmt = [0x02, 0xB0, (byte)(body.Length + 4), .. body];
        Packets(o, counters, 0, [0, .. pat, .. Crc(pat)], false);
        Packets(o, counters, 0x1000, [0, .. pmt, .. Crc(pmt)], false);
    }

    /// <summary>A start code and <paramref name="raw"/> with emulation prevention bytes (00 00 0x, x ≤ 3 → 00 00 03 0x).</summary>
    public static byte[] Escaped(byte code, ReadOnlySpan<byte> raw)
    {
        var o = new List<byte> { 0, 0, 1, code };
        var zeros = 0;
        foreach (var b in raw)
        {
            if (zeros >= 2 && b <= 3)
            {
                o.Add(3);
                zeros = 0;
            }

            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. o];
    }
}
