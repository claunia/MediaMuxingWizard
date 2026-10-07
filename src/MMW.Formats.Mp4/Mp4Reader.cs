using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MMW.Core.Languages;
using MMW.Core.Model;
using MMW.Formats.Mp4.Boxes;
using MMW.Formats.Mp4.Metadata;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4;

/// <summary>Builds a <see cref="MediaDocument"/> from an MP4 file.</summary>
internal static class Mp4Reader
{
    public static MediaDocument Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var layout = Mp4Layout.Read(fs);
        var moov = layout.Moov.Loaded!;
        var state = new Mp4State { Layout = layout, Moov = moov };
        var doc = new MediaDocument(path, ContainerKind.Mp4) { ContainerState = state, FileSize = layout.FileLength };

        var mvhd = moov.Find("mvhd") ?? throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Error_NoBox, "mvhd"));
        var movieTimescale = HeaderBoxes.MvhdTimescale(mvhd);
        if (movieTimescale > 0)
            doc.Duration = TimeSpan.FromSeconds((double)HeaderBoxes.MvhdDuration(mvhd) / movieTimescale);

        var traks = moov.FindAll("trak").ToList();

        // Chapter tracks are the targets of 'chap' references.
        foreach (var trak in traks)
        {
            foreach (var id in References(trak, "chap"))
                state.ChapterTrackIds.Add(id);
        }

        var byId = new Dictionary<uint, (Track Track, Box Trak)>();
        foreach (var trak in traks)
        {
            var tkhd = trak.Find("tkhd");
            if (tkhd is null)
                continue;
            var id = HeaderBoxes.TkhdTrackId(tkhd);
            if (state.ChapterTrackIds.Contains(id))
                continue;
            var track = ReadTrack(trak, tkhd, path);
            doc.Tracks.Add(track);
            byId[id] = (track, trak);
        }

        // Cross-track references.
        foreach (var (track, trak) in byId.Values)
        {
            if (track is AudioTrack audio)
            {
                audio.Fallback = References(trak, "fall").Select(r => byId.GetValueOrDefault(r).Track).FirstOrDefault(t => t is not null);
                audio.FollowsSubtitle = References(trak, "folw").Select(r => byId.GetValueOrDefault(r).Track).FirstOrDefault(t => t is not null);
            }
            else if (track is SubtitleTrack sub)
            {
                sub.ForcedTrack = References(trak, "forc").Select(r => byId.GetValueOrDefault(r).Track).FirstOrDefault(t => t is not null);
            }
        }

        // Chapters: prefer the QuickTime text track; fall back to Nero chpl.
        // Chapter references can point at a text track and a JPEG preview-image track.
        var chapterTraks = traks.Where(t => t.Find("tkhd") is { } h && state.ChapterTrackIds.Contains(HeaderBoxes.TkhdTrackId(h))).ToList();
        var chapterTrak = chapterTraks.FirstOrDefault(t => t.FindPath("mdia/hdlr") is { } h && HeaderBoxes.HdlrType(h) != "vide") ?? chapterTraks.FirstOrDefault(t => t.FindPath("mdia/hdlr") is null);
        var imageTrak = chapterTraks.FirstOrDefault(t => t.FindPath("mdia/hdlr") is { } h && HeaderBoxes.HdlrType(h) == "vide");
        var chapters = chapterTrak is not null ? Mp4Chapters.ReadTextTrack(chapterTrak, fs, movieTimescale) : [];
        if (chapters.Count == 0 && moov.FindPath("udta/chpl") is { } chpl)
            chapters = Mp4Chapters.ReadChpl(chpl);
        if (imageTrak is not null && chapters.Count > 0)
        {
            var images = Mp4Chapters.ReadImageTrack(imageTrak, fs);
            for (var i = 0; i < chapters.Count && i < images.Count; i++)
                chapters[i].Thumbnail = images[i].Length > 0 ? images[i] : null;
        }

        foreach (var c in chapters)
            doc.Chapters.Add(c);
        if (chapters.Count > 0)
        {
            doc.Tracks.Add(new ChapterTrack
            {
                Id = chapterTrak is not null ? HeaderBoxes.TkhdTrackId(chapterTrak.Find("tkhd")!) : 0,
                Name = "Chapters",
                Language = "en",
                Duration = doc.Duration,
                FormatDetails = string.Format(CultureInfo.CurrentCulture, Strings.Details_ChapterCount, chapters.Count),
                Enabled = false,
            });
        }

        if (moov.FindPath("udta/meta/ilst") is { } ilst)
            ItunesMetadata.Read(ilst, doc.Metadata, state.PreservedItems);

        doc.IsDirty = false;
        return doc;
    }

    public static IEnumerable<uint> References(Box trak, string type)
    {
        var refBox = trak.FindPath("tref/" + type);
        if (refBox is null)
            yield break;
        for (var i = 0; i + 4 <= refBox.Payload.Length; i += 4)
            yield return BinaryPrimitives.ReadUInt32BigEndian(refBox.Payload.AsSpan(i));
    }

    private static Track ReadTrack(Box trak, Box tkhd, string path)
    {
        var mdia = trak.Find("mdia");
        var handler = mdia?.Find("hdlr") is { } hdlr ? HeaderBoxes.HdlrType(hdlr) : string.Empty;
        var entry = trak.FindPath("mdia/minf/stbl/stsd")?.Children?.FirstOrDefault();

        Track track = handler switch
        {
            "vide" => new VideoTrack(),
            "soun" => new AudioTrack(),
            "sbtl" or "subt" or "text" or "subp" => new SubtitleTrack(),
            "clcp" => new ClosedCaptionTrack(),
            _ => new OtherTrack(),
        };

        // VobSub in MP4 uses an 'mp4s' entry under a generic handler.
        if (track is OtherTrack && entry?.Type == "mp4s")
            track = new SubtitleTrack();

        var id = HeaderBoxes.TkhdTrackId(tkhd);
        track.Id = id;
        track.Source = new TrackSource(path, ContainerKind.Mp4, id);
        track.Enabled = (HeaderBoxes.TkhdFlags(tkhd) & HeaderBoxes.TrackEnabled) != 0;
        track.IsDefault = track.Enabled;
        track.AlternateGroup = HeaderBoxes.TkhdAlternateGroup(tkhd);

        if (mdia?.Find("mdhd") is { } mdhd)
        {
            track.Timescale = HeaderBoxes.MdhdTimescale(mdhd);
            if (track.Timescale > 0)
                track.Duration = TimeSpan.FromSeconds((double)HeaderBoxes.MdhdDuration(mdhd) / track.Timescale);
            track.Language = LanguageTable.ToBcp47(HeaderBoxes.UnpackLanguage(HeaderBoxes.MdhdLanguage(mdhd)));
        }

        // Extended language (BCP-47) overrides the packed ISO code.
        if (mdia?.Find("elng") is { Payload.Length: > 4 } elng)
        {
            var tag = Encoding.UTF8.GetString(elng.Payload, 4, elng.Payload.Length - 4).TrimEnd('\0');
            if (tag.Length > 0)
                track.Language = tag;
        }

        if (trak.FindPath("udta/name") is { } name)
            track.Name = Encoding.UTF8.GetString(name.Payload).TrimEnd('\0');

        foreach (var tagc in trak.FindPath("udta")?.FindAll("tagc") ?? [])
        {
            var value = Encoding.UTF8.GetString(tagc.Payload).TrimEnd('\0');
            if (value.Length > 0 && !track.MediaCharacteristics.Contains(value))
                track.MediaCharacteristics.Add(value);
        }

        if (trak.FindPath("mdia/minf/stbl") is { } stbl)
        {
            var (_, bytes) = SampleTable.Summary(stbl);
            track.DataLength = bytes;
            if (track.Duration.TotalSeconds > 0)
                track.Bitrate = (long)(bytes * 8 / track.Duration.TotalSeconds);
        }

        if (entry is not null)
        {
            track.CodecId = entry.Type;
            track.Format = CodecInfo.FormatName(entry);
            switch (track)
            {
                case VideoTrack v:
                    CodecInfo.DescribeVideo(entry, v);
                    var (w, h) = HeaderBoxes.TkhdSize(tkhd);
                    v.DisplayWidth = w;
                    v.DisplayHeight = h;
                    if (track.Duration.TotalSeconds > 0 && trak.FindPath("mdia/minf/stbl") is { } vstbl)
                        v.FrameRate = SampleTable.Summary(vstbl).Count / track.Duration.TotalSeconds;
                    break;
                case AudioTrack a:
                    CodecInfo.DescribeAudio(entry, a);
                    a.Volume = HeaderBoxes.TkhdVolume(tkhd);
                    break;
                case SubtitleTrack s:
                    CodecInfo.DescribeSubtitle(entry, s, tkhd);
                    break;
            }
        }

        return track;
    }
}
