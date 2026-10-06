using System.Text;
using System.Text.RegularExpressions;

namespace MMW.Ocr;

/// <summary>
/// Cleans raw OCR output into subtitle text: line breaks are kept, whitespace is normalised, empty and noise lines
/// are dropped, and a few unambiguous OCR confusions are corrected.
/// </summary>
/// <remarks>
/// Corrections are deliberately conservative (they must not damage correct text in any language):
/// <list type="bullet">
/// <item>'|' (never used in subtitles) becomes 'I'.</item>
/// <item>Typographic single quotes and the modifier apostrophe become ASCII apostrophes; typographic double quotes
/// become ASCII quotes (low-9 quotes such as „ are kept).</item>
/// <item>A lone lowercase "l" word becomes "I", as do "l'm", "l'll", "l've", "l'd" (French "l'" before a vowel is
/// untouched).</item>
/// <item>A capital "I" between two lowercase letters, in a word with no other capital, becomes "l" ("heIlo" →
/// "hello", but "MacIntyre" is kept).</item>
/// <item>A leading "l" in an otherwise upper-case word becomes "I" ("lT'S" → "IT'S").</item>
/// <item>Music notes framing a sung subtitle, which the Latin models read as "J" ("J Take on me I"): a lone "J" at
/// the start of the first line or the end of the last one, with a lone J, I, [, ], ', S, s, N or f at the other end,
/// become "♪".</item>
/// </list>
/// </remarks>
public static partial class OcrTextCleaner
{
    /// <summary>Cleans <paramref name="raw"/> (see the remarks of <see cref="OcrTextCleaner"/>).</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = NormalizeCharacters(raw);
        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = Whitespace().Replace(rawLine, " ").Trim();
            if (line.Length == 0 || IsNoise(line))
                continue;
            lines.Add(FixWords(line));
        }

        FixMusicNotes(lines);
        return string.Join('\n', lines);
    }

    private static string NormalizeCharacters(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            sb.Append(c switch
            {
                '|' => 'I',
                '\u2018' or '\u2019' or '\u02BC' or '\u2032' => '\'',
                '\u201C' or '\u201D' or '\u2033' => '"',
                '\f' or '\v' => '\n',
                '\u00A0' or '\u2007' or '\u202F' => ' ',
                _ => c,
            });
        }

        return sb.ToString();
    }

    /// <summary>A line with no letter or digit that is at most two characters long ('.', '-', "''", …) is a speck.</summary>
    private static bool IsNoise(string line) => line.Length <= 2 && !line.Any(char.IsLetterOrDigit);

    private static string FixWords(string line)
    {
        line = LonelyL().Replace(line, "I");
        line = LContraction().Replace(line, "I'$1");
        line = Word().Replace(line, m => FixInnerCapitalI(m.Value));
        line = LeadingLUpperWord().Replace(line, "I");
        return line;
    }

    /// <summary>Replaces the music notes misread at the start of the first line and the end of the last one.</summary>
    private static void FixMusicNotes(List<string> lines)
    {
        if (lines.Count == 0)
            return;
        var head = lines[0].Split(' ');
        var tail = lines.Count == 1 ? head : lines[^1].Split(' ');
        if ((lines.Count == 1 && head.Length < 3) || head.Length < 2 || tail.Length < 2 || head[0].Length != 1 || tail[^1].Length != 1)
            return;
        char first = head[0][0], last = tail[^1][0];
        if (!((first == 'J' && IsNoteLookalike(last)) || (last == 'J' && IsNoteLookalike(first))))
            return;
        head[0] = MusicNote;
        tail[^1] = MusicNote;
        lines[0] = string.Join(' ', head);
        if (lines.Count > 1)
            lines[^1] = string.Join(' ', tail);

        static bool IsNoteLookalike(char c) => c is 'J' or 'I' or '[' or ']' or '\'' or 'S' or 's' or 'N' or 'f';
    }

    private const string MusicNote = "\u266A";

    [GeneratedRegex(@"[ \t\u3000]+")]
    private static partial Regex Whitespace();

    /// <summary>A standalone "l" (not followed by an apostrophe, a letter or a digit).</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}'])l(?![\p{L}\p{N}'])")]
    private static partial Regex LonelyL();

    /// <summary>"l'm", "l'll", "l've", "l'd" as whole words.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])l'(m|ll|ve|d)(?![\p{L}])")]
    private static partial Regex LContraction();

    private static string FixInnerCapitalI(string word)
    {
        var index = word.IndexOf('I', 1);
        if (index < 0 || index + 1 >= word.Length || word.Count(char.IsUpper) != 1)
            return word;
        if (!char.IsLower(word[index - 1]) || !char.IsLower(word[index + 1]))
            return word;
        return string.Concat(word.AsSpan(0, index), "l", word.AsSpan(index + 1));
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    /// <summary>A leading "l" followed only by upper-case letters (and apostrophes) up to the end of the word.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])l(?=\p{Lu}[\p{Lu}']*(?![\p{L}]))")]
    private static partial Regex LeadingLUpperWord();
}
