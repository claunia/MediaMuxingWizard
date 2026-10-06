using System.Collections.Frozen;
using System.Globalization;

namespace MMW.Core.Languages;

/// <summary>A language usable for tracks.</summary>
/// <param name="Tag">BCP-47 tag (e.g. "en", "zh-Hans", "pt-BR"); "und" for undetermined.</param>
/// <param name="Iso639_2T">ISO 639-2/T code, as stored in MP4 <c>mdhd</c>.</param>
/// <param name="Iso639_2B">ISO 639-2/B code, as stored in Matroska's legacy Language element.</param>
/// <param name="Name">English display name.</param>
public sealed record Language(string Tag, string Iso639_2T, string Iso639_2B, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Conversions between BCP-47 tags and ISO 639-2 codes.</summary>
public static class LanguageTable
{
    public const string Undetermined = "und";

    private static readonly FrozenDictionary<string, string> s_tToB = new Dictionary<string, string>
    {
        ["sqi"] = "alb", ["hye"] = "arm", ["eus"] = "baq", ["mya"] = "bur", ["zho"] = "chi", ["ces"] = "cze",
        ["nld"] = "dut", ["fra"] = "fre", ["kat"] = "geo", ["deu"] = "ger", ["ell"] = "gre", ["isl"] = "ice",
        ["mkd"] = "mac", ["mri"] = "mao", ["msa"] = "may", ["fas"] = "per", ["ron"] = "rum", ["slk"] = "slo",
        ["bod"] = "tib", ["cym"] = "wel",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, string> s_bToT = s_tToB.ToFrozenDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Regional/script variants that matter for video (subtitles, dubs).</summary>
    private static readonly (string Tag, string Name)[] s_variants =
    [
        ("zh-Hans", "Chinese (Simplified)"),
        ("zh-Hant", "Chinese (Traditional)"),
        ("zh-HK", "Chinese (Hong Kong)"),
        ("yue", "Cantonese"),
        ("pt-BR", "Portuguese (Brazil)"),
        ("pt-PT", "Portuguese (Portugal)"),
        ("es-419", "Spanish (Latin America)"),
        ("es-ES", "Spanish (Spain)"),
        ("fr-CA", "French (Canada)"),
        ("en-US", "English (United States)"),
        ("en-GB", "English (United Kingdom)"),
    ];

    private static readonly Lazy<IReadOnlyList<Language>> s_all = new(Build);
    private static readonly Lazy<FrozenDictionary<string, Language>> s_byTag =
        new(() => s_all.Value.ToFrozenDictionary(l => l.Tag, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<FrozenDictionary<string, Language>> s_byIso =
        new(() => s_all.Value.Where(l => !l.Tag.Contains('-', StringComparison.Ordinal))
            .SelectMany(l => new[] { (l.Iso639_2T, l), (l.Iso639_2B, l) })
            .DistinctBy(x => x.Item1)
            .ToFrozenDictionary(x => x.Item1, x => x.l, StringComparer.OrdinalIgnoreCase));

    /// <summary>All languages sorted by name, with "Undetermined" first.</summary>
    public static IReadOnlyList<Language> All => s_all.Value;

    public static Language? Find(string? tag) =>
        tag is null ? null : s_byTag.Value.GetValueOrDefault(tag) ?? FromIso639_2(tag);

    public static string DisplayName(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return "Undetermined";
        if (Find(tag) is { } lang)
            return lang.Name;

        try
        {
            var ci = CultureInfo.GetCultureInfo(tag);
            return string.IsNullOrEmpty(ci.EnglishName) ? tag : ci.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>Looks up a language from an ISO 639-2 (B or T) code.</summary>
    public static Language? FromIso639_2(string code) => s_byIso.Value.GetValueOrDefault(code);

    /// <summary>Converts an ISO 639-2 code (or anything else) to a BCP-47 tag; returns "und" when unknown.</summary>
    public static string ToBcp47(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Undetermined;
        if (s_byTag.Value.TryGetValue(code, out var l))
            return l.Tag;
        return FromIso639_2(code)?.Tag ?? code;
    }

    /// <summary>ISO 639-2/T code for a BCP-47 tag (primary subtag only); "und" when unknown.</summary>
    public static string ToIso639_2T(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Undetermined;
        if (tag.Length == 3 && !tag.Contains('-', StringComparison.Ordinal))
            return s_bToT.GetValueOrDefault(tag.ToLowerInvariant(), tag.ToLowerInvariant());

        var primary = tag.Split('-')[0];
        return s_byTag.Value.TryGetValue(primary, out var l) ? l.Iso639_2T : Undetermined;
    }

    /// <summary>ISO 639-2/B code for a BCP-47 tag; "und" when unknown.</summary>
    public static string ToIso639_2B(string? tag)
    {
        var t = ToIso639_2T(tag);
        return s_tToB.GetValueOrDefault(t, t);
    }

    private static List<Language> Build()
    {
        var list = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var ci in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.IsNullOrEmpty(ci.Name) || ci.Name.Contains('-', StringComparison.Ordinal))
                continue;
            var three = ci.ThreeLetterISOLanguageName;
            if (three.Length != 3 || ci.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
                continue;
            var t = s_bToT.GetValueOrDefault(three, three);
            list.TryAdd(ci.Name, new Language(ci.Name, t, s_tToB.GetValueOrDefault(t, t), ci.EnglishName));
        }

        // A few that ICU might not expose as neutral cultures on every platform.
        foreach (var (tag, t, name) in new[]
                 {
                     ("en", "eng", "English"), ("fr", "fra", "French"), ("de", "deu", "German"), ("es", "spa", "Spanish"),
                     ("it", "ita", "Italian"), ("ja", "jpn", "Japanese"), ("zh", "zho", "Chinese"), ("pt", "por", "Portuguese"),
                     ("ru", "rus", "Russian"), ("ko", "kor", "Korean"), ("nl", "nld", "Dutch"), ("sv", "swe", "Swedish"),
                 })
        {
            list.TryAdd(tag, new Language(tag, t, s_tToB.GetValueOrDefault(t, t), name));
        }

        foreach (var (tag, name) in s_variants)
        {
            var primary = tag.Split('-')[0];
            var t = list.TryGetValue(primary, out var p) ? p.Iso639_2T : primary;
            list.TryAdd(tag, new Language(tag, t, s_tToB.GetValueOrDefault(t, t), name));
        }

        var sorted = list.Values.OrderBy(l => l.Name, StringComparer.CurrentCulture).ToList();
        sorted.Insert(0, new Language(Undetermined, Undetermined, Undetermined, "Undetermined"));
        return sorted;
    }
}
