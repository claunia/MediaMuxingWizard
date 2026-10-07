using System.Globalization;
using Microsoft.Win32.SafeHandles;
using MMW.Formats.Matroska.Ebml;
using MMW.Formats.Matroska.Resources;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>
/// Rewrites top-level elements of a Matroska Segment in place, mkvpropedit-style: an element that fits in its old
/// slot (plus any Void elements following it) is written there and the rest is turned into a Void; otherwise it is
/// moved to the end of the Segment and its old slot becomes a Void. The SeekHead and the Segment size are updated to
/// match. Clusters and Cues are never moved or read.
/// </summary>
internal sealed class MatroskaInPlaceWriter
{
    private const int ZeroBlockSize = 64 * 1024;

    private enum RegionKind
    {
        /// <summary>An unchanged element at its original position.</summary>
        Element,

        /// <summary>Free space, written as a Void element.</summary>
        Void,

        /// <summary>A newly encoded element.</summary>
        New,
    }

    private sealed class Region
    {
        public RegionKind Kind { get; set; }

        public ulong Id { get; init; }

        public long Size { get; init; }

        public long OriginalPosition { get; init; } = -1;

        public byte[]? Bytes { get; init; }

        /// <summary>For voids: the bytes on disk are not a Void yet and must be written.</summary>
        public bool Dirty { get; set; }

        public long Position { get; set; }
    }

    private readonly MatroskaLayout _layout;
    private readonly List<Region> _regions = [];
    private readonly HashSet<ulong> _touched = [];

    private MatroskaInPlaceWriter(MatroskaLayout layout)
    {
        _layout = layout;
        foreach (var e in layout.Elements)
        {
            _regions.Add(new Region
            {
                Kind = e.Id == VoidElement ? RegionKind.Void : RegionKind.Element,
                Id = e.Id,
                Size = e.TotalSize,
                OriginalPosition = e.Position,
            });
        }
    }

