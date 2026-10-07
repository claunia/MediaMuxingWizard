using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MMW.Core.Diagnostics;
using MMW.Core.Languages;
using MMW.Core.Media;
using MMW.Core.Media.Codecs;
using MMW.Core.Metadata;
using MMW.Core.Model;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;
using static MMW.Formats.Matroska.Media.MatroskaMediaIds;

namespace MMW.Formats.Matroska.Media;

/// <summary>
/// Writes a Matroska/WebM file from interleaved samples.
/// </summary>
/// <remarks>
/// Layout: EBML header, Segment (size patched at the end) with a reserved SeekHead, Info (Duration patched at the
/// end), Tracks, Chapters, Attachments and Tags — each followed by Void padding so later in-place metadata edits
/// fit — then Clusters (started at video key frames at most every second and at least every 5 s; SimpleBlocks, or
/// BlockGroups with BlockDuration for subtitles; no lacing) and Cues for the key frames. Block timestamps are
/// presentation times in milliseconds (TimestampScale 1 ms); audio pre-roll becomes CodecDelay.
/// </remarks>
internal sealed class MatroskaMuxer : IMuxer
{
    private const ulong TimestampScaleNs = 1_000_000;
    private const int SeekHeadReserve = 256;
    private const long MaxClusterMs = 5000;
    private const long MinVideoClusterMs = 1000;
    private const long MaxClusterBytes = 32L * 1024 * 1024;

    private readonly Stream _out;
    private readonly MuxerSettings _settings;
    private readonly List<TrackState> _tracks = [];
    private readonly List<(long TimeMs, uint Track, long ClusterPosition, long Relative)> _cues = [];
    private readonly Dictionary<ulong, long> _seek = [];
    private long _segmentStart;
    private long _segmentDataStart;
    private long _seekHeadPosition;
    private long _durationPosition;
    private bool _headerWritten;
    private long _clusterStart = -1;
    private long _clusterDataStart;
    private long _clusterTimestamp;
    private long _maxEndMs;
    private long _tagsPosition;
    private long _tagsRegion;
    private byte[] _buffer = new byte[64 * 1024];
    private bool _finished;

