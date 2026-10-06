using System.Xml;
using System.Xml.Linq;

namespace MMW.Formats.Mp4.Metadata;

/// <summary>Minimal XML property-list support for the <c>iTunMOVI</c> freeform item.</summary>
internal static class PropertyList
{
    /// <summary>Parses a plist whose root is a dictionary of string or array-of-{name} values.</summary>
    public static Dictionary<string, object> ParseDictionary(string xml)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return result;
        }

        var dict = doc.Root?.Element("dict");
        if (dict is null)
            return result;

        var elements = dict.Elements().ToList();
        for (var i = 0; i + 1 < elements.Count; i += 2)
        {
            if (elements[i].Name != "key")
                continue;
            var key = elements[i].Value;
            var value = elements[i + 1];
            switch (value.Name.LocalName)
            {
                case "string":
                    result[key] = value.Value;
                    break;
                case "array":
                    result[key] = value.Elements("dict")
                        .Select(d => NameOf(d))
                        .Where(n => n.Length > 0)
                        .ToArray();
                    break;
            }
        }

        return result;
    }

    private static string NameOf(XElement dict)
    {
        var items = dict.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            if (items[i].Name == "key" && items[i].Value == "name")
                return items[i + 1].Value;
        }

        return string.Empty;
    }

    /// <summary>Builds the plist used by iTunes for cast/crew.</summary>
    public static string BuildDictionary(IEnumerable<KeyValuePair<string, object>> entries)
    {
        var dict = new XElement("dict");
        foreach (var (key, value) in entries)
        {
            dict.Add(new XElement("key", key));
            switch (value)
            {
                case string s:
                    dict.Add(new XElement("string", s));
                    break;
                case IEnumerable<string> names:
                    dict.Add(new XElement("array", names.Select(n => new XElement("dict", new XElement("key", "name"), new XElement("string", n)))));
                    break;
            }
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dict));
        using var sw = new Utf8StringWriter();
        doc.Save(sw, SaveOptions.None);
        return sw.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}
