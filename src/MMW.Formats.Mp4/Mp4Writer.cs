using System.Buffers.Binary;
using System.Text;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Metadata;

namespace MMW.Formats.Mp4;

/// <summary>Writes a <see cref="MediaDocument"/> back to an MP4 file.</summary>
/// <remarks>
/// Media data is never re-encoded or reordered. Three strategies are used, cheapest first:
/// <list type="number">
/// <item>moov is at the end of the file: rewrite the tail (extra data + moov) in place.</item>
/// <item>moov fits in its old slot plus adjacent padding: overwrite it, pad with <c>free</c>, append extra data.</item>
/// <item>otherwise: rewrite the whole file with moov first (fast start) and remapped chunk offsets.</item>
/// </list>
/// </remarks>
internal static class Mp4Writer
{
    /// <summary>Padding left after moov on a full rewrite, so later tag edits can be done in place.</summary>
    private const int RewritePadding = 4096;

    private sealed class Plan
    {
        public required Box Moov { get; init; }

        /// <summary>Extra media data (chapter samples) to store in a new mdat.</summary>
        public byte[] Extra { get; init; } = [];

        /// <summary>ID of the generated chapter track (0 when there is none).</summary>
        public uint ChapterTrackId { get; init; }
    }

    public static void Save(MediaDocument doc, SaveOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var source = doc.Path ?? throw new InvalidOperationException("The document has no source file.");
        var dest = options.OutputPath ?? source;
        var inPlace = string.Equals(Path.GetFullPath(dest), Path.GetFullPath(source), StringComparison.Ordinal);

        Mp4Layout layout;
        Plan plan;
        using (var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            layout = Mp4Layout.Read(fs);
            plan = BuildPlan(layout, doc, options);
        }

        if (inPlace && !options.Optimize && TryWriteInPlace(source, layout, plan, options))
        {
            AppLog.Info($"Updated '{Path.GetFileName(source)}' in place.");
        }
        else
        {
            Rewrite(source, dest, layout, plan, options, progress, ct);
            AppLog.Info($"Wrote '{Path.GetFileName(dest)}'.");
        }

        progress?.Report(1);
    }

    // ------------------------------------------------------------------ strategies

    private static bool TryWriteInPlace(string path, Mp4Layout layout, Plan plan, SaveOptions options)
    {
        var boxes = layout.Boxes.ToList();
        var moovIndex = boxes.FindIndex(b => b.Type == "moov");
        var referenced = ReferencedOffsets(plan);

        // Space we may overwrite: moov, padding, and mdat boxes that only held data of dropped tracks (old
        // chapter samples), both directly before and directly after moov.
        bool Reusable(TopLevelBox b) => b.IsPadding || (b.Type == "mdat" && !referenced.Any(o => o >= b.Offset && o < b.End));
        var first = moovIndex;
        while (first > 0 && Reusable(boxes[first - 1]) && boxes[first - 1].Type != "ftyp")
            first--;
        var last = moovIndex;
        while (last + 1 < boxes.Count && Reusable(boxes[last + 1]))
            last++;

        var slotStart = boxes[first].Offset;
        var slotEnd = boxes[last].End;
        var atEnd = last == boxes.Count - 1;
        var extraSize = plan.Extra.Length > 0 ? MdatHeaderSize(plan.Extra.Length) + plan.Extra.Length : 0;

        // Layout inside the slot: [moov][mdat extra][free]. The extra data offset depends on the moov size only
        // through stco/co64, so compute it in two passes.
        var moovBytes = Finalize(plan, _ => _, 0, options.Use64BitOffsets);
        moovBytes = Finalize(plan, _ => _, slotStart + moovBytes.Length + MdatHeaderSize(plan.Extra.Length), options.Use64BitOffsets);
        var used = moovBytes.Length + extraSize;
        var remaining = slotEnd - slotStart - used;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (atEnd)
        {
            // The slot is the tail of the file: just write the new tail and truncate.
            fs.Position = slotStart;
            fs.Write(moovBytes);
            WriteMdat(fs, plan.Extra);
            fs.SetLength(fs.Position);
            fs.Flush(flushToDisk: true);
            return true;
        }

        if (remaining == 0 || remaining >= 8)
        {
            fs.Position = slotStart;
            fs.Write(moovBytes);
            WriteMdat(fs, plan.Extra);
            WriteFree(fs, remaining);
            fs.Flush(flushToDisk: true);
            return true;
        }

        return false;
    }

