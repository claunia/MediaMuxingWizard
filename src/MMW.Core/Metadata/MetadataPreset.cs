using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMW.Core.Metadata;

/// <summary>A named, reusable set of tags and artwork (Subler's "Sets").</summary>
public sealed class MetadataPreset
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Tag values in their storage form (string, string[], bool, int or "n/total").</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];

    /// <summary>Artwork images, base64 encoded.</summary>
    public List<string> Artworks { get; set; } = [];

    /// <summary>When applied, existing artwork is replaced (otherwise preset artwork is appended).</summary>
    public bool ReplaceArtworks { get; set; }

    /// <summary>When applied, existing tag values are overwritten (otherwise only missing tags are filled).</summary>
    public bool ReplaceAnnotations { get; set; } = true;

    public static MetadataPreset FromSet(string name, MetadataSet set, bool replaceArtworks, bool replaceAnnotations)
    {
        ArgumentNullException.ThrowIfNull(set);
        var preset = new MetadataPreset { Name = name, ReplaceArtworks = replaceArtworks, ReplaceAnnotations = replaceAnnotations };
        foreach (var id in set.Keys)
        {
            var value = set[id];
            preset.Values[id.ToString()] = value switch
            {
                IntPair p => JsonSerializer.SerializeToElement(p.ToString()),
                IReadOnlyList<string> list => JsonSerializer.SerializeToElement(list.ToArray()),
                bool b => JsonSerializer.SerializeToElement(b),
                int i => JsonSerializer.SerializeToElement(i),
                _ => JsonSerializer.SerializeToElement(Convert.ToString(value, CultureInfo.InvariantCulture)),
            };
        }

        preset.Artworks.AddRange(set.Artworks.Select(a => Convert.ToBase64String(a.Data)));
        return preset;
    }

    /// <summary>Converts the preset to a metadata set (unknown or invalid entries are skipped).</summary>
    public MetadataSet ToSet()
    {
        var set = new MetadataSet();
        foreach (var (key, element) in Values)
        {
            if (!Enum.TryParse<TagId>(key, out var id))
                continue;
            object? value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetInt32(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => element.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
                _ => null,
            };
            try
            {
                set.Set(id, value);
            }
            catch (FormatException)
            {
            }
        }

        foreach (var b64 in Artworks)
        {
            try
            {
                set.Artworks.Add(new Artwork(Convert.FromBase64String(b64)));
            }
            catch (FormatException)
            {
            }
        }

        return set;
    }

    /// <summary>Applies the preset to <paramref name="target"/>.</summary>
    public void ApplyTo(MetadataSet target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Merge(ToSet(), ReplaceAnnotations, ReplaceArtworks);
    }

    /// <summary>A built-in preset that adds the given tags with empty values (used for "Movie"/"TV Show").</summary>
    [JsonIgnore]
    public IReadOnlyList<TagId>? EmptyTags { get; init; }
}
