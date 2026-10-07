using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.App.Services;
using MMW.Core.Diagnostics;
using MMW.Core.Metadata;
using MMW.Core.Undo;

namespace MMW.App.ViewModels;

public sealed class TagGroupViewModel(string name, IReadOnlyList<TagItemViewModel> items)
{
    public string Name { get; } = name;

    public IReadOnlyList<TagItemViewModel> Items { get; } = items;
}

/// <summary>Inspector for the "Metadata" row: tags, artwork and sets.</summary>
public sealed partial class MetadataInspectorViewModel : ViewModelBase, ITagEditorHost
{
    private readonly UndoStack _undo;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly Dictionary<TagId, TagItemViewModel> _items = [];
    private bool _suspendRefresh;

    public MetadataInspectorViewModel(MetadataSet metadata, UndoStack undo, IDialogService dialogs, ISettingsService settings)
    {
        Metadata = metadata;
        _undo = undo;
        _dialogs = dialogs;
        _settings = settings;
        metadata.Changed += (_, _) =>
        {
            if (!_suspendRefresh)
                Rebuild();
        };
        Rebuild();
    }

    public MetadataSet Metadata { get; }

    public string RatingsCountry => _settings.Settings.RatingsCountry;

    public ObservableCollection<TagGroupViewModel> Groups { get; } = [];

    public ObservableCollection<ArtworkItemViewModel> Artworks { get; } = [];

    public ObservableCollection<MenuNode> AddTagMenu { get; } = [];

    public ObservableCollection<MenuNode> SetsMenu { get; } = [];

