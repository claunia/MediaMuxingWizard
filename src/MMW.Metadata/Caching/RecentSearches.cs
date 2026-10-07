using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using MMW.Metadata.Resources;
using MMW.Metadata.Search;

namespace MMW.Metadata.Caching;

/// <summary>Most-recently-used list of search queries, optionally persisted to a JSON file.</summary>
public sealed class RecentSearches
{
    private readonly List<SearchQuery> _items = [];
    private readonly string? _path;

    /// <summary>Creates the list, loading <paramref name="path"/> when it exists.</summary>
    /// <param name="path">JSON file to persist to; null keeps the list in memory only.</param>
    /// <param name="capacity">Maximum number of entries kept.</param>
    public RecentSearches(string? path = null, int capacity = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _path = path;
        Capacity = capacity;
        if (path is not null && File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), RecentSearchesJsonContext.Default.ListSearchQuery);
                if (loaded is not null)
                    _items.AddRange(loaded.Where(q => !string.IsNullOrWhiteSpace(q.Title)).Take(capacity));
            }
            catch (JsonException ex)
            {
                MMW.Core.Diagnostics.AppLog.Warn(string.Format(CultureInfo.CurrentCulture, Strings.Search_IgnoringRecentSearches, ex.Message));
            }
        }
    }

    /// <summary>Maximum number of entries.</summary>
    public int Capacity { get; }

    /// <summary>Entries, newest first.</summary>
    public IReadOnlyList<SearchQuery> Items => _items;

    /// <summary>Adds (or moves to the top) a query and saves the list.</summary>
    public void Add(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Title))
            return;
        _items.RemoveAll(q => q.Kind == query.Kind && q.Season == query.Season && q.Episode == query.Episode &&
                              q.Year == query.Year && string.Equals(q.Title, query.Title, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, query);
        if (_items.Count > Capacity)
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        Save();
    }

    /// <summary>Recent series/movie titles of one kind, newest first (for autocompletion).</summary>
    public IReadOnlyList<string> Titles(MediaSearchKind kind) =>
        _items.Where(q => q.Kind == kind).Select(q => q.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Removes every entry and saves.</summary>
    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Save()
    {
        if (_path is null)
            return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items, RecentSearchesJsonContext.Default.ListSearchQuery));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<SearchQuery>))]
internal sealed partial class RecentSearchesJsonContext : JsonSerializerContext;
