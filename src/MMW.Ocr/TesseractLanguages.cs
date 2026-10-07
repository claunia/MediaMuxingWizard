using System.Globalization;
using MMW.Core.Languages;
using MMW.Ocr.Resources;

namespace MMW.Ocr;

/// <summary>A Tesseract recognition language.</summary>
/// <param name="Code">Tesseract name, the traineddata file name without extension ("eng", "chi_sim", "srp_latn").</param>
public sealed record TesseractLanguage(string Code)
{
    /// <summary>Display name in the current UI language (the code when the language has no name).</summary>
    public string Name => Strings.ResourceManager.GetString("Language_" + Code, CultureInfo.CurrentUICulture) ?? Code;

    public override string ToString() => Name;
}

/// <summary>The languages of the official Tesseract models and their mapping from BCP-47 track languages.</summary>
public static class TesseractLanguages
{
    /// <summary>The English model, the fallback for tracks without a (known) language.</summary>
    public const string English = "eng";

    /// <summary>
    /// The horizontal-text models of <c>tesseract-ocr/tessdata_fast</c> (vertical CJK, orientation detection,
    /// equations and the historic Fraktur models are omitted: they are not useful for subtitles), sorted by display name
    /// (in the UI language of the first use). The English and translated names are in the Language_* resources.
    /// </summary>
    public static IReadOnlyList<TesseractLanguage> All { get; } = new[]
    {
        "afr", "amh", "ara", "asm", "aze", "aze_cyrl", "bel", "ben", "bod", "bos", "bre", "bul", "cat", "ceb", "ces",
        "chi_sim", "chi_tra", "chr", "cos", "cym", "dan", "deu", "div", "dzo", "ell", "eng", "enm", "epo", "est", "eus",
        "fao", "fas", "fil", "fin", "fra", "frm", "fry", "gla", "gle", "glg", "grc", "guj", "hat", "heb", "hin", "hrv",
        "hun", "hye", "iku", "ind", "isl", "ita", "ita_old", "jav", "jpn", "kan", "kat", "kaz", "khm", "kir", "kmr",
        "kor", "lao", "lat", "lav", "lit", "ltz", "mal", "mar", "mkd", "mlt", "mon", "mri", "msa", "mya", "nep", "nld",
        "nor", "oci", "ori", "pan", "pol", "por", "pus", "que", "ron", "rus", "san", "sin", "slk", "slv", "snd", "spa",
        "spa_old", "sqi", "srp", "srp_latn", "sun", "swa", "swe", "syr", "tam", "tat", "tel", "tgk", "tha", "tir",
        "ton", "tur", "uig", "ukr", "urd", "uzb", "uzb_cyrl", "vie", "yid", "yor",
    }.Select(c => new TesseractLanguage(c)).OrderBy(l => l.Name, StringComparer.CurrentCulture).ToArray();

    private static readonly Dictionary<string, TesseractLanguage> s_byCode = All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The language with Tesseract name <paramref name="code"/>, or null.</summary>
    public static TesseractLanguage? Find(string? code) => code is null ? null : s_byCode.GetValueOrDefault(code);

    /// <summary>Display name of a Tesseract language code or "+"-joined set ("English + French").</summary>
    public static string DisplayName(string languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return string.Join(" + ", Split(languages).Select(c => Find(c)?.Name ?? c));
    }

    /// <summary>The individual codes of a "+"-joined language set.</summary>
    public static IReadOnlyList<string> Split(string languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The Tesseract model for a BCP-47 track language ("en" → "eng", "zh-Hant" → "chi_tra", "sr-Latn" →
    /// "srp_latn", "pt-BR" → "por"); also accepts ISO 639-2 codes and Tesseract names. Null when the language is
    /// undetermined or has no model.
    /// </summary>
    public static string? FromTrackLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        tag = tag.Trim().Replace('_', '-');
        if (s_byCode.TryGetValue(tag.Replace('-', '_'), out var direct) && tag.Length > 2)
            return direct.Code;

        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var primary = parts[0].ToLowerInvariant();
        var subtags = parts.Skip(1).Select(p => p.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        bool Has(params string[] values) => values.Any(subtags.Contains);

        switch (primary)
        {
            case "und" or "mis" or "mul" or "zxx":
                return null;
            case "zh" or "zho" or "chi" or "cmn":
                return Has("hant", "tw", "hk", "mo") ? "chi_tra" : "chi_sim";
            case "yue":
                return "chi_tra";
            case "sr" or "srp" or "scc":
                return Has("latn") ? "srp_latn" : "srp";
            case "az" or "aze":
                return Has("cyrl") ? "aze_cyrl" : "aze";
            case "uz" or "uzb":
                return Has("cyrl") ? "uzb_cyrl" : "uzb";
            case "no" or "nb" or "nn" or "nob" or "nno":
                return "nor";
            case "tl" or "tgl" or "fil":
                return "fil";
            case "ku" or "kur" or "kmr":
                return "kmr";
            case "ms" or "msa" or "may" or "zsm":
                return "msa";
            case "fa" or "fas" or "per" or "pes":
                return "fas";
        }

        var t = LanguageTable.ToIso639_2T(primary);
        return s_byCode.TryGetValue(t, out var mapped) ? mapped.Code : null;
    }
}