    /// <summary>Chunk offsets of every track that is kept (the generated chapter track excluded).</summary>
    private static List<long> ReferencedOffsets(Plan plan)
    {
        var result = new List<long>();
        foreach (var trak in plan.Moov.FindAll("trak"))
        {
            if (plan.ChapterTrackId != 0 && HeaderBoxes.TkhdTrackId(trak.Find("tkhd")!) == plan.ChapterTrackId)
                continue;
            if (trak.FindPath("mdia/minf/stbl") is { } stbl)
                result.AddRange(SampleTable.ChunkOffsets(stbl));
        }

        return result;
    }

    private static void Rewrite(string source, string dest, Mp4Layout layout, Plan plan, SaveOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var ftyp = AdjustFtyp(layout.Ftyp, source, dest);
        var ftypBytes = ftyp is null ? [] : BoxWriter.ToArray(ftyp);
        var copied = layout.Boxes.Where(b => b.Type is not ("ftyp" or "moov") && !b.IsPadding).ToList();
        var copiedBytes = copied.Sum(b => b.Size);

        // The moov size depends on the offsets (stco vs co64), which depend on the moov size: iterate.
        var moovSize = BoxWriter.ToArray(plan.Moov).Length;
        byte[] moovBytes;
        long dataStart;
        var attempts = 0;
        while (true)
        {
            dataStart = ftypBytes.Length + moovSize + RewritePadding;
            var map = BuildOffsetMap(copied, dataStart);
            var extraOffset = dataStart + copiedBytes + MdatHeaderSize(plan.Extra.Length);
            moovBytes = Finalize(plan, map, extraOffset, options.Use64BitOffsets);
            if (moovBytes.Length == moovSize)
                break;
            moovSize = moovBytes.Length;
            if (++attempts > 4)
                throw new InvalidOperationException("Could not lay out the moov box.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dest))!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(dest) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                dst.SetLength(dataStart + copiedBytes + (plan.Extra.Length > 0 ? MdatHeaderSize(plan.Extra.Length) + plan.Extra.Length : 0));
                dst.Write(ftypBytes);
                dst.Write(moovBytes);
                WriteFree(dst, RewritePadding);

                var buffer = new byte[1 << 20];
                long done = 0;
                foreach (var box in copied)
                {
                    src.Position = box.Offset;
                    var left = box.Size;
                    while (left > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (n == 0)
                            throw new EndOfStreamException($"Unexpected end of file while copying '{box.Type}'.");
                        dst.Write(buffer, 0, n);
                        left -= n;
                        done += n;
                        progress?.Report(copiedBytes == 0 ? 1 : (double)done / copiedBytes);
                    }
                }

                WriteMdat(dst, plan.Extra);
                dst.Flush(flushToDisk: true);
            }

            File.Move(temp, dest, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static Func<long, long> BuildOffsetMap(List<TopLevelBox> copied, long dataStart)
    {
        var regions = new List<(long OldStart, long OldEnd, long Delta)>(copied.Count);
        var pos = dataStart;
        foreach (var b in copied)
        {
            regions.Add((b.Offset, b.End, pos - b.Offset));
            pos += b.Size;
        }

        return offset =>
        {
            foreach (var (start, end, delta) in regions)
            {
                if (offset >= start && offset < end)
                    return offset + delta;
            }

            // Zero-size samples may point exactly at the end of a box.
            foreach (var (start, end, delta) in regions)
            {
                if (offset == end)
                    return offset + delta;
            }

            throw new InvalidDataException($"Chunk offset {offset} does not point into any media data box.");
        };
    }

    // ------------------------------------------------------------------ plan

    private static Plan BuildPlan(Mp4Layout layout, MediaDocument doc, SaveOptions options)
    {
        var moov = layout.Moov.Loaded!;
        var traks = moov.FindAll("trak").ToList();
        var oldChapterIds = traks.SelectMany(t => Mp4Reader.References(t, "chap")).ToHashSet();

        var tracks = doc.Tracks.Where(t => t is not ChapterTrack).ToList();
        if (tracks.FirstOrDefault(t => t.IsPending) is { } pending)
            throw new NotSupportedException($"Adding tracks ('{pending.Format}') requires remuxing, which is not available yet.");

        var trakById = traks.ToDictionary(t => HeaderBoxes.TkhdTrackId(t.Find("tkhd")!));
        var keepIds = tracks.Select(t => t.Id).ToHashSet();

        // Rebuild the trak list in document order; drop removed tracks and old chapter tracks.
        var others = moov.Children!.Where(c => c.Type != "trak").ToList();
        var newTraks = new List<Box>();
        foreach (var track in tracks)
        {
            if (!trakById.TryGetValue(track.Id, out var trak))
                throw new InvalidDataException($"Track {track.Id} is no longer in the file.");
            ApplyTrack(trak, track, keepIds);
            newTraks.Add(trak);
        }

        foreach (var (id, trak) in trakById)
        {
            // Unknown tracks (timecode, hint, …) that the document doesn't list are kept, unless they were chapters.
            if (!keepIds.Contains(id) && !oldChapterIds.Contains(id) && doc.Tracks.All(t => t.Id != id) && !IsListedKind(trak))
                newTraks.Add(trak);
        }

        foreach (var trak in newTraks)
            RemoveReferences(trak, "chap");

        var mvhdIndex = others.FindIndex(c => c.Type == "mvhd");
        others.InsertRange(mvhdIndex + 1, newTraks);
        moov.Children = others;
        var mvhd = moov.Find("mvhd")!;

        // Chapters.
        var udta = moov.Find("udta");
        udta?.RemoveAll("chpl");
        byte[] extra = [];
        uint chapterId = 0;
        var chapters = doc.Chapters.OrderBy(c => c.Start).ToList();
        if (chapters.Count > 0)
        {
            chapterId = Math.Max(HeaderBoxes.MvhdNextTrackId(mvhd), newTraks.Select(t => HeaderBoxes.TkhdTrackId(t.Find("tkhd")!)).DefaultIfEmpty(0u).Max() + 1);
            var samples = chapters.Select(c => Mp4Chapters.EncodeSample(c.Title)).ToList();
            extra = samples.SelectMany(s => s).ToArray();
            var movieTimescale = HeaderBoxes.MvhdTimescale(mvhd);
            var duration = doc.Duration > TimeSpan.Zero ? doc.Duration : TimeSpan.FromSeconds((double)HeaderBoxes.MvhdDuration(mvhd) / Math.Max(1u, movieTimescale));
            var chapterTrak = Mp4Chapters.BuildTextTrack(chapterId, chapters, duration, movieTimescale, samples.Select(s => s.Length).ToList(), 0, options.Use64BitOffsets);
            var insertAt = moov.Children.FindLastIndex(c => c.Type == "trak") + 1;
            moov.Children.Insert(insertAt, chapterTrak);
            HeaderBoxes.SetMvhdNextTrackId(mvhd, chapterId + 1);

            // Reference the chapter track from the first video and the first audio track.
            foreach (var target in new[] { FirstWithHandler(newTraks, "vide"), FirstWithHandler(newTraks, "soun") })
            {
                if (target is not null)
                    SetReferences(target, "chap", [chapterId]);
            }

            udta = moov.GetOrAddContainer("udta");
            udta.Children!.Add(Mp4Chapters.BuildChpl(chapters));
        }

        ApplyMetadata(moov, doc, doc.ContainerState as Mp4State);
        return new Plan { Moov = moov, Extra = extra, ChapterTrackId = chapterId };
    }

    private static bool IsListedKind(Box trak)
    {
        var handler = trak.FindPath("mdia/hdlr") is { } h ? HeaderBoxes.HdlrType(h) : string.Empty;
        return handler is "vide" or "soun" or "sbtl" or "subt" or "text" or "subp" or "clcp";
    }

    private static Box? FirstWithHandler(IEnumerable<Box> traks, string handler) =>
        traks.FirstOrDefault(t => t.FindPath("mdia/hdlr") is { } h && HeaderBoxes.HdlrType(h) == handler);

    private static void ApplyTrack(Box trak, Track track, HashSet<uint> keepIds)
    {
        var tkhd = trak.Find("tkhd")!;
        var flags = HeaderBoxes.TkhdFlags(tkhd);
        flags = track.Enabled ? flags | HeaderBoxes.TrackEnabled | HeaderBoxes.TrackInMovie : flags & ~HeaderBoxes.TrackEnabled;
        HeaderBoxes.SetTkhdFlags(tkhd, flags);
        HeaderBoxes.SetTkhdAlternateGroup(tkhd, (short)track.AlternateGroup);

        switch (track)
        {
            case AudioTrack audio:
                HeaderBoxes.SetTkhdVolume(tkhd, audio.Volume);
                SetReferences(trak, "fall", audio.Fallback is { } f && keepIds.Contains(f.Id) ? [f.Id] : []);
                SetReferences(trak, "folw", audio.FollowsSubtitle is { } s && keepIds.Contains(s.Id) ? [s.Id] : []);
                break;
            case VideoTrack video:
                if (video.DisplayWidth > 0 && video.DisplayHeight > 0)
                    HeaderBoxes.SetTkhdSize(tkhd, video.DisplayWidth, video.DisplayHeight);
                if (trak.FindPath("mdia/minf/stbl/stsd")?.Children?.FirstOrDefault(e => BoxParser.IsVisualSampleEntry(e.Type)) is { IsContainer: true } entry)
                    ApplyColor(entry, video.Color);
                break;
            case SubtitleTrack sub:
                SetReferences(trak, "forc", sub.ForcedTrack is { } ft && keepIds.Contains(ft.Id) ? [ft.Id] : []);
                if (trak.FindPath("mdia/minf/stbl/stsd")?.Find("tx3g") is { Payload.Length: >= 12 } tx3g)
                {
                    var display = BinaryPrimitives.ReadUInt32BigEndian(tx3g.Payload.AsSpan(8)) & 0x3FFFFFFF;
                    display |= sub.ForcedMode switch
                    {
                        ForcedSubtitleMode.AllSamplesForced => 0x80000000u,
                        ForcedSubtitleMode.SomeSamplesForced => 0x40000000u,
                        _ => 0u,
                    };
                    BinaryPrimitives.WriteUInt32BigEndian(tx3g.Payload.AsSpan(8), display);
                }

                break;
        }

        // References to tracks that no longer exist.
        if (trak.Find("tref") is { } tref)
        {
            foreach (var r in tref.Children!.ToList())
            {
                var ids = Enumerable.Range(0, r.Payload.Length / 4)
                    .Select(i => BinaryPrimitives.ReadUInt32BigEndian(r.Payload.AsSpan(i * 4)))
                    .Where(id => r.Type == "chap" || keepIds.Contains(id))
                    .ToArray();
                SetReferences(trak, r.Type, ids);
            }
        }

        // Language: packed ISO 639-2/T in mdhd, full BCP-47 tag in elng when it carries more information.
        var mdia = trak.Find("mdia")!;
        var iso = LanguageTable.ToIso639_2T(track.Language);
        if (mdia.Find("mdhd") is { } mdhd)
            HeaderBoxes.SetMdhdLanguage(mdhd, HeaderBoxes.PackLanguage(iso));
        mdia.RemoveAll("elng");
        if (!string.Equals(LanguageTable.ToBcp47(iso), track.Language, StringComparison.OrdinalIgnoreCase) && track.Language != LanguageTable.Undetermined)
        {
            var elng = new Box("elng", new PayloadBuilder().FullBox(0, 0).Utf8(track.Language, nullTerminated: true).ToArray());
            var mdhdIndex = mdia.Children!.FindIndex(c => c.Type == "mdhd");
            mdia.Children.Insert(mdhdIndex + 1, elng);
        }

        // Name and media characteristics live in the track's udta.
        var udta = trak.GetOrAddContainer("udta");
        udta.RemoveAll("name");
        if (!string.IsNullOrEmpty(track.Name))
            udta.Children!.Add(new Box("name", Encoding.UTF8.GetBytes(track.Name)));
        udta.RemoveAll("tagc");
        foreach (var tag in track.MediaCharacteristics.Distinct(StringComparer.Ordinal))
            udta.Children!.Add(new Box("tagc", Encoding.UTF8.GetBytes(tag)));
        if (udta.Children!.Count == 0)
            trak.RemoveAll("udta");
    }

    /// <summary>Writes (or removes) the nclx colour box of a visual sample entry; ICC profiles are left alone.</summary>
    private static void ApplyColor(Box entry, ColorInfo color)
    {
        var existing = entry.Children!.FirstOrDefault(c => c.Type == "colr");
        var kind = existing is { Payload.Length: >= 4 } ? Box.Latin1.GetString(existing.Payload, 0, 4) : null;
        if (kind is "prof" or "rICC")
            return;

        if (!color.IsSpecified)
        {
            entry.RemoveAll("colr");
            return;
        }

        var builder = new PayloadBuilder().Type("nclx").U16(color.Primaries).U16(color.Transfer).U16(color.Matrix).U8(color.FullRange == true ? 0x80 : 0);
        var box = new Box("colr", builder.ToArray());
        var index = existing is null ? -1 : entry.Children!.IndexOf(existing);
        if (index >= 0)
        {
            entry.Children![index] = box;
        }
        else
        {
            // Keep the codec configuration (avcC/hvcC/…) first, as some players expect.
            var after = entry.Children!.FindLastIndex(c => c.Type is "avcC" or "hvcC" or "av1C" or "vvcC" or "esds" or "dvcC" or "dvvC" or "pasp");
            entry.Children.Insert(after + 1, box);
        }
    }

    private static void RemoveReferences(Box trak, string type) => SetReferences(trak, type, []);

    private static void SetReferences(Box trak, string type, IReadOnlyList<uint> ids)
    {
        var tref = trak.Find("tref");
        if (ids.Count == 0)
        {
            if (tref is null)
                return;
            tref.RemoveAll(type);
            if (tref.Children!.Count == 0)
                trak.RemoveAll("tref");
            return;
        }

        if (tref is null)
        {
            tref = new Box("tref", null, []);
            var index = trak.Children!.FindIndex(c => c.Type == "tkhd");
            trak.Children.Insert(index + 1, tref);
        }

        var b = new PayloadBuilder();
        foreach (var id in ids)
            b.U32(id);
        tref.SetChild(new Box(type, b.ToArray()));
    }

    private static void ApplyMetadata(Box moov, MediaDocument doc, Mp4State? state)
    {
        var ilst = ItunesMetadata.Build(doc.Metadata, state?.PreservedItems ?? []);
        var udta = moov.Find("udta");
        var meta = udta?.Find("meta");

        if (ilst.Children!.Count == 0)
        {
            if (meta is null)
                return;
            meta.RemoveAll("ilst");
            if (meta.Children!.All(c => c.Type is "hdlr" or "free"))
                udta!.RemoveAll("meta");
            if (udta!.Children!.Count == 0)
                moov.RemoveAll("udta");
            return;
        }

        udta ??= moov.GetOrAddContainer("udta");
        if (meta is null)
        {
            meta = new Box("meta", new byte[4], [
                new Box("hdlr", new PayloadBuilder().FullBox(0, 0).U32(0).Type("mdir").Type("appl").U32(0).U32(0).U8(0).ToArray()),
            ]);
            udta.Children!.Add(meta);
        }

        meta.Children ??= [];
        var index = meta.Children.FindIndex(c => c.Type == "ilst");
        if (index >= 0)
            meta.Children[index] = ilst;
        else
            meta.Children.Add(ilst);
    }

    // ------------------------------------------------------------------ finalizing

    /// <summary>Serializes a copy of the planned moov with mapped chunk offsets.</summary>
    private static byte[] Finalize(Plan plan, Func<long, long> map, long extraOffset, bool force64)
    {
        var moov = BoxParser.ParseSingle(BoxWriter.ToArray(plan.Moov));
        foreach (var trak in moov.FindAll("trak"))
        {
            var stbl = trak.FindPath("mdia/minf/stbl");
            if (stbl is null)
                continue;
            var id = HeaderBoxes.TkhdTrackId(trak.Find("tkhd")!);
            if (id == plan.ChapterTrackId && plan.ChapterTrackId != 0)
                SampleTable.RemapChunkOffsets(stbl, _ => extraOffset, force64);
            else
                SampleTable.RemapChunkOffsets(stbl, map, force64);
        }

        return BoxWriter.ToArray(moov);
    }

    private static Box? AdjustFtyp(Box? ftyp, string source, string dest)
    {
        if (ftyp is null)
            return null;
        var srcExt = Path.GetExtension(source).ToLowerInvariant();
        var destExt = Path.GetExtension(dest).ToLowerInvariant();
        if (srcExt == destExt)
            return ftyp;

        string? major = destExt switch
        {
            ".m4v" => "M4V ",
            ".m4a" or ".m4r" => "M4A ",
            ".m4b" => "M4B ",
            ".mp4" => "mp42",
            _ => null,
        };
        if (major is null)
            return ftyp;

        var p = ftyp.Payload;
        var brands = new List<string>();
        for (var i = 8; i + 4 <= p.Length; i += 4)
            brands.Add(Box.Latin1.GetString(p, i, 4));
        foreach (var b in new[] { major, "mp42", "isom" })
        {
            if (!brands.Contains(b))
                brands.Add(b);
        }

        var builder = new PayloadBuilder().Type(major).U32(0);
        foreach (var b in brands)
            builder.Type(b);
        return new Box("ftyp", builder.ToArray());
    }

    private static int MdatHeaderSize(long length) => length + 8 > uint.MaxValue ? 16 : 8;

    private static void WriteMdat(Stream stream, byte[] data)
    {
        if (data.Length == 0)
            return;
        stream.Write(BoxWriter.Header("mdat", data.Length + MdatHeaderSize(data.Length)));
        stream.Write(data);
    }

    private static void WriteFree(Stream stream, long size)
    {
        if (size == 0)
            return;
        stream.Write(BoxWriter.Header("free", size));
        var zeros = new byte[Math.Min(size - 8, 64 * 1024)];
        var left = size - 8;
        while (left > 0)
        {
            var n = (int)Math.Min(zeros.Length, left);
            stream.Write(zeros, 0, n);
            left -= n;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
