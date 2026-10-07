using System.Text.RegularExpressions;
using System.Xml.Linq;
using MMW.TestSupport;

namespace MMW.Core.Tests;

/// <summary>
/// Every project's Strings.resx (English) and Strings.es.resx (Spanish) carry the same keys, every Spanish text is
/// filled in, and both use the same {n} placeholders (a translation that drops or adds one would throw or lose data).
/// </summary>
public sealed partial class LocalizationResourceTests
{
    public static TheoryData<string> Projects()
    {
        var src = Path.GetFullPath(Path.Combine(Fixtures.GeneratedDirectory, "..", "..", "..", "src"));
        var data = new TheoryData<string>();
        foreach (var resx in Directory.GetFiles(src, "Strings.resx", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(src, Path.GetDirectoryName(Path.GetDirectoryName(resx)!)!));
        return data;
    }

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    [Theory]
    [MemberData(nameof(Projects))]
    public void Spanish_matches_english(string project)
    {
        var dir = Path.Combine(Path.GetFullPath(Path.Combine(Fixtures.GeneratedDirectory, "..", "..", "..", "src")), project, "Resources");
        var english = Read(Path.Combine(dir, "Strings.resx"));
        var spanishPath = Path.Combine(dir, "Strings.es.resx");
        Assert.True(File.Exists(spanishPath), $"{project} has no Spanish resources");
        var spanish = Read(spanishPath);
        Assert.Empty(english.Keys.Except(spanish.Keys).Order(StringComparer.Ordinal));
        Assert.Empty(spanish.Keys.Except(english.Keys).Order(StringComparer.Ordinal));
        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(spanish[key]) && !string.IsNullOrWhiteSpace(text), $"{project}: {key} has no Spanish text");
            Assert.True(Placeholders(text).SetEquals(Placeholders(spanish[key])), $"{project}: {key} placeholders differ: \"{text}\" / \"{spanish[key]}\"");
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
