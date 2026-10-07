using System.Text.RegularExpressions;
using System.Xml.Linq;
using MMW.TestSupport;

namespace MMW.Core.Tests;

/// <summary>
/// Every project's Strings.resx (English) and each translation (Strings.&lt;language&gt;.resx) carry the same keys, every
/// translation is filled in, and both use the same {n} placeholders (a translation that drops or adds one would throw or lose data).
/// </summary>
public sealed partial class LocalizationResourceTests
{
    /// <summary>The interface languages besides English (the neutral language).</summary>
    private static readonly string[] s_languages = ["de", "es", "fr", "it", "pt-BR", "zh-Hans"];

    public static TheoryData<string, string> Projects()
    {
        var src = Path.GetFullPath(Path.Combine(Fixtures.GeneratedDirectory, "..", "..", "..", "src"));
        var data = new TheoryData<string, string>();
        foreach (var resx in Directory.GetFiles(src, "Strings.resx", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            foreach (var language in s_languages)
                data.Add(Path.GetRelativePath(src, Path.GetDirectoryName(Path.GetDirectoryName(resx)!)!), language);
        }

        return data;
    }

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    [Theory]
    [MemberData(nameof(Projects))]
    public void Translation_matches_english(string project, string language)
    {
        var dir = Path.Combine(Path.GetFullPath(Path.Combine(Fixtures.GeneratedDirectory, "..", "..", "..", "src")), project, "Resources");
        var english = Read(Path.Combine(dir, "Strings.resx"));
        var translationPath = Path.Combine(dir, $"Strings.{language}.resx");
        Assert.True(File.Exists(translationPath), $"{project} has no {language} resources");
        var translation = Read(translationPath);
        Assert.Empty(english.Keys.Except(translation.Keys).Order(StringComparer.Ordinal));
        Assert.Empty(translation.Keys.Except(english.Keys).Order(StringComparer.Ordinal));
        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(translation[key]) && !string.IsNullOrWhiteSpace(text), $"{project}: {key} has no translation");
            Assert.True(Placeholders(text).SetEquals(Placeholders(translation[key])), $"{project}: {key} placeholders differ: \"{text}\" / \"{translation[key]}\"");
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