    /// <summary>Filters the tag list by name.</summary>
    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveArtworkCommand), nameof(MoveArtworkUpCommand), nameof(MoveArtworkDownCommand), nameof(ExportArtworkCommand))]
    private ArtworkItemViewModel? _selectedArtwork;

    [ObservableProperty]
    private double _thumbnailSize = 160;

    /// <summary>Selected inspector tab (0 = tags, 1 = artwork); remembered per document.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public bool IsEmpty => Metadata.Count == 0;

    partial void OnFilterChanged(string value) => RebuildGroups();

    // ------------------------------------------------------------------ ITagEditorHost

    public void SetTag(TagId id, object? value)
    {
        var definition = TagCatalog.Get(id);
        var normalised = MetadataSet.Normalize(definition.Kind, value);
        var old = Metadata[id];
        var oldText = old is null ? null : MetadataSet.FormatValue(id, old);
        var newText = normalised is null ? null : MetadataSet.FormatValue(id, normalised);
        if (oldText == newText)
            return;

        // Clearing a value removes the tag but keeps its (empty) row until the user removes it.
        RecordAndApply(string.Format(CultureInfo.CurrentCulture, normalised is null ? Strings.Undo_ClearTagFormat : Strings.Undo_ChangeTagFormat, definition.Name),
            () => Metadata.Set(id, normalised), () => Metadata.Set(id, old));
    }

    public void RemoveTag(TagId id)
    {
        var old = Metadata[id];
        _items.Remove(id);
        RecordAndApply(string.Format(CultureInfo.CurrentCulture, Strings.Undo_RemoveTagFormat, TagCatalog.Get(id).Name), () => Metadata.Remove(id), () => Metadata.Set(id, old));
        Rebuild();
    }

    private void RecordAndApply(string description, Action redo, Action undo)
    {
        var edit = new DelegateEdit(description, () => { redo(); Rebuild(); }, () => { undo(); Rebuild(); });
        _suspendRefresh = true;
        try
        {
            redo();
        }
        finally
        {
            _suspendRefresh = false;
        }

        _undo.Record(edit);
        RefreshItems();
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ------------------------------------------------------------------ tags

    [RelayCommand]
    private void AddTag(TagId id)
    {
        if (!_items.ContainsKey(id))
            _items[id] = TagItemViewModel.Create(TagCatalog.Get(id), this);
        RebuildGroups();
    }

    private void Rebuild()
    {
        // Keep rows that exist in the metadata plus rows the user added but has not filled yet.
        foreach (var id in Metadata.Keys)
        {
            if (!_items.ContainsKey(id))
                _items[id] = TagItemViewModel.Create(TagCatalog.Get(id), this);
        }

        RefreshItems();
        RebuildGroups();
        RebuildArtworks();
        RebuildMenus();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshItems()
    {
        foreach (var item in _items.Values)
            item.Refresh();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        var visible = _items.Values
            .Where(i => Filter.Length == 0 || i.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => TagCatalog.OrderOf(i.Id));
        foreach (var group in visible.GroupBy(i => i.Definition.Group))
            Groups.Add(new TagGroupViewModel(TagCatalog.GroupDisplayName(group.Key), group.ToList()));
    }

    private void RebuildMenus()
    {
        AddTagMenu.Clear();
        foreach (var group in TagCatalog.All.Where(d => !_items.ContainsKey(d.Id)).GroupBy(d => d.Group))
        {
            AddTagMenu.Add(new MenuNode(TagCatalog.GroupDisplayName(group.Key),
                children: group.Select(d => new MenuNode(d.Name, AddTagCommand, d.Id)).ToList()));
        }

        SetsMenu.Clear();
        SetsMenu.Add(new MenuNode(Strings.Sets_Save, SaveSetCommand));
        SetsMenu.Add(MenuNode.Separator);
        SetsMenu.Add(new MenuNode(Strings.Sets_All, ApplyBuiltInSetCommand, "All"));
        SetsMenu.Add(new MenuNode(Strings.Sets_Movie, ApplyBuiltInSetCommand, "Movie"));
        SetsMenu.Add(new MenuNode(Strings.Sets_TvShow, ApplyBuiltInSetCommand, "TV Show"));
        var presets = _settings.Settings.Presets;
        if (presets.Count > 0)
        {
            SetsMenu.Add(MenuNode.Separator);
            for (var i = 0; i < presets.Count; i++)
                SetsMenu.Add(new MenuNode(presets[i].Name, ApplyPresetCommand, presets[i], gesture: i < 9 ? $"{i + 1}" : null));
        }
    }

    // ------------------------------------------------------------------ sets

    [RelayCommand]
    private void ApplyBuiltInSet(string name)
    {
        IEnumerable<TagId> ids = name switch
        {
            "Movie" => TagCatalog.MovieSet,
            "TV Show" => TagCatalog.TvShowSet,
            _ => TagCatalog.All.Select(d => d.Id),
        };
        foreach (var id in ids)
        {
            if (!_items.ContainsKey(id))
                _items[id] = TagItemViewModel.Create(TagCatalog.Get(id), this);
        }

        var mediaKind = name switch
        {
            "Movie" => TagCatalog.MediaKindMovie,
            "TV Show" => TagCatalog.MediaKindTvShow,
            _ => (int?)null,
        };
        if (mediaKind is { } kind)
            SetTag(TagId.MediaKind, kind);
        RebuildGroups();
        RebuildMenus();
    }

    [RelayCommand]
    private void ApplyPreset(MetadataPreset preset)
    {
        var before = Metadata.Clone();
        _suspendRefresh = true;
        try
        {
            preset.ApplyTo(Metadata);
        }
        finally
        {
            _suspendRefresh = false;
        }

        var after = Metadata.Clone();
        _undo.Record(new DelegateEdit(string.Format(CultureInfo.CurrentCulture, Strings.Undo_ApplySetFormat, preset.Name), () => Restore(after), () => Restore(before)));
        Rebuild();
    }

    /// <summary>Refreshes menus and pickers after the preferences changed (sets, ratings country).</summary>
    public void SettingsChanged() => Rebuild();

    /// <summary>Applies a change to the whole metadata set (search results, NFO import) as one undo step.</summary>
    public void ApplyExternal(string description, Action change)
    {
        var before = Metadata.Clone();
        _suspendRefresh = true;
        try
        {
            change();
        }
        finally
        {
            _suspendRefresh = false;
        }

        var after = Metadata.Clone();
        _undo.Record(new DelegateEdit(description, () => Restore(after), () => Restore(before)));
        Rebuild();
    }

    /// <summary>Applies the n-th user preset (Ctrl/Cmd+1…9).</summary>
    public void ApplyPresetAt(int index)
    {
        if (index >= 0 && index < _settings.Settings.Presets.Count)
            ApplyPreset(_settings.Settings.Presets[index]);
    }

    [RelayCommand]
    private async Task SaveSet()
    {
        var result = await _dialogs.ShowDialogAsync(new SaveSetDialogViewModel());
        if (result is null)
            return;
        var presets = _settings.Settings.Presets;
        presets.RemoveAll(p => p.Name == result.Name);
        presets.Add(MetadataPreset.FromSet(result.Name, Metadata, !result.KeepArtworks, !result.KeepAnnotations));
        _settings.Save();
        RebuildMenus();
        AppLog.Info(string.Format(CultureInfo.CurrentCulture, Strings.Log_SavedSetFormat, result.Name));
    }

    private void Restore(MetadataSet snapshot)
    {
        _suspendRefresh = true;
        try
        {
            Metadata.Clear();
            Metadata.Merge(snapshot, overwrite: true, replaceArtworks: true);
        }
        finally
        {
            _suspendRefresh = false;
        }

        Rebuild();
    }

    // ------------------------------------------------------------------ artwork

    private void RebuildArtworks()
    {
        if (Artworks.Select(a => a.Artwork).SequenceEqual(Metadata.Artworks))
            return;
        var selected = SelectedArtwork?.Artwork;
        foreach (var a in Artworks)
            a.Dispose();
        Artworks.Clear();
        foreach (var art in Metadata.Artworks)
            Artworks.Add(new ArtworkItemViewModel(art));
        SelectedArtwork = Artworks.FirstOrDefault(a => a.Artwork == selected);
    }

    private void EditArtworks(string description, Action<List<Artwork>> change)
    {
        var before = Metadata.Artworks.ToList();
        var after = Metadata.Artworks.ToList();
        change(after);
        void Apply(List<Artwork> list)
        {
            Metadata.Artworks.Clear();
            Metadata.Artworks.AddRange(list);
            Metadata.NotifyArtworksChanged();
        }

        Apply(after);
        _undo.Record(new DelegateEdit(description, () => Apply(after), () => Apply(before)));
    }

    /// <summary>Adds images (from files, drag and drop or the clipboard).</summary>
    public void AddArtworkData(IEnumerable<byte[]> images)
    {
        var valid = images.Where(d => Artwork.Detect(d) != ArtworkFormat.Unknown).Select(d => new Artwork(d)).ToList();
        if (valid.Count == 0)
            return;
        EditArtworks(valid.Count == 1 ? Strings.Undo_AddArtwork : Strings.Undo_AddArtworks, list => list.AddRange(valid));
        SelectedTab = 1;
    }

    [RelayCommand]
    private async Task AddArtwork()
    {
        var files = await _dialogs.OpenFilesAsync(Strings.Dialog_AddArtwork_Title, [FileFilters.Images], allowMultiple: true);
        var data = new List<byte[]>();
        foreach (var f in files)
            data.Add(await File.ReadAllBytesAsync(f));
        AddArtworkData(data);
    }

    private bool HasSelectedArtwork() => SelectedArtwork is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedArtwork))]
    private void RemoveArtwork()
    {
        var art = SelectedArtwork!.Artwork;
        EditArtworks(Strings.Undo_RemoveArtwork, list => list.Remove(art));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedArtwork))]
    private void MoveArtworkUp() => MoveArtwork(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedArtwork))]
    private void MoveArtworkDown() => MoveArtwork(1);

    private void MoveArtwork(int delta)
    {
        var art = SelectedArtwork!.Artwork;
        var index = Metadata.Artworks.IndexOf(art);
        var target = index + delta;
        if (target < 0 || target >= Metadata.Artworks.Count)
            return;
        EditArtworks(Strings.Undo_MoveArtwork, list =>
        {
            list.RemoveAt(index);
            list.Insert(target, art);
        });
        SelectedArtwork = Artworks.FirstOrDefault(a => a.Artwork == art);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedArtwork))]
    private async Task ExportArtwork()
    {
        var art = SelectedArtwork!.Artwork;
        var path = await _dialogs.SaveFileAsync(Strings.Dialog_ExportArtwork_Title, Strings.File_DefaultArtworkName + art.Extension, [FileFilters.Images]);
        if (path is not null)
            await File.WriteAllBytesAsync(path, art.Data);
    }
}
