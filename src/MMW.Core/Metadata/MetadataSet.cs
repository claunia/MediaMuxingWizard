using System.Globalization;

namespace MMW.Core.Metadata;

/// <summary>
/// Container-neutral collection of tags, artwork and free-form items. Values are normalised to the
/// CLR type that matches the tag's <see cref="TagValueKind"/>.
/// </summary>
public sealed class MetadataSet
{
    private readonly Dictionary<TagId, object> _values = [];

    /// <summary>Raised whenever a tag value, artwork list or custom item changes.</summary>
    public event EventHandler? Changed;

    public List<Artwork> Artworks { get; } = [];

    /// <summary>
    /// Free-form items with no <see cref="TagId"/> (for example <c>----:com.example:FOO</c> atoms or unknown
    /// Matroska SimpleTags). Keys are kept as the container wrote them so they round-trip unchanged.
    /// </summary>
    public Dictionary<string, string> CustomItems { get; } = new(StringComparer.Ordinal);

    public IEnumerable<TagId> Keys => _values.Keys.OrderBy(TagCatalog.OrderOf);

    public int Count => _values.Count;

    public bool Contains(TagId id) => _values.ContainsKey(id);

    public object? this[TagId id]
    {
        get => _values.GetValueOrDefault(id);
        set => Set(id, value);
    }

    public void Set(TagId id, object? value)
    {
        var normalised = Normalize(TagCatalog.Get(id).Kind, value);
        if (normalised is null)
        {
            if (_values.Remove(id))
                OnChanged();
            return;
        }

        if (_values.TryGetValue(id, out var existing) && ValuesEqual(existing, normalised))
            return;

        _values[id] = normalised;
        OnChanged();
    }

    public bool Remove(TagId id)
    {
        var removed = _values.Remove(id);
        if (removed)
            OnChanged();
        return removed;
    }

    public void Clear()
    {
        _values.Clear();
        Artworks.Clear();
        CustomItems.Clear();
        OnChanged();
    }

    public string? GetString(TagId id) => this[id] switch
    {
        null => null,
        string s => s,
        IReadOnlyList<string> list => string.Join(", ", list),
        var other => FormatValue(id, other),
    };

    public int? GetInt(TagId id) => this[id] as int?;

    public bool GetBool(TagId id) => this[id] as bool? ?? false;

    public IntPair? GetPair(TagId id) => this[id] as IntPair?;

    public IReadOnlyList<string> GetList(TagId id) => this[id] as IReadOnlyList<string> ?? [];

    /// <summary>Deep copy of values, artwork references and custom items.</summary>
    public MetadataSet Clone()
    {
        var copy = new MetadataSet();
        foreach (var (k, v) in _values)
            copy._values[k] = v;
        copy.Artworks.AddRange(Artworks);
        foreach (var (k, v) in CustomItems)
            copy.CustomItems[k] = v;
        return copy;
    }

    /// <summary>Merges <paramref name="other"/> into this set.</summary>
    /// <param name="other">Source set.</param>
    /// <param name="overwrite">When false, existing values are kept.</param>
    /// <param name="replaceArtworks">When true, existing artwork is replaced by the source artwork (if it has any).</param>
    public void Merge(MetadataSet other, bool overwrite, bool replaceArtworks)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (k, v) in other._values)
        {
            if (overwrite || !_values.ContainsKey(k))
                _values[k] = v;
        }

        if (other.Artworks.Count > 0)
        {
            if (replaceArtworks)
                Artworks.Clear();
            Artworks.AddRange(other.Artworks);
        }

        foreach (var (k, v) in other.CustomItems)
        {
            if (overwrite || !CustomItems.ContainsKey(k))
                CustomItems[k] = v;
        }

        OnChanged();
    }

    public void NotifyArtworksChanged() => OnChanged();

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Converts loosely typed input (usually text from an editor) to the canonical value type.</summary>
    /// <returns>The normalised value, or null when the input is empty.</returns>
    /// <exception cref="FormatException">The text cannot be parsed for the tag kind.</exception>
    public static object? Normalize(TagValueKind kind, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s when kind is not (TagValueKind.String or TagValueKind.Text or TagValueKind.Date or TagValueKind.Rating):
                return ParseText(kind, s);
        }

        return kind switch
        {
            TagValueKind.String or TagValueKind.Text or TagValueKind.Date or TagValueKind.Rating =>
                value is string str ? (str.Length == 0 ? null : str) : Convert.ToString(value, CultureInfo.InvariantCulture),
            TagValueKind.StringList => value switch
            {
                IEnumerable<string> list => list.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() is { Length: > 0 } arr ? arr : null,
                _ => throw new FormatException($"Expected a list of strings, got {value.GetType().Name}."),
            },
            TagValueKind.Bool => value is bool b ? b : Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            TagValueKind.Integer or TagValueKind.Enum => value is int i ? i : Convert.ToInt32(value, CultureInfo.InvariantCulture),
            TagValueKind.IntegerPair => value is IntPair p ? p : throw new FormatException("Expected an IntPair."),
            _ => value,
        };
    }

    private static object? ParseText(TagValueKind kind, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        return kind switch
        {
            TagValueKind.StringList => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            TagValueKind.Bool => text.ToUpperInvariant() switch
            {
                "1" or "YES" or "TRUE" or "ON" => true,
                "0" or "NO" or "FALSE" or "OFF" => false,
                _ => throw new FormatException($"'{text}' is not a yes/no value."),
            },
            TagValueKind.Integer or TagValueKind.Enum => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            TagValueKind.IntegerPair => IntPair.TryParse(text, out var pair) ? pair : throw new FormatException($"'{text}' is not in n/total form."),
            _ => text,
        };
    }

    /// <summary>Formats a value for display or for text-based containers.</summary>
    public static string FormatValue(TagId id, object value)
    {
        var def = TagCatalog.Get(id);
        return value switch
        {
            bool b => b ? "Yes" : "No",
            int i when def.Choices is { } choices => choices.FirstOrDefault(c => c.Value == i)?.Name ?? i.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            IntPair p => p.ToString(),
            IReadOnlyList<string> list => string.Join(", ", list),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static bool ValuesEqual(object a, object b) =>
        a is IReadOnlyList<string> la && b is IReadOnlyList<string> lb ? la.SequenceEqual(lb) : a.Equals(b);
}
