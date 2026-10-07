using System.Buffers.Binary;

namespace MMW.Core.Media.Codecs;

/// <summary>DVD subpicture units (VobSub packets).</summary>
public static class Spu
{
    /// <summary>
    /// How long a subpicture is shown: the date of the control sequence that stops the display (STP_DSP), in units of
    /// 1024/90000 s from the start; null when the packet has none (it stays until the next one).
    /// </summary>
    public static TimeSpan? DisplayDuration(ReadOnlySpan<byte> spu)
    {
        if (spu.Length < 4)
            return null;
        int offset = BinaryPrimitives.ReadUInt16BigEndian(spu[2..]);
        for (var guard = 0; guard < 64 && offset + 4 <= spu.Length; guard++)
        {
            var date = BinaryPrimitives.ReadUInt16BigEndian(spu[offset..]);
            int next = BinaryPrimitives.ReadUInt16BigEndian(spu[(offset + 2)..]);
            var pos = offset + 4;
            while (pos < spu.Length)
            {
                var command = spu[pos++];
                if (command == 0xFF)
                    break;
                if (command == 0x02)
                    return TimeSpan.FromSeconds(date * 1024 / 90000.0);
                pos += command switch
                {
                    0x03 or 0x04 => 2,
                    0x05 => 6,
                    0x06 => 4,
                    0x07 when pos + 2 <= spu.Length => BinaryPrimitives.ReadUInt16BigEndian(spu[pos..]),
                    _ => 0,
                };
            }

            if (next == offset)
                break;
            offset = next;
        }

        return null;
    }
}
