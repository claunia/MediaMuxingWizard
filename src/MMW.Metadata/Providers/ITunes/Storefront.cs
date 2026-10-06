using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMW.Metadata.Providers.ITunes;

/// <summary>An iTunes / Apple TV storefront.</summary>
/// <param name="Name">English country name.</param>
/// <param name="Iso">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Id">Apple storefront id (the value of the iTunes Country tag, e.g. 143441 for the US).</param>
/// <param name="Locale">Primary BCP-47 locale of the store (used by the Apple TV API).</param>
public sealed record Storefront(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iso")] string Iso,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("locale")] string Locale)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Table of iTunes storefronts (embedded resource <c>storefronts.json</c>).</summary>
public static class Storefronts
{
    private static readonly Lazy<IReadOnlyList<Storefront>> s_all = new(Load);

    /// <summary>All storefronts sorted by name.</summary>
    public static IReadOnlyList<Storefront> All => s_all.Value;

    /// <summary>The United States store, used as default.</summary>
    public static Storefront UnitedStates => All.First(s => s.Iso == "US");

    /// <summary>
    /// Finds a storefront by English name, ISO code (case-insensitive) or numeric storefront id;
    /// null when nothing matches.
    /// </summary>
    public static Storefront? Find(string? nameOrCode)
    {
        if (string.IsNullOrWhiteSpace(nameOrCode))
            return null;
        var text = nameOrCode.Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return All.FirstOrDefault(s => s.Id == id);
        if (text.Equals("UK", StringComparison.OrdinalIgnoreCase))
            text = "GB";
        return All.FirstOrDefault(s => s.Iso.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(s => s.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Like <see cref="Find"/> but falls back to the US store.</summary>
    public static Storefront FindOrDefault(string? nameOrCode) => Find(nameOrCode) ?? UnitedStates;

    private static List<Storefront> Load()
    {
        using var stream = typeof(Storefronts).Assembly.GetManifestResourceStream("MMW.Metadata.Resources.storefronts.json")
                           ?? throw new InvalidOperationException("storefronts.json resource missing.");
        return JsonSerializer.Deserialize(stream, StorefrontJsonContext.Default.ListStorefront) ?? [];
    }
}

[JsonSerializable(typeof(List<Storefront>))]
internal sealed partial class StorefrontJsonContext : JsonSerializerContext;