    public MatroskaMuxer(Stream output, MuxerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);
        if (!output.CanSeek || !output.CanWrite)
            throw new ArgumentException("The output stream must be seekable and writable.", nameof(output));
        _out = output;
        _settings = settings;
    }

    public TimeSpan InterleaveDuration => TimeSpan.Zero;

    /// <inheritdoc />
    public bool SupportsVideoPreRoll => false;

    private bool IsWebM => Path.GetExtension(_settings.OutputPath).Equals(".webm", StringComparison.OrdinalIgnoreCase);

    private sealed class TrackState
    {
        public required CodecConfig Config { get; init; }

        public required MuxTrackSettings Settings { get; init; }

        public uint Number { get; init; }

        public ulong Uid { get; set; }

        /// <summary>Added to presentation times (ms) so pre-roll starts at zero.</summary>
        public long ShiftMs { get; init; }

        public bool IsVideo => Config.Kind == TrackKind.Video;

        public bool ConvertTx3g { get; init; }

        /// <summary>D_WEBVTT blocks start with the (empty) cue identifier and settings lines.</summary>
        public bool WebVttLines { get; init; }

        public long Frames { get; set; }

        public long Bytes { get; set; }

        public long FirstMs { get; set; } = long.MaxValue;

        public long EndMs { get; set; }

        /// <summary>Timestamp of the previous block (the ReferenceBlock of a BlockGroup that is not a key frame).</summary>
        public long LastPtsMs { get; set; } = -1;
    }

    public int AddTrack(CodecConfig config, MuxTrackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        if (_headerWritten)
            throw new InvalidOperationException("Tracks must be added before samples are written.");
        var support = MatroskaCodecMapping.CheckSupport(config);
        if (!support.CanMux)
            throw new NotSupportedException($"{config.FormatName} cannot be written to Matroska: {support.Reason}");

        if (IsWebM && config.Codec is not (CodecType.Vp8 or CodecType.Vp9 or CodecType.Av1 or CodecType.Opus or CodecType.Vorbis or CodecType.WebVtt))
            throw new NotSupportedException($"{config.FormatName} cannot be stored in WebM (only VP8, VP9, AV1, Opus, Vorbis and WebVTT); save as .mkv instead.");

        var shift = config.Kind == TrackKind.Audio ? (long)Math.Round(settings.PreRoll.TotalMilliseconds) : 0;
        _tracks.Add(new TrackState
        {
            Config = config,
            Settings = settings,
            Number = (uint)_tracks.Count + 1,
            ShiftMs = shift,
            ConvertTx3g = config.Codec == CodecType.Tx3g,
            WebVttLines = config.Codec == CodecType.WebVtt &&
                          (config.Native is MatroskaNativeTrack ? config.SourceCodecId : MatroskaCodecMapping.CodecIdFor(config))!.StartsWith("D_WEBVTT", StringComparison.Ordinal),
        });
        return _tracks.Count - 1;
    }

    // ------------------------------------------------------------------ header

    private void EnsureHeader()
    {
        if (_headerWritten)
            return;
        _headerWritten = true;
        _out.SetLength(0);
        _out.Position = 0;

        var ebml = new EbmlWriter();
        ebml.Master(EbmlHeader, h =>
        {
            h.UInt(EbmlVersion, 1);
            h.UInt(EbmlReadVersion, 1);
            h.UInt(EbmlMaxIdLength, 4);
            h.UInt(EbmlMaxSizeLength, 8);
            h.String(DocType, IsWebM ? "webm" : "matroska");
            h.UInt(DocTypeVersion, 4);
            h.UInt(DocTypeReadVersion, 2);
        });
        _out.Write(ebml.WrittenSpan);

        // Segment with an 8-byte size, patched in Finish.
        _segmentStart = _out.Position;
        Span<byte> header = stackalloc byte[12];
        var n = EbmlVarInt.WriteId(header, Segment);
        n += EbmlVarInt.WriteUnknownSize(header[n..], 8);
        _out.Write(header[..n]);
        _segmentDataStart = _out.Position;

        _seekHeadPosition = _out.Position;
        WriteVoid(SeekHeadReserve);

        WriteInfo();
        WriteVoid(128);
        _seek[Tracks] = _out.Position - _segmentDataStart;
        _out.Write(EbmlWriter.Element(Tracks, BuildTracks()));
        WriteVoid(1024);

        var document = _settings.Document;
        if (document.Chapters.Count > 0)
        {
            _seek[Chapters] = _out.Position - _segmentDataStart;
            _out.Write(EbmlWriter.Element(Chapters, MatroskaChapters.Build(document.Chapters, document.Duration)));
            WriteVoid(512);
        }

        if (BuildAttachments() is { } attachments)
        {
            _seek[Attachments] = _out.Position - _segmentDataStart;
            _out.Write(EbmlWriter.Element(Attachments, attachments));
        }

        // Tags: the global tags now; the region is rewritten in Finish with the per-track statistics added.
        _tagsPosition = _out.Position;
        var tags = BuildTags(statistics: false);
        if (tags.Length > 0)
        {
            _seek[Tags] = _out.Position - _segmentDataStart;
            _out.Write(EbmlWriter.Element(Tags, tags));
        }

        WriteVoid(2048 + 192L * _tracks.Count);
        _tagsRegion = _out.Position - _tagsPosition;
    }

    private void WriteInfo()
    {
        var w = new EbmlWriter();
        var uuid = new byte[16];
        RandomNumberGenerator.Fill(uuid);
        w.Binary(SegmentUuid, uuid);
        w.UInt(TimestampScale, TimestampScaleNs);
        var version = typeof(MatroskaMuxer).Assembly.GetName().Version?.ToString(3) ?? "0";
        w.String(MuxingApp, "MediaMetadataWizard " + version);
        w.String(WritingApp, "MediaMetadataWizard " + version);
        w.Int(DateUtc, (DateTime.UtcNow - EbmlParser.MatroskaEpoch).Ticks * 100);
        if (_settings.Document.Metadata.GetString(TagId.Name) is { Length: > 0 } title)
            w.String(Title, title);
        var durationOffset = w.Length;
        w.Float(MatroskaIds.Duration, 0);
        var payload = w.ToArray();

        _seek[Info] = _out.Position - _segmentDataStart;
        var element = EbmlWriter.Element(Info, payload);
        var headerLength = element.Length - payload.Length;

        // Position of the 8-byte float value (after the 2-byte ID and the 1-byte size).
        _durationPosition = _out.Position + headerLength + durationOffset + 3;
        _out.Write(element);
    }

    private byte[] BuildTracks()
    {
        var w = new EbmlWriter();
        var usedUids = new HashSet<ulong>();
        foreach (var t in _tracks)
        {
            var entry = t.Config.Native is MatroskaNativeTrack native ? RewriteNative(t, native) : BuildEntry(t);
            var uid = EbmlParser.Children(entry).GetUInt(TrackUid, 0);
            if (uid == 0 || !usedUids.Add(uid))
            {
                uid = MatroskaChapters.RandomUid();
                usedUids.Add(uid);
                entry = MatroskaTrackWriter.ApplyReplacements(EbmlParser.Children(entry), new() { [TrackUid] = Encode(x => x.UInt(TrackUid, uid)) });
            }

            t.Uid = uid;
            w.Binary(TrackEntry, entry);
        }

        return w.ToArray();
    }

    /// <summary>Reuses the source TrackEntry: new number, decoded content, the document's edits applied.</summary>
    private static byte[] RewriteNative(TrackState t, MatroskaNativeTrack native)
    {
        var payload = native.Payload;
        if (t.Settings.Model is { } model)
        {
            var children = EbmlParser.Children(payload);
            var state = new TrackEntryState
            {
                TrackNumber = children.GetUInt(TrackNumber, 0),
                TrackUid = children.GetUInt(TrackUid, 0),
                Payload = payload,
                Snapshot = TrackEditState.Capture(native.SourceTrack),
            };
            payload = MatroskaTrackWriter.Rewrite(state, model) ?? payload;
        }

        if (t.Config.Kind == TrackKind.Video && (t.Config.StreamColor.IsSpecified || t.Config.StreamHdr is not null))
            payload = MatroskaTrackWriter.AddMissingColour(payload, t.Config.StreamColor, t.Config.StreamHdr);

        var replacements = new Dictionary<ulong, byte[]?>
        {
            [TrackNumber] = Encode(x => x.UInt(TrackNumber, t.Number)),
            [ContentEncodings] = null,
            [FlagLacing] = Encode(x => x.UInt(FlagLacing, 0)),
        };
        if (t.Settings.PreRoll > TimeSpan.Zero && t.Config.Codec != CodecType.Opus && t.Config.Kind == TrackKind.Audio)
            replacements[CodecDelay] = Encode(x => x.UInt(CodecDelay, (ulong)(t.Settings.PreRoll.Ticks * 100)));
        return MatroskaTrackWriter.ApplyReplacements(EbmlParser.Children(payload), replacements);
    }

    private static byte[] BuildEntry(TrackState t)
    {
        var c = t.Config;
        var model = t.Settings.Model;
        var w = new EbmlWriter();
        w.UInt(TrackNumber, t.Number);
        w.UInt(TrackUid, MatroskaChapters.RandomUid());
        w.UInt(TrackType, c.Kind switch
        {
            TrackKind.Video => (ulong)MatroskaTrackParser.TrackTypeVideo,
            TrackKind.Audio => (ulong)MatroskaTrackParser.TrackTypeAudio,
            _ => (ulong)MatroskaTrackParser.TrackTypeSubtitle,
        });

        var characteristics = model?.MediaCharacteristics ?? [];
        var isDefault = model?.IsDefault ?? true;
        var forced = (model?.IsForced ?? false) || characteristics.Contains(MediaCharacteristics.ForcedOnly) ||
                     model is SubtitleTrack { ForcedMode: ForcedSubtitleMode.AllSamplesForced };
        if (!isDefault)
            w.UInt(FlagDefault, 0);
        if (forced)
            w.UInt(FlagForced, 1);
        if (characteristics.Contains(MediaCharacteristics.TranscribesSpokenDialog) || characteristics.Contains(MediaCharacteristics.DescribesMusicAndSound))
            w.UInt(FlagHearingImpaired, 1);
        if (characteristics.Contains(MediaCharacteristics.DescribesVideo))
            w.UInt(FlagVisualImpaired, 1);
        if (characteristics.Contains(MediaCharacteristics.OriginalContent))
            w.UInt(FlagOriginal, 1);
        if (characteristics.Contains(MediaCharacteristics.AuxiliaryContent))
            w.UInt(FlagCommentary, 1);
        w.UInt(FlagLacing, 0);

        if (c.Kind is TrackKind.Video or TrackKind.Audio && c.DefaultSampleDuration > 0 && c.Timescale > 0)
            w.UInt(DefaultDuration, (ulong)Math.Round(c.DefaultSampleDuration * 1e9 / c.Timescale));
        else if (c.Kind == TrackKind.Video && c.FrameRate > 0)
            w.UInt(DefaultDuration, (ulong)Math.Round(1e9 / c.FrameRate));

        var name = model?.Name ?? c.Name;
        if (!string.IsNullOrEmpty(name))
            w.String(Name, name);
        var language = model?.Language ?? c.Language;
        if (string.IsNullOrWhiteSpace(language))
            language = LanguageTable.Undetermined;
        w.String(TrackLanguage, LanguageTable.ToIso639_2B(language));
        w.String(LanguageBcp47, language);

        w.String(CodecId, MatroskaCodecMapping.CodecIdFor(c)!);
        if (MatroskaCodecMapping.CodecPrivateFor(c) is { Length: > 0 } priv)
            w.Binary(CodecPrivate, priv);
        var delay = c.Codec == CodecType.Opus ? c.CodecDelay : c.Kind == TrackKind.Audio ? t.Settings.PreRoll : TimeSpan.Zero;
        if (delay > TimeSpan.Zero)
            w.UInt(CodecDelay, (ulong)(delay.Ticks * 100));
        if (c.SeekPreRoll > TimeSpan.Zero || c.Codec == CodecType.Opus)
            w.UInt(SeekPreRoll, (ulong)((c.SeekPreRoll > TimeSpan.Zero ? c.SeekPreRoll : TimeSpan.FromMilliseconds(80)).Ticks * 100));

        if (c.Hdr10PlusInBlockAdditions)
        {
            w.UInt(MaxBlockAdditionId, BlockAddition.ItuT35);
            w.Master(BlockAdditionMapping, m =>
            {
                m.UInt(BlockAddIdValue, BlockAddition.ItuT35);
                m.String(BlockAddIdName, "ITU-T T.35 metadata (HDR10+)");
                m.UInt(BlockAddIdType, BlockAddTypeItuT35);
            });
        }

        if (c.DolbyVisionConfig is { Length: >= 5 } dv)
        {
            var profile = dv[2] >> 1;
            w.Master(BlockAdditionMapping, m =>
            {
                m.String(BlockAddIdName, "Dolby Vision configuration");
                m.UInt(BlockAddIdType, profile <= 7 ? BlockAddTypeDvcC : BlockAddTypeDvvC);
                m.Binary(BlockAddIdExtraData, dv);
            });
        }

        switch (c.Kind)
        {
            case TrackKind.Video:
                w.Master(Video, v => WriteVideo(v, c, model as VideoTrack));
                break;
            case TrackKind.Audio:
                w.Master(Audio, a =>
                {
                    a.Float(SamplingFrequency, c.SampleRate > 0 ? c.SampleRate : c.Timescale);
                    a.UInt(Channels, (ulong)Math.Max(1, c.Channels));
                    if (c.BitsPerSample > 0 && c.Codec is CodecType.Pcm or CodecType.Flac or CodecType.Alac)
                        a.UInt(BitDepth, (ulong)c.BitsPerSample);
                });
                break;
        }

        return w.ToArray();
    }

    private static void WriteVideo(EbmlWriter v, CodecConfig c, VideoTrack? model)
    {
        v.UInt(PixelWidth, (ulong)Math.Max(1, c.Width));
        v.UInt(PixelHeight, (ulong)Math.Max(1, c.Height));
        int parN = c.ParNumerator, parD = c.ParDenominator;
        if (model is { ParNumerator: > 0, ParDenominator: > 0 })
            (parN, parD) = (model.ParNumerator, model.ParDenominator);
        if (model is { DisplayWidth: > 0, DisplayHeight: > 0 } &&
            ((int)Math.Round(model.DisplayWidth) != c.Width || (int)Math.Round(model.DisplayHeight) != c.Height))
        {
            v.UInt(DisplayWidth, (ulong)Math.Round(model.DisplayWidth));
            v.UInt(DisplayHeight, (ulong)Math.Round(model.DisplayHeight));
        }
        else if (parN > 0 && parD > 0 && parN != parD)
        {
            v.UInt(DisplayWidth, (ulong)Math.Round(c.Width * (double)parN / parD));
            v.UInt(DisplayHeight, (ulong)c.Height);
        }

        // The edited (or container) colour; "implicit" and missing values fall back to what the bitstream carries.
        var color = model is not null ? (model.Color.IsSpecified ? model.Color : c.StreamColor) : c.EffectiveColor;
        var hdr = HdrInfo.Merge(model?.Hdr ?? c.Hdr, c.StreamHdr);
        if (!color.IsSpecified && hdr is null)
            return;
        v.Master(Colour, col =>
        {
            if (color.IsSpecified)
            {
                col.UInt(MatrixCoefficients, (ulong)color.Matrix);
                col.UInt(TransferCharacteristics, (ulong)color.Transfer);
                col.UInt(Primaries, (ulong)color.Primaries);
                if (color.FullRange is { } full)
                    col.UInt(ColourRange, full ? 2UL : 1UL);
            }

            if (hdr?.MaxCll is { } cll)
                col.UInt(MaxCll, (ulong)cll);
            if (hdr?.MaxFall is { } fall)
                col.UInt(MaxFall, (ulong)fall);
            if (hdr is { DisplayPrimaries: { Length: 3 } p })
            {
                col.Master(MasteringMetadata, m =>
                {
                    m.Float(PrimaryRChromaticityX, p[0].X);
                    m.Float(PrimaryRChromaticityY, p[0].Y);
                    m.Float(PrimaryGChromaticityX, p[1].X);
                    m.Float(PrimaryGChromaticityY, p[1].Y);
                    m.Float(PrimaryBChromaticityX, p[2].X);
                    m.Float(PrimaryBChromaticityY, p[2].Y);
                    if (hdr.WhitePoint is { } wp)
                    {
                        m.Float(WhitePointChromaticityX, wp.X);
                        m.Float(WhitePointChromaticityY, wp.Y);
                    }

                    if (hdr.MaxLuminance is { } max)
                        m.Float(LuminanceMax, max);
                    if (hdr.MinLuminance is { } min)
                        m.Float(LuminanceMin, min);
                });
            }
        });
    }

    private byte[] BuildTags(bool statistics)
    {
        var document = _settings.Document;
        var layout = document.ContainerState as MatroskaLayout;
        var w = new EbmlWriter();
        foreach (var tag in MatroskaTagMapping.Write(document.Metadata, layout?.PreservedBinaryTags ?? []))
        {
            if (tag.SimpleTags.Count > 0 || tag.RawSimpleTags.Count > 0)
                tag.Write(w);
        }

        if (!statistics)
            return w.ToArray();

        // Statistics tags as mkvmerge writes them (players use DURATION for the track duration).
        var app = "MediaMetadataWizard " + (typeof(MatroskaMuxer).Assembly.GetName().Version?.ToString(3) ?? "0");
        foreach (var t in _tracks)
        {
            // DURATION spans the first to the last presented instant (mkvmerge's definition).
            var durationMs = t.Frames == 0 ? 0 : Math.Max(0, t.EndMs - Math.Min(t.FirstMs, t.EndMs));
            var duration = TimeSpan.FromMilliseconds(durationMs);
            var bps = durationMs > 0 ? t.Bytes * 8000 / durationMs : 0;
            w.Master(Tag, tag =>
            {
                tag.Master(Targets, targets =>
                {
                    targets.UInt(TargetTypeValue, 50);
                    targets.UInt(TagTrackUid, t.Uid);
                });
                void Simple(string name, string value) => tag.Master(SimpleTag, st =>
                {
                    st.String(TagName, name);
                    st.String(TagString, value);
                });
                Simple("BPS", bps.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Simple("DURATION", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds * 1_000_000:000000000}"));
                Simple("NUMBER_OF_FRAMES", t.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Simple("NUMBER_OF_BYTES", t.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Simple("_STATISTICS_WRITING_APP", app);
                Simple("_STATISTICS_TAGS", "BPS DURATION NUMBER_OF_FRAMES NUMBER_OF_BYTES");
            });
        }

        return w.ToArray();
    }

    private byte[]? BuildAttachments()
    {
        var document = _settings.Document;
        var artworks = document.Metadata.Artworks;

        // Attachments of the source Matroska file (fonts, …) are carried over verbatim.
        if (document.ContainerState is MatroskaLayout layout && document.Path is { } path && File.Exists(path) &&
            layout.Attachments.Count > 0 && new FileInfo(path).Length == layout.FileLength)
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return MatroskaAttachments.Build(new EbmlReader(handle), layout, artworks);
        }

        if (artworks.Count == 0)
            return null;
        var w = new EbmlWriter();
        for (var i = 0; i < artworks.Count; i++)
        {
            var artwork = artworks[i];
            var name = (i == 0 ? "cover" : $"cover_{i + 1}") + artwork.Extension;
            w.Master(AttachedFile, f =>
            {
                f.String(FileName, name);
                f.String(FileMediaType, artwork.MimeType);
                f.Binary(FileData, artwork.Data);
                f.UInt(FileUid, MatroskaChapters.RandomUid());
            });
        }

        return w.ToArray();
    }

    // ------------------------------------------------------------------ samples

    public void WriteSample(int track, MediaSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        EnsureHeader();
        var t = _tracks[track];
        var timescale = (double)Math.Max(1u, t.Config.Timescale);
        var ptsMs = (long)Math.Round(sample.Pts * 1000 / timescale) + t.ShiftMs;
        if (ptsMs < 0)
            ptsMs = 0;
        var durationMs = sample.Duration > 0 ? (long)Math.Round(sample.Duration * 1000 / timescale) : 0;

        ReadOnlySpan<byte> data;
        if (t.ConvertTx3g)
        {
            var text = SubtitleText.FromTx3g(sample.GetData().Span);
            if (text.IsEmpty)
                return;
            data = Encoding.UTF8.GetBytes(SubtitleText.ToSrt(text));
        }
        else if (t.Config.Codec == CodecType.ProRes && sample.Size >= 8)
        {
            // Matroska ProRes frames omit the 8-byte frame header (size and 'icpf').
            var size = sample.Size;
            if (_buffer.Length < size)
                _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
            sample.CopyTo(_buffer.AsSpan(0, size));
            data = _buffer.AsSpan(0, size);
            if (data.Slice(4, 4).SequenceEqual("icpf"u8))
                data = data[8..];
        }
        else if (t.WebVttLines)
        {
            byte[] lines = [(byte)'\n', (byte)'\n', .. sample.GetData().Span];
            data = lines;
        }
        else
        {
            var size = sample.Size;
            if (_buffer.Length < size)
                _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
            sample.CopyTo(_buffer.AsSpan(0, size));
            data = _buffer.AsSpan(0, size);
        }

        StartClusterIfNeeded(t, sample.IsSync, ptsMs, data.Length);
        var relative = ptsMs - _clusterTimestamp;
        var blockPosition = _out.Position;
        // Subtitles always carry a duration; audio frames when theirs differs from the default (e.g. the last
        // frame of ALAC/FLAC), so players see the right end.
        var subtitle = t.Config.Kind is TrackKind.Subtitle or TrackKind.ClosedCaption;
        var oddAudio = t.Config.Kind == TrackKind.Audio && sample.Duration > 0 && t.Config.DefaultSampleDuration > 0 &&
                       sample.Duration != t.Config.DefaultSampleDuration;
        if (sample.Additions is { Count: > 0 } additions)
        {
            // BlockAdditions (e.g. HDR10+ of VP9) only fit in a BlockGroup; non-key frames then need a ReferenceBlock.
            long? reference = sample.IsSync || t.LastPtsMs < 0 ? null : t.LastPtsMs - ptsMs is 0 ? -1 : t.LastPtsMs - ptsMs;
            WriteBlockGroup(t.Number, (short)relative, data, subtitle || oddAudio ? durationMs : 0, additions, reference);
        }
        else if (subtitle || oddAudio || sample.TrimEnd > 0)
        {
            // The encoder padding of the last audio frame is DiscardPadding (nanoseconds).
            var discardNs = sample.TrimEnd > 0 && t.Config.Timescale > 0 ? (long)Math.Round(sample.TrimEnd * 1e9 / t.Config.Timescale) : 0;
            WriteBlockGroup(t.Number, (short)relative, data, subtitle || oddAudio ? durationMs : 0, discardPaddingNs: discardNs);
        }
        else
        {
            WriteSimpleBlock(t.Number, (short)relative, data, sample.IsSync, sample.IsDiscardable);
        }

        t.LastPtsMs = ptsMs;

        var cueTrack = _tracks.FirstOrDefault(x => x.IsVideo) ?? _tracks.FirstOrDefault(x => x.Config.Kind == TrackKind.Audio) ?? _tracks[0];
        if (t == cueTrack && sample.IsSync && (t.IsVideo || _cues.Count == 0 || _cues[^1].ClusterPosition != _clusterStart - _segmentDataStart))
            _cues.Add((ptsMs, t.Number, _clusterStart - _segmentDataStart, blockPosition - _clusterDataStart));
        var frameEnd = ptsMs + (durationMs > 0 ? durationMs : FrameMs(t));
        _maxEndMs = Math.Max(_maxEndMs, frameEnd);
        t.Frames++;
        t.Bytes += data.Length;
        t.FirstMs = Math.Min(t.FirstMs, ptsMs);
        t.EndMs = Math.Max(t.EndMs, frameEnd);
    }

    /// <summary>Nominal frame duration in milliseconds (for samples without a duration).</summary>
    private static long FrameMs(TrackState t) =>
        t.Config.DefaultSampleDuration > 0 && t.Config.Timescale > 0 ? (long)Math.Round(t.Config.DefaultSampleDuration * 1000.0 / t.Config.Timescale) : 0;

    private void StartClusterIfNeeded(TrackState t, bool sync, long ptsMs, int size)
    {
        var hasVideo = _tracks.Any(x => x.IsVideo);
        var age = ptsMs - _clusterTimestamp;
        var start = _clusterStart < 0
                    || age >= MaxClusterMs
                    || ptsMs - _clusterTimestamp is > short.MaxValue or < short.MinValue
                    || _out.Position - _clusterDataStart + size > MaxClusterBytes
                    || (hasVideo && t.IsVideo && sync && age >= MinVideoClusterMs);
        if (!start)
            return;

        CloseCluster();
        _clusterStart = _out.Position;
        Span<byte> header = stackalloc byte[12];
        var n = EbmlVarInt.WriteId(header, Cluster);
        n += EbmlVarInt.WriteUnknownSize(header[n..], 8);
        _out.Write(header[..n]);
        _clusterDataStart = _out.Position;
        _clusterTimestamp = Math.Max(0, ptsMs);
        var w = new EbmlWriter();
        w.UInt(Timestamp, (ulong)_clusterTimestamp);
        _out.Write(w.WrittenSpan);
    }

    private void CloseCluster()
    {
        if (_clusterStart < 0)
            return;
        var end = _out.Position;
        var size = end - _clusterDataStart;
        Span<byte> sizeField = stackalloc byte[8];
        EbmlVarInt.WriteSize(sizeField, (ulong)size, 8);
        _out.Position = _clusterDataStart - 8;
        _out.Write(sizeField);
        _out.Position = end;
        _clusterStart = -1;
    }

    private void WriteSimpleBlock(uint track, short relative, ReadOnlySpan<byte> data, bool keyframe, bool discardable)
    {
        Span<byte> header = stackalloc byte[16];
        var trackLength = EbmlVarInt.SizeLength(track);
        var blockSize = (ulong)(trackLength + 3 + data.Length);
        var n = EbmlVarInt.WriteId(header, SimpleBlock);
        n += EbmlVarInt.WriteSize(header[n..], blockSize);
        n += EbmlVarInt.WriteSize(header[n..], track, trackLength);
        BinaryPrimitives.WriteInt16BigEndian(header[n..], relative);
        n += 2;
        header[n++] = (byte)((keyframe ? 0x80 : 0) | (discardable ? 0x01 : 0));
        _out.Write(header[..n]);
        _out.Write(data);
    }

    private void WriteBlockGroup(uint track, short relative, ReadOnlySpan<byte> data, long durationMs,
        IReadOnlyList<BlockAddition>? additions = null, long? referenceMs = null, long discardPaddingNs = 0)
    {
        var trackLength = EbmlVarInt.SizeLength(track);
        var blockSize = (ulong)(trackLength + 3 + data.Length);
        // Elements after the Block, in the specification's order: BlockAdditions, BlockDuration, ReferenceBlock,
        // DiscardPadding.
        var duration = new EbmlWriter();
        if (additions is { Count: > 0 })
        {
            duration.Master(BlockAdditions, a =>
            {
                foreach (var addition in additions)
                {
                    a.Master(BlockMore, m =>
                    {
                        m.UInt(BlockAddId, addition.Id);
                        m.Binary(BlockAdditional, addition.Data.Span);
                    });
                }
            });
        }

        if (durationMs > 0)
            duration.UInt(BlockDuration, (ulong)durationMs);
        if (referenceMs is { } reference)
            duration.Int(ReferenceBlock, reference);
        if (discardPaddingNs > 0)
            duration.Int(DiscardPadding, discardPaddingNs);
        var blockElementSize = EbmlVarInt.IdLength(MatroskaMediaIds.Block) + EbmlVarInt.SizeLength(blockSize) + (long)blockSize;
        var groupSize = (ulong)(blockElementSize + duration.Length);

        Span<byte> header = stackalloc byte[32];
        var n = EbmlVarInt.WriteId(header, BlockGroup);
        n += EbmlVarInt.WriteSize(header[n..], groupSize);
        n += EbmlVarInt.WriteId(header[n..], MatroskaMediaIds.Block);
        n += EbmlVarInt.WriteSize(header[n..], blockSize);
        n += EbmlVarInt.WriteSize(header[n..], track, trackLength);
        BinaryPrimitives.WriteInt16BigEndian(header[n..], relative);
        n += 2;
        header[n++] = 0;
        _out.Write(header[..n]);
        _out.Write(data);
        _out.Write(duration.WrittenSpan);
    }

    // ------------------------------------------------------------------ finishing

    public void Finish(CancellationToken cancellationToken)
    {
        if (_finished)
            throw new InvalidOperationException("The muxer was already finished.");
        _finished = true;
        EnsureHeader();
        CloseCluster();
        cancellationToken.ThrowIfCancellationRequested();

        if (_cues.Count > 0)
        {
            var cues = new EbmlWriter();
            foreach (var (time, track, cluster, relative) in _cues)
            {
                cues.Master(CuePoint, p =>
                {
                    p.UInt(CueTime, (ulong)time);
                    p.Master(CueTrackPositions, tp =>
                    {
                        tp.UInt(CueTrack, track);
                        tp.UInt(CueClusterPosition, (ulong)cluster);
                        tp.UInt(CueRelativePosition, (ulong)relative);
                    });
                });
            }

            _seek[Cues] = _out.Position - _segmentDataStart;
            _out.Write(EbmlWriter.Element(Cues, cues.WrittenSpan));
        }

        // Tags with statistics: in the reserved region when they fit, else at the end of the Segment.
        var allTags = BuildTags(statistics: true);
        var tagsElement = EbmlWriter.Element(Tags, allTags);
        if (tagsElement.Length == _tagsRegion || tagsElement.Length + 2 <= _tagsRegion)
        {
            var resume = _out.Position;
            _out.Position = _tagsPosition;
            _out.Write(tagsElement);
            WriteVoid(_tagsRegion - tagsElement.Length);
            _seek[Tags] = _tagsPosition - _segmentDataStart;
            _out.Position = resume;
        }
        else
        {
            _seek[Tags] = _out.Position - _segmentDataStart;
            _out.Write(tagsElement);
        }

        var end = _out.Position;

        // Segment size.
        Span<byte> sizeField = stackalloc byte[8];
        EbmlVarInt.WriteSize(sizeField, (ulong)(end - _segmentDataStart), 8);
        _out.Position = _segmentDataStart - 8;
        _out.Write(sizeField);

        // Duration (TimestampScale units).
        var duration = Math.Max(_maxEndMs, (long)_settings.Document.Duration.TotalMilliseconds);
        Span<byte> value = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(value, duration);
        _out.Position = _durationPosition;
        _out.Write(value);

        // SeekHead in the reserved space.
        var seek = new EbmlWriter();
        foreach (var (id, position) in _seek.OrderBy(kv => kv.Value))
        {
            seek.Master(Seek, s =>
            {
                s.Binary(SeekId, EbmlVarInt.EncodeId(id));
                s.UInt(SeekPosition, (ulong)position);
            });
        }

        var seekHead = EbmlWriter.Element(SeekHead, seek.WrittenSpan);
        if (seekHead.Length + 2 > SeekHeadReserve)
            throw new InvalidOperationException("The SeekHead does not fit in its reserved space.");
        _out.Position = _seekHeadPosition;
        _out.Write(seekHead);
        WriteVoid(SeekHeadReserve - seekHead.Length);
        _out.Position = end;
        _out.SetLength(end);
        AppLog.Debug($"Matroska muxer wrote {_tracks.Count} track(s), {_cues.Count} cue point(s).");
    }

    private void WriteVoid(long total)
    {
        if (total == 0)
            return;
        var header = EbmlWriter.VoidHeader(total);
        _out.Write(header);
        var left = total - header.Length;
        var zeros = new byte[Math.Min(left, 4096)];
        while (left > 0)
        {
            var n = (int)Math.Min(zeros.Length, left);
            _out.Write(zeros, 0, n);
            left -= n;
        }
    }

    private static byte[] Encode(Action<EbmlWriter> body)
    {
        var w = new EbmlWriter();
        body(w);
        return w.ToArray();
    }

    public void Dispose()
    {
        // The stream belongs to the caller.
    }
}
