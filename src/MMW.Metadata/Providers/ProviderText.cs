using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MMW.Metadata.Providers;

/// <summary>Small text helpers shared by the providers.</summary>
internal static partial class ProviderText
{
    /// <summary>Normalised form of a title for comparisons: lower case, no accents or punctuation.</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return string.Empty;
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c == '&')
                sb.Append(" and ");
            else
                sb.Append(' ');
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>True when two titles are equal after <see cref="Normalize"/> (a leading "the" is ignored).</summary>
    public static bool SameTitle(string? a, string? b)
    {
        var x = StripArticle(Normalize(a));
        var y = StripArticle(Normalize(b));
        return x.Length > 0 && x == y;
    }

    /// <summary>Formats a date as yyyy-MM-dd from ISO text or milliseconds since the epoch.</summary>
    public static string? Date(string? isoText)
    {
        if (string.IsNullOrWhiteSpace(isoText))
            return null;
        if (DateTimeOffset.TryParse(isoText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return isoText.Trim();
    }

    /// <summary>Formats milliseconds since the Unix epoch as yyyy-MM-dd (UTC).</summary>
    public static string? DateFromUnixMilliseconds(long? ms) =>
        ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Removes HTML tags and decodes entities (some providers return HTML descriptions).</summary>
    public static string? StripHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var noBreaks = BreakTags().Replace(text, "\n");
        var plain = WebUtility.HtmlDecode(Tags().Replace(noBreaks, string.Empty));
        return SpacesAroundNewlines().Replace(plain, "\n").Trim();
    }

    /// <summary>Non-empty, distinct, trimmed names.</summary>
    public static string[] Names(IEnumerable<string?>? names) =>
        names?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.Ordinal).ToArray() ?? [];

    /// <summary>Joins non-empty values with ", " or returns null.</summary>
    public static string? Join(IEnumerable<string?>? values)
    {
        var names = Names(values);
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    private static string StripArticle(string s) => s.StartsWith("the ", StringComparison.Ordinal) ? s[4..] : s;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<\s*(br|/p)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpacesAroundNewlines();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