    /// <summary>Applies <paramref name="updates"/> to the file open in <paramref name="handle"/>.</summary>
    /// <exception cref="NotSupportedException">The file cannot be edited in place.</exception>
    public static void Apply(SafeFileHandle handle, MatroskaLayout layout, IReadOnlyList<ElementUpdate> updates, CancellationToken cancellationToken)
    {
        if (layout.ScanProblem is not null)
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NotEditableInPlace, layout.ScanProblem));
        if (updates.Count == 0)
            return;

        var writer = new MatroskaInPlaceWriter(layout);
        writer.Plan(updates);
        cancellationToken.ThrowIfCancellationRequested();
        writer.Execute(handle);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Planning
    // ------------------------------------------------------------------------------------------------------------

    private void Plan(IReadOnlyList<ElementUpdate> updates)
    {
        var pending = new List<(ulong Id, byte[] Payload, Region? Target)>();
        foreach (var update in updates)
        {
            _touched.Add(update.Id);
            var instances = _regions.Where(r => r.Kind == RegionKind.Element && r.Id == update.Id).ToList();
            var keep = update.Payload is null ? 0 : 1;
            foreach (var extra in instances.Skip(keep))
                MakeVoid(extra);
            if (update.Payload is not null)
                pending.Add((update.Id, update.Payload, instances.FirstOrDefault()));
        }

        while (pending.Count > 0)
        {
            var progress = false;
            foreach (var p in pending.ToList())
            {
                if (p.Target is not null && TryPlace(p.Target, p.Id, p.Payload))
                {
                    pending.Remove(p);
                    progress = true;
                }
            }

            if (progress)
                continue;

            // Nothing fits where it is: move one element to the end (which may free room for the others).
            var move = pending.FirstOrDefault(p => p.Target is not null);
            if (move.Payload is null)
                move = pending[0];
            pending.Remove(move);
            if (move.Target is not null)
                MakeVoid(move.Target);
            Append(move.Id, move.Payload);
        }

        UpdateSeekHead();
        ComputePositions();

        var newEnd = _regions.Count > 0 ? _regions[^1].Position + _regions[^1].Size : _layout.SegmentDataPosition;
        if (newEnd > _layout.SegmentEnd && _layout.SegmentEnd < _layout.FileLength)
            throw new NotSupportedException(Strings.Error_SegmentCannotGrow);
    }

    private static void MakeVoid(Region region)
    {
        region.Kind = RegionKind.Void;
        region.Dirty = true;
    }

    /// <summary>Encodes an element so it exactly fills <paramref name="slot"/> bytes, possibly with a trailing Void.</summary>
    /// <returns>The element bytes, or null when it does not fit.</returns>
    private static byte[]? Fit(ulong id, byte[] payload, long slot, out long filler)
    {
        filler = 0;
        var element = EbmlWriter.Element(id, payload);
        if (element.Length == slot)
            return element;
        if (element.Length + 1 == slot)
        {
            // A Void needs at least 2 bytes: grow the size field by one byte instead.
            var sizeLength = EbmlVarInt.SizeLength((ulong)payload.Length);
            return sizeLength < EbmlVarInt.MaxSizeLength ? EbmlWriter.Element(id, payload, sizeLength + 1) : null;
        }

        if (element.Length + 2 <= slot)
        {
            filler = slot - element.Length;
            return element;
        }

        return null;
    }

    /// <summary>Places an element in the slot of <paramref name="target"/> plus the Voids that follow it.</summary>
    private bool TryPlace(Region target, ulong id, byte[] payload)
    {
        var index = _regions.IndexOf(target);
        var last = index;
        var slot = target.Size;
        while (last + 1 < _regions.Count && _regions[last + 1].Kind == RegionKind.Void)
        {
            last++;
            slot += _regions[last].Size;
        }

        var bytes = Fit(id, payload, slot, out var filler);
        if (bytes is null)
            return false;

        _regions.RemoveRange(index, last - index + 1);
        _regions.Insert(index, new Region { Kind = RegionKind.New, Id = id, Size = bytes.Length, Bytes = bytes });
        if (filler > 0)
            _regions.Insert(index + 1, new Region { Kind = RegionKind.Void, Size = filler, Dirty = true });
        return true;
    }

    /// <summary>Places an element at the end of the Segment, reusing trailing Void space.</summary>
    private void Append(ulong id, byte[] payload)
    {
        var tail = _regions.Count;
        long trailing = 0;
        while (tail > 0 && _regions[tail - 1].Kind == RegionKind.Void)
        {
            tail--;
            trailing += _regions[tail].Size;
        }

        if (trailing > 0 && Fit(id, payload, trailing, out var filler) is { } fitted)
        {
            _regions.RemoveRange(tail, _regions.Count - tail);
            _regions.Add(new Region { Kind = RegionKind.New, Id = id, Size = fitted.Length, Bytes = fitted });
            if (filler > 0)
                _regions.Add(new Region { Kind = RegionKind.Void, Size = filler, Dirty = true });
            return;
        }

        var bytes = EbmlWriter.Element(id, payload);
        _regions.RemoveRange(tail, _regions.Count - tail);
        _regions.Add(new Region { Kind = RegionKind.New, Id = id, Size = bytes.Length, Bytes = bytes });
    }

    private void ComputePositions()
    {
        var pos = _layout.SegmentDataPosition;
        foreach (var r in _regions)
        {
            r.Position = pos;
            if (r.Kind == RegionKind.Element && r.OriginalPosition != pos)
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Error_InternalElementMove, r.Id, r.OriginalPosition, pos));
            pos += r.Size;
        }
    }

    private bool PointsTo(ulong id, long relativePosition)
    {
        var absolute = _layout.SegmentDataPosition + relativePosition;
        return _regions.Any(r => r.Kind != RegionKind.Void && r.Id == id && r.Position == absolute);
    }

    private void UpdateSeekHead()
    {
        ComputePositions();
        var seekHeads = _layout.SeekHeads.OrderBy(s => s.Position).ToList();
        var primary = seekHeads.FirstOrDefault();
        var entries = new List<SeekEntry>();

        if (primary is not null)
        {
            foreach (var e in primary.Entries)
            {
                if (!_touched.Contains(e.Id) && e.Id != SeekHead && PointsTo(e.Id, e.Position))
                    entries.Add(e);
            }

            // Secondary SeekHeads that index something that moved are merged into the primary one.
            foreach (var secondary in seekHeads.Skip(1))
            {
                var region = _regions.FirstOrDefault(r => r.Kind == RegionKind.Element && r.OriginalPosition == secondary.Position);
                if (region is null)
                    continue;
                var stale = secondary.Entries.Any(e => _touched.Contains(e.Id) || !PointsTo(e.Id, e.Position));
                if (stale)
                {
                    MakeVoid(region);
                    foreach (var e in secondary.Entries)
                    {
                        if (!_touched.Contains(e.Id) && e.Id != SeekHead && PointsTo(e.Id, e.Position) && !entries.Contains(e))
                            entries.Add(e);
                    }
                }
                else
                {
                    var relative = secondary.Position - _layout.SegmentDataPosition;
                    if (primary.Entries.Any(e => e.Id == SeekHead && e.Position == relative))
                        entries.Add(new SeekEntry(SeekHead, relative));
                }
            }
        }
        else
        {
            foreach (var r in _regions)
            {
                if (r.Kind != RegionKind.Void && r.Id is not Cluster and not SeekHead && !_touched.Contains(r.Id))
                    entries.Add(new SeekEntry(r.Id, r.Position - _layout.SegmentDataPosition));
            }
        }

        foreach (var id in _touched)
        {
            var region = _regions.FirstOrDefault(r => r.Kind != RegionKind.Void && r.Id == id);
            if (region is not null)
                entries.Add(new SeekEntry(id, region.Position - _layout.SegmentDataPosition));
        }

        if (primary is null)
        {
            // Without a SeekHead, readers only find what precedes the first Cluster: add one if anything moved after it.
            var firstCluster = _regions.FindIndex(r => r.Id == Cluster && r.Kind == RegionKind.Element);
            var movedPastClusters = firstCluster >= 0 && _regions.Skip(firstCluster).Any(r => r.Kind == RegionKind.New);
            if (!movedPastClusters)
                return;

            var payload = EncodeSeekHead(entries, false);
            foreach (var candidate in _regions.Take(firstCluster).Where(r => r.Kind == RegionKind.Void).ToList())
            {
                if (TryPlace(candidate, SeekHead, payload))
                    return;
            }

            throw new NotSupportedException(
                Strings.Error_NoSeekHeadSpace);
        }

        if (entries.ToHashSet().SetEquals(primary.Entries))
            return;

        var primaryRegion = _regions.First(r => r.Kind == RegionKind.Element && r.OriginalPosition == primary.Position);
        var full = EncodeSeekHead(entries, primary.HasCrc);
        if (TryPlace(primaryRegion, SeekHead, full))
            return;

        // The full index does not fit: move it to the end and leave a pointer to it in the original SeekHead slot.
        Append(SeekHead, full);
        ComputePositions();
        var moved = _regions.Last(r => r.Kind == RegionKind.New && r.Id == SeekHead);
        var pointer = EncodeSeekHead([new SeekEntry(SeekHead, moved.Position - _layout.SegmentDataPosition)], primary.HasCrc);
        if (!TryPlace(primaryRegion, SeekHead, pointer))
            throw new NotSupportedException(Strings.Error_SeekHeadTooSmall);
    }

    private static byte[] EncodeSeekHead(IEnumerable<SeekEntry> entries, bool withCrc)
    {
        var w = new EbmlWriter();
        foreach (var e in entries)
        {
            w.Master(Seek, s =>
            {
                s.Binary(SeekId, EbmlVarInt.EncodeId(e.Id));
                s.UInt(SeekPosition, (ulong)e.Position);
            });
        }

        return withCrc ? EbmlWriter.WithCrc32(w.WrittenSpan) : w.ToArray();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------------------------------------------------

    private void Execute(SafeFileHandle handle)
    {
        ComputePositions();
        var oldEnd = _layout.SegmentEnd;
        var newEnd = _regions.Count > 0 ? _regions[^1].Position + _regions[^1].Size : _layout.SegmentDataPosition;

        // 1. Content appended past the old end: harmless if interrupted before the Segment size is updated.
        foreach (var r in _regions.Where(r => r.Kind == RegionKind.New && r.Position >= oldEnd))
            RandomAccess.Write(handle, r.Bytes!, r.Position);

        // 2. Segment size.
        if (!_layout.SegmentSizeUnknown && newEnd != oldEnd)
        {
            var size = (ulong)(newEnd - _layout.SegmentDataPosition);
            var field = new byte[_layout.SegmentSizeLength];
            if (size <= EbmlVarInt.MaxSizeValue(field.Length))
                EbmlVarInt.WriteSize(field, size, field.Length);
            else
                EbmlVarInt.WriteUnknownSize(field, field.Length);
            var sizePosition = _layout.SegmentDataPosition - _layout.SegmentSizeLength;
            RandomAccess.Write(handle, field, sizePosition);
        }

        // 3. Elements rewritten in place.
        foreach (var r in _regions.Where(r => r.Kind == RegionKind.New && r.Position < oldEnd))
            RandomAccess.Write(handle, r.Bytes!, r.Position);

        // 4. Free space: each run of adjacent Voids that contains new free space becomes a single Void element.
        for (var i = 0; i < _regions.Count; i++)
        {
            if (_regions[i].Kind != RegionKind.Void)
                continue;
            var start = i;
            long length = 0;
            var dirty = false;
            while (i < _regions.Count && _regions[i].Kind == RegionKind.Void)
            {
                length += _regions[i].Size;
                dirty |= _regions[i].Dirty;
                i++;
            }

            if (dirty)
                WriteVoid(handle, _regions[start].Position, length);
        }
    }

    private static void WriteVoid(SafeFileHandle handle, long position, long length)
    {
        var header = EbmlWriter.VoidHeader(length);
        RandomAccess.Write(handle, header, position);
        var zeros = new byte[(int)Math.Min(ZeroBlockSize, length)];
        var pos = position + header.Length;
        var remaining = length - header.Length;
        while (remaining > 0)
        {
            var n = (int)Math.Min(zeros.Length, remaining);
            RandomAccess.Write(handle, zeros.AsSpan(0, n), pos);
            pos += n;
            remaining -= n;
        }
    }
}
