using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>A Matroska SimpleTag.</summary>
internal sealed class MatroskaSimpleTag
{
    public MatroskaSimpleTag(string name, string? value = null)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; }

    public string? Value { get; set; }

    public byte[]? Binary { get; set; }

    /// <summary>Legacy TagLanguage (ISO 639-2), null when absent.</summary>
    public string? Language { get; set; }

    public string? LanguageBcp47 { get; set; }

    public bool IsDefault { get; set; } = true;

    public List<MatroskaSimpleTag> Children { get; } = [];

    /// <summary>The element as read from the file (only set by <see cref="Parse"/>).</summary>
    public ReadOnlyMemory<byte> RawElement { get; private set; }

    /// <summary>Effective language: TagLanguageBCP47, else TagLanguage, else "und".</summary>
    public string EffectiveLanguage => LanguageBcp47 ?? Language ?? "und";

    public static MatroskaSimpleTag Parse(EbmlChild element)
    {
        var tag = new MatroskaSimpleTag(string.Empty) { RawElement = element.Element };
        foreach (var c in EbmlParser.Children(element.Data))
        {
            switch (c.Id)
            {
                case TagName:
                    tag.Name = c.String;
                    break;
                case TagString:
                    tag.Value = c.String;
                    break;
                case TagBinary:
                    tag.Binary = c.Data.ToArray();
                    break;
                case TagLanguage:
                    tag.Language = c.String;
                    break;
                case TagLanguageBcp47:
                    tag.LanguageBcp47 = c.String;
                    break;
                case TagDefault:
                    tag.IsDefault = c.UInt != 0;
                    break;
                case SimpleTag:
                    tag.Children.Add(Parse(c));
                    break;
            }
        }

        return tag;
    }

    public void Write(EbmlWriter w) => w.Master(SimpleTag, body =>
    {
        body.String(TagName, Name);
        if (LanguageBcp47 is not null)
            body.String(TagLanguageBcp47, LanguageBcp47);
        if (Language is not null)
            body.String(TagLanguage, Language);
        if (!IsDefault)
            body.UInt(TagDefault, 0);
        if (Value is not null)
            body.String(TagString, Value);
        else if (Binary is not null)
            body.Binary(TagBinary, Binary);
        foreach (var child in Children)
            child.Write(body);
    });
}

/// <summary>A Matroska Tag: targets plus SimpleTags.</summary>
internal sealed class MatroskaTag
{
    public int TargetTypeValue { get; set; } = 50;

    public string? TargetType { get; set; }

    public List<ulong> TrackUids { get; } = [];

    public List<ulong> EditionUids { get; } = [];

    public List<ulong> ChapterUids { get; } = [];

    public List<ulong> AttachmentUids { get; } = [];

    public List<MatroskaSimpleTag> SimpleTags { get; } = [];

    /// <summary>Already-encoded SimpleTag elements written after <see cref="SimpleTags"/> (e.g. preserved binary tags).</summary>
    public List<byte[]> RawSimpleTags { get; } = [];

    /// <summary>True when the tag applies to the whole segment (no non-zero target UID).</summary>
    public bool IsGlobal =>
        TrackUids.All(u => u == 0) && EditionUids.All(u => u == 0) && ChapterUids.All(u => u == 0) && AttachmentUids.All(u => u == 0);

    public static MatroskaTag Parse(ReadOnlyMemory<byte> payload)
    {
        var tag = new MatroskaTag();
        foreach (var c in EbmlParser.Children(payload))
        {
            if (c.Id == Targets)
            {
                foreach (var t in EbmlParser.Children(c.Data))
                {
                    switch (t.Id)
                    {
                        case MatroskaIds.TargetTypeValue:
                            tag.TargetTypeValue = (int)Math.Min(t.UInt, int.MaxValue);
                            break;
                        case MatroskaIds.TargetType:
                            tag.TargetType = t.String;
                            break;
                        case TagTrackUid:
                            tag.TrackUids.Add(t.UInt);
                            break;
                        case TagEditionUid:
                            tag.EditionUids.Add(t.UInt);
                            break;
                        case TagChapterUid:
                            tag.ChapterUids.Add(t.UInt);
                            break;
                        case TagAttachmentUid:
                            tag.AttachmentUids.Add(t.UInt);
                            break;
                    }
                }
            }
            else if (c.Id == SimpleTag)
            {
                tag.SimpleTags.Add(MatroskaSimpleTag.Parse(c));
            }
        }

        return tag;
    }

    /// <summary>Writes the Tag element.</summary>
    public void Write(EbmlWriter w) => w.Master(Tag, body =>
    {
        body.Master(Targets, t =>
        {
            t.UInt(MatroskaIds.TargetTypeValue, (ulong)TargetTypeValue);
            if (TargetType is not null)
                t.String(MatroskaIds.TargetType, TargetType);
            foreach (var u in TrackUids)
                t.UInt(TagTrackUid, u);
            foreach (var u in EditionUids)
                t.UInt(TagEditionUid, u);
            foreach (var u in ChapterUids)
                t.UInt(TagChapterUid, u);
            foreach (var u in AttachmentUids)
                t.UInt(TagAttachmentUid, u);
        });
        foreach (var s in SimpleTags)
            s.Write(body);
        foreach (var raw in RawSimpleTags)
            body.Raw(raw);
    });
}
