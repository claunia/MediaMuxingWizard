using System.Globalization;
using System.Text;

namespace MMW.Metadata.Http;

/// <summary>Builds URL query strings with proper escaping.</summary>
internal sealed class QueryString
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    public QueryString Add(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _items.Add(new(name, value));
        return this;
    }

    public QueryString Add(string name, int? value) =>
        value is { } v ? Add(name, v.ToString(CultureInfo.InvariantCulture)) : this;

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in _items)
        {
            sb.Append(sb.Length == 0 ? '?' : '&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }

        return sb.ToString();
    }

    /// <summary>Query string without the named (secret) parameters, for cache keys.</summary>
    public string ToStringWithout(params string[] names)
    {
        var copy = new QueryString();
        foreach (var (k, v) in _items)
        {
            if (!names.Contains(k, StringComparer.Ordinal))
                copy.Add(k, v);
        }

        return copy.ToString();
    }
}
