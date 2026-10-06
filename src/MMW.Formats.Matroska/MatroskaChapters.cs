using System.Security.Cryptography;
using MMW.Core.Chapters;
using MMW.Formats.Matroska.Ebml;
using static MMW.Formats.Matroska.MatroskaIds;

namespace MMW.Formats.Matroska;

/// <summary>Reads and builds the Chapters element.</summary>
internal static class MatroskaChapters
{
    /// <summary>
    /// Reads the default edition (or the first one) and flattens its chapter atoms, nested ones included, into a
    /// list sorted by start time. Hidden and disabled chapters are skipped.
    /// </summary>
    public static List<Chapter> Read(ReadOnlyMemory<byte> chaptersPayload)
    {
        var editions = EbmlParser.Children(chaptersPayload).Where(c => c.Id == EditionEntry).ToList();
        if (editions.Count == 0)
            return [];

        var chosen = editions[0];
        foreach (var edition in editions)
        {
            if (EbmlParser.Children(edition.Data).GetUInt(EditionFlagDefault, 0) != 0)
            {
                chosen = edition;
                break;
            }
        }

        var result = new List<Chapter>();
        Collect(EbmlParser.Children(chosen.Data), result);
        return result.OrderBy(c => c.Start).ToList();
    }

    private static void Collect(List<EbmlChild> parent, List<Chapter> result)
    {
        foreach (var atom in parent)
        {
            if (atom.Id != ChapterAtom)
                continue;
            var c = EbmlParser.Children(atom.Data);
            var hidden = c.GetUInt(ChapterFlagHidden, 0) != 0;
            var enabled = c.GetUInt(ChapterFlagEnabled, 1) != 0;
            if (!hidden && enabled)
            {
                var start = c.GetUInt(ChapterTimeStart, 0);
                var title = string.Empty;
                if (c.Child(ChapterDisplay) is { } display)
                    title = EbmlParser.Children(display.Data).GetString(ChapString) ?? string.Empty;
                result.Add(new Chapter(TimeSpan.FromTicks((long)(start / 100)), title));
            }

            Collect(c, result);
        }
    }

    /// <summary>Builds a Chapters payload with a single default edition (UIDs are random).</summary>
    /// <param name="chapters">Chapters, in any order.</param>
    /// <param name="duration">Duration of the file, used as the end of the last chapter when known.</param>
    public static byte[] Build(IEnumerable<Chapter> chapters, TimeSpan duration)
    {
        var sorted = chapters.OrderBy(c => c.Start).ToList();
        var w = new EbmlWriter();
        w.Master(EditionEntry, edition =>
        {
            edition.UInt(EditionUid, RandomUid());
            edition.UInt(EditionFlagDefault, 1);
            for (var i = 0; i < sorted.Count; i++)
            {
                var chapter = sorted[i];
                var end = i + 1 < sorted.Count ? sorted[i + 1].Start : duration;
                edition.Master(ChapterAtom, atom =>
                {
                    atom.UInt(ChapterUid, RandomUid());
                    atom.UInt(ChapterTimeStart, ToNanoseconds(chapter.Start));
                    if (end > chapter.Start)
                        atom.UInt(ChapterTimeEnd, ToNanoseconds(end));
                    atom.Master(ChapterDisplay, display =>
                    {
                        display.String(ChapString, chapter.Title);
                        display.String(ChapLanguage, "und");
                    });
                });
            }
        });
        return w.ToArray();
    }

    /// <summary>A random non-zero 64-bit UID.</summary>
    public static ulong RandomUid()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong uid;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            uid = BitConverter.ToUInt64(bytes);
        }
        while (uid == 0);
        return uid;
    }

    private static ulong ToNanoseconds(TimeSpan t) => (ulong)Math.Max(0, t.Ticks) * 100;
}
