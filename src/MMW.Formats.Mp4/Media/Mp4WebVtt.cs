using System.Buffers.Binary;
using System.Text;
using MMW.Core.Media;
using MMW.Formats.Mp4.Boxes;

namespace MMW.Formats.Mp4.Media;

/// <summary>A WebVTT cue of an ISO/IEC 14496-30 sample ('vttc'): identifier ('iden'), settings ('sttg') and text ('payl').</summary>
internal readonly record struct Mp4WebVttCue(string? Id, string? Settings, string Text);

/// <summary>
/// ISO/IEC 14496-30 WebVTT samples: each sample covers an interval of the timeline in which the set of active cues
/// does not change, and holds one 'vttc' box per active cue, or a single 'vtte' box when no cue is shown.
/// </summary>
internal static class Mp4WebVtt
{
    /// <summary>The cues of a sample (none for a 'vtte' sample).</summary>
    public static List<Mp4WebVttCue> Parse(ReadOnlySpan<byte> sample)
    {
        var cues = new List<Mp4WebVttCue>();
        var pos = 0;
        while (pos + 8 <= sample.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(sample[pos..]);
            if (size < 8 || pos + size > sample.Length)
                break;
            if (sample.Slice(pos + 4, 4).SequenceEqual("vttc"u8))
            {
                string? id = null, settings = null, text = null;
                var inner = sample.Slice(pos + 8, size - 8);
                var ip = 0;
                while (ip + 8 <= inner.Length)
                {
                    var isize = (int)BinaryPrimitives.ReadUInt32BigEndian(inner[ip..]);
                    if (isize < 8 || ip + isize > inner.Length)
                        break;
                    var type = inner.Slice(ip + 4, 4);
                    var value = Encoding.UTF8.GetString(inner.Slice(ip + 8, isize - 8));
                    if (type.SequenceEqual("payl"u8))
                        text = value;
                    else if (type.SequenceEqual("sttg"u8))
                        settings = value.Trim().Length > 0 ? value.Trim() : null;
                    else if (type.SequenceEqual("iden"u8))
                        id = value;
                    ip += isize;
                }

                if (text is not null)
                    cues.Add(new Mp4WebVttCue(id, settings, text));
            }

            pos += size;
        }

        return cues;
    }

    /// <summary>A sample holding <paramref name="cues"/> ('vttc' boxes), or a 'vtte' box when there are none.</summary>
    public static byte[] Build(IReadOnlyList<Mp4WebVttCue> cues)
    {
        if (cues.Count == 0)
            return BoxWriter.ToArray(new Box("vtte", []));
        using var ms = new MemoryStream();
        foreach (var cue in cues)
        {
            var children = new List<Box>();
            if (!string.IsNullOrEmpty(cue.Id))
                children.Add(new Box("iden", Encoding.UTF8.GetBytes(cue.Id)));
            if (!string.IsNullOrWhiteSpace(cue.Settings))
                children.Add(new Box("sttg", Encoding.UTF8.GetBytes(cue.Settings.Trim())));
            children.Add(new Box("payl", Encoding.UTF8.GetBytes(cue.Text)));
            ms.Write(BoxWriter.ToArray(new Box("vttc", null, children)));
        }

        return ms.ToArray();
    }
}

/// <summary>
/// Turns WebVTT cues (fed in start order, possibly overlapping) into the contiguous, non-overlapping intervals of
/// ISO/IEC 14496-30 samples: each interval lists the cues active in it; gaps are intervals without cues.
/// </summary>
internal sealed class Mp4WebVttTimeline
{
    private readonly List<(long End, Mp4WebVttCue Cue)> _active = [];
    private long _position;

    /// <summary>Adds a cue and returns the intervals that are complete before its start.</summary>
    public List<(long Start, long End, List<Mp4WebVttCue> Cues)> Add(long start, long end, Mp4WebVttCue cue)
    {
        var output = new List<(long, long, List<Mp4WebVttCue>)>();
        start = Math.Max(start, _position);
        Flush(start, output);
        if (end > start && cue.Text.Trim().Length > 0)
            _active.Add((end, cue));
        return output;
    }

    /// <summary>Returns the remaining intervals.</summary>
    public List<(long Start, long End, List<Mp4WebVttCue> Cues)> Complete()
    {
        var output = new List<(long, long, List<Mp4WebVttCue>)>();
        Flush(long.MaxValue, output);
        return output;
    }

    private void Flush(long until, List<(long, long, List<Mp4WebVttCue>)> output)
    {
        while (_position < until)
        {
            if (_active.Count == 0)
            {
                if (until != long.MaxValue)
                    output.Add((_position, until, []));
                _position = until;
                return;
            }

            var nextEnd = Math.Min(_active.Min(c => c.End), until);
            output.Add((_position, nextEnd, _active.Select(c => c.Cue).ToList()));
            _position = nextEnd;
            _active.RemoveAll(c => c.End <= _position);
        }
    }
}

/// <summary>
/// Rebuilds the original WebVTT cues from ISO/IEC 14496-30 samples: a cue repeated (same identifier, settings and
/// text) in consecutive samples is one cue lasting over all of them. Cues are returned in start order.
/// </summary>
internal sealed class Mp4WebVttCueMerger
{
    private sealed class PendingCue(long start, long end, Mp4WebVttCue cue)
    {
        public long Start { get; } = start;

        public long End { get; set; } = end;

        public Mp4WebVttCue Cue { get; } = cue;

        public bool Open { get; set; } = true;
    }

    private readonly List<PendingCue> _cues = [];

    /// <summary>Adds the cues of the sample presented from <paramref name="start"/> to <paramref name="end"/>; returns the cues that are complete.</summary>
    public List<MediaSample> Add(long start, long end, List<Mp4WebVttCue> cues)
    {
        var unmatched = new List<Mp4WebVttCue>(cues);
        foreach (var pending in _cues.Where(c => c.Open))
        {
            var match = pending.End == start ? unmatched.IndexOf(pending.Cue) : -1;
            if (match >= 0)
            {
                pending.End = end;
                unmatched.RemoveAt(match);
            }
            else
            {
                pending.Open = false;
            }
        }

        foreach (var cue in unmatched)
            _cues.Add(new PendingCue(start, end, cue));
        return Flush(false);
    }

    /// <summary>Returns the remaining cues.</summary>
    public List<MediaSample> Complete() => Flush(true);

    /// <summary>Forgets every cue (seeking back to the start).</summary>
    public void Clear() => _cues.Clear();

    private List<MediaSample> Flush(bool all)
    {
        var output = new List<MediaSample>();
        var count = 0;
        while (count < _cues.Count && (all || !_cues[count].Open))
            count++;
        foreach (var c in _cues.Take(count))
        {
            output.Add(new MediaSample
            {
                Dts = c.Start,
                Duration = Math.Max(1, c.End - c.Start),
                IsSync = true,
                Data = Encoding.UTF8.GetBytes(c.Cue.Text),
                CueSettings = c.Cue.Settings,
            });
        }

        _cues.RemoveRange(0, count);
        return output;
    }
}
