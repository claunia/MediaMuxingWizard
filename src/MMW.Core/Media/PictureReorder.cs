namespace MMW.Core.Media;

/// <summary>
/// Presentation times of MPEG-1/2/4 Part 2 pictures from their coding types, for containers that only give decoding
/// times (AVI, ASF, MPEG program stream pictures without a PTS): B pictures are shown when decoded, I and P pictures
/// when the next one arrives; the n-th picture shown takes the n-th decoding time plus the stream's delay. A time the
/// container gives is kept. Pictures leave in decoding order once their presentation time is known.
/// </summary>
public sealed class PictureReorder(long delay, long frameTicks)
{
    private readonly List<long> _dts = [];
    private readonly LinkedList<(MediaSample Sample, bool Timed, long Pts)> _waiting = new();
    private LinkedListNode<(MediaSample Sample, bool Timed, long Pts)>? _pendingReference;
    private int _shown;

    /// <summary>'I', 'P', 'B' or 'S' (MPEG-4 sprite), '?' when the picture header cannot be found.</summary>
    public static char PictureType(CodecType codec, byte[] data)
    {
        if (codec == CodecType.Mpeg4Visual)
        {
            var vop = IndexOfStartCode(data, 0xB6);
            return vop < 0 || vop + 4 >= data.Length ? '?' : "IPBS"[data[vop + 4] >> 6];
        }

        var picture = IndexOfStartCode(data, 0x00);
        if (picture < 0 || picture + 5 >= data.Length)
            return '?';
        return ((data[picture + 5] >> 3) & 7) switch
        {
            1 => 'I',
            2 => 'P',
            3 => 'B',
            _ => '?',
        };
    }

    /// <summary>Position of the start code 00 00 01 <paramref name="code"/> in <paramref name="data"/>, or -1.</summary>
    public static int IndexOfStartCode(byte[] data, byte code, int from = 0)
    {
        for (var i = from; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == code)
                return i;
        }

        return -1;
    }

    /// <param name="pts">The container's presentation time, used when known (long.MinValue otherwise).</param>
    public void Add(MediaSample sample, char type, long pts, Queue<MediaSample> output)
    {
        if (sample.Dts == long.MinValue)
            sample.Dts = _dts.Count > 0 ? _dts[^1] + frameTicks : 0;
        _dts.Add(sample.Dts);
        var node = _waiting.AddLast((sample, false, pts));
        if (type == 'B')
        {
            Show(node);
        }
        else
        {
            if (_pendingReference is { } reference)
                Show(reference);
            _pendingReference = node;
        }

        Release(output);
    }

    public void Flush(Queue<MediaSample> output)
    {
        if (_pendingReference is { } reference)
            Show(reference);
        _pendingReference = null;
        Release(output);
    }

    private void Show(LinkedListNode<(MediaSample Sample, bool Timed, long Pts)> node)
    {
        var slot = Math.Min(_shown++, _dts.Count - 1);
        var pts = node.Value.Pts != long.MinValue ? node.Value.Pts : _dts[slot] + delay;
        node.Value.Sample.CtsOffset = pts - node.Value.Sample.Dts;
        node.Value = (node.Value.Sample, true, node.Value.Pts);
    }

    private void Release(Queue<MediaSample> output)
    {
        while (_waiting.First is { Value.Timed: true } first)
        {
            output.Enqueue(first.Value.Sample);
            _waiting.RemoveFirst();
        }
    }
}
