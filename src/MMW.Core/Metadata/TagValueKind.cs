namespace MMW.Core.Metadata;

/// <summary>How a tag value is stored and which editor the UI shows for it.</summary>
public enum TagValueKind
{
    /// <summary>Single-line text. Value type: <see cref="string"/>.</summary>
    String,

    /// <summary>Multi-line text. Value type: <see cref="string"/>.</summary>
    Text,

    /// <summary>List of names, edited as comma separated text. Value type: <see cref="IReadOnlyList{T}"/> of string.</summary>
    StringList,

    /// <summary>Value type: <see cref="bool"/>.</summary>
    Bool,

    /// <summary>Value type: <see cref="int"/>.</summary>
    Integer,

    /// <summary>"n/total" pair. Value type: <see cref="IntPair"/>.</summary>
    IntegerPair,

    /// <summary>Release-style date kept as text (year, date or ISO timestamp). Value type: <see cref="string"/>.</summary>
    Date,

    /// <summary>Integer chosen from a fixed list. Value type: <see cref="int"/>.</summary>
    Enum,

    /// <summary>iTunes content rating "prefix|code|value|annotation". Value type: <see cref="string"/>.</summary>
    Rating,
}

/// <summary>A number with an optional total, used by Track # and Disk #.</summary>
public readonly record struct IntPair(int Number, int Total)
{
    public override string ToString() => Total > 0 ? $"{Number}/{Total}" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out IntPair value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var number))
            return false;

        var total = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && !int.TryParse(parts[1], out total))
            return false;

        value = new IntPair(number, total);
        return true;
    }
}
