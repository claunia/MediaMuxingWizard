using MMW.Core.Languages;

namespace MMW.Ocr;

/// <summary>A Tesseract recognition language.</summary>
/// <param name="Code">Tesseract name, the traineddata file name without extension ("eng", "chi_sim", "srp_latn").</param>
/// <param name="Name">English display name.</param>
public sealed record TesseractLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The languages of the official Tesseract models and their mapping from BCP-47 track languages.</summary>
public static class TesseractLanguages
{
    /// <summary>The English model, the fallback for tracks without a (known) language.</summary>
    public const string English = "eng";

    /// <summary>
    /// The horizontal-text models of <c>tesseract-ocr/tessdata_fast</c> (vertical CJK, orientation detection,
    /// equations and the historic Fraktur models are omitted: they are not useful for subtitles), sorted by name.
    /// </summary>
    public static IReadOnlyList<TesseractLanguage> All { get; } = new TesseractLanguage[]
    {
        new("afr", "Afrikaans"), new("amh", "Amharic"), new("ara", "Arabic"), new("asm", "Assamese"), new("aze", "Azerbaijani"),
        new("aze_cyrl", "Azerbaijani (Cyrillic)"), new("bel", "Belarusian"), new("ben", "Bengali"), new("bod", "Tibetan"),
        new("bos", "Bosnian"), new("bre", "Breton"), new("bul", "Bulgarian"), new("cat", "Catalan"), new("ceb", "Cebuano"),
        new("ces", "Czech"), new("chi_sim", "Chinese (Simplified)"), new("chi_tra", "Chinese (Traditional)"), new("chr", "Cherokee"),
        new("cos", "Corsican"), new("cym", "Welsh"), new("dan", "Danish"), new("deu", "German"), new("div", "Dhivehi"),
        new("dzo", "Dzongkha"), new("ell", "Greek"), new("eng", "English"), new("enm", "English, Middle"), new("epo", "Esperanto"),
        new("est", "Estonian"), new("eus", "Basque"), new("fao", "Faroese"), new("fas", "Persian"), new("fil", "Filipino"),
        new("fin", "Finnish"), new("fra", "French"), new("frm", "French, Middle"), new("fry", "Western Frisian"),
        new("gla", "Scottish Gaelic"), new("gle", "Irish"), new("glg", "Galician"), new("grc", "Greek, Ancient"),
        new("guj", "Gujarati"), new("hat", "Haitian Creole"), new("heb", "Hebrew"), new("hin", "Hindi"), new("hrv", "Croatian"),
        new("hun", "Hungarian"), new("hye", "Armenian"), new("iku", "Inuktitut"), new("ind", "Indonesian"), new("isl", "Icelandic"),
        new("ita", "Italian"), new("ita_old", "Italian (Old)"), new("jav", "Javanese"), new("jpn", "Japanese"), new("kan", "Kannada"),
        new("kat", "Georgian"), new("kaz", "Kazakh"), new("khm", "Khmer"), new("kir", "Kyrgyz"), new("kmr", "Kurdish (Kurmanji)"),
        new("kor", "Korean"), new("lao", "Lao"), new("lat", "Latin"), new("lav", "Latvian"), new("lit", "Lithuanian"),
        new("ltz", "Luxembourgish"), new("mal", "Malayalam"), new("mar", "Marathi"), new("mkd", "Macedonian"), new("mlt", "Maltese"),
        new("mon", "Mongolian"), new("mri", "Maori"), new("msa", "Malay"), new("mya", "Burmese"), new("nep", "Nepali"),
        new("nld", "Dutch"), new("nor", "Norwegian"), new("oci", "Occitan"), new("ori", "Odia"), new("pan", "Punjabi"),
        new("pol", "Polish"), new("por", "Portuguese"), new("pus", "Pashto"), new("que", "Quechua"), new("ron", "Romanian"),
        new("rus", "Russian"), new("san", "Sanskrit"), new("sin", "Sinhala"), new("slk", "Slovak"), new("slv", "Slovenian"),
        new("snd", "Sindhi"), new("spa", "Spanish"), new("spa_old", "Spanish (Old)"), new("sqi", "Albanian"), new("srp", "Serbian"),
        new("srp_latn", "Serbian (Latin)"), new("sun", "Sundanese"), new("swa", "Swahili"), new("swe", "Swedish"), new("syr", "Syriac"),
        new("tam", "Tamil"), new("tat", "Tatar"), new("tel", "Telugu"), new("tgk", "Tajik"), new("tha", "Thai"), new("tir", "Tigrinya"),
        new("ton", "Tongan"), new("tur", "Turkish"), new("uig", "Uyghur"), new("ukr", "Ukrainian"), new("urd", "Urdu"),
        new("uzb", "Uzbek"), new("uzb_cyrl", "Uzbek (Cyrillic)"), new("vie", "Vietnamese"), new("yid", "Yiddish"), new("yor", "Yoruba"),
    }.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray();

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
