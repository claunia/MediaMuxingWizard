using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.Core.Metadata;

namespace MMW.App.ViewModels;

/// <summary>Editing context shared by the tag editors of one document.</summary>
public interface ITagEditorHost
{
    void SetTag(TagId id, object? value);

    void RemoveTag(TagId id);

    MetadataSet Metadata { get; }

    string RatingsCountry { get; }
}

/// <summary>One row of the tag editor.</summary>
public abstract partial class TagItemViewModel : ViewModelBase
{
    protected TagItemViewModel(TagDefinition definition, ITagEditorHost host)
    {
        Definition = definition;
        Host = host;
    }

    public TagDefinition Definition { get; }

    protected ITagEditorHost Host { get; }

    /// <summary>The tag's name in the UI language.</summary>
    public string Name => Definition.DisplayName;

    public TagId Id => Definition.Id;

    [ObservableProperty]
    private string? _error;

    /// <summary>Re-reads the value from the metadata set (after undo, presets, …).</summary>
    public abstract void Refresh();

    [RelayCommand]
    private void Remove() => Host.RemoveTag(Id);

    protected void Commit(object? value)
    {
        try
        {
            Host.SetTag(Id, value);
            Error = null;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
        }
    }

    public static TagItemViewModel Create(TagDefinition definition, ITagEditorHost host)
    {
        TagItemViewModel vm = definition.Kind switch
        {
            TagValueKind.Bool => new BoolTagViewModel(definition, host),
            TagValueKind.Enum => new EnumTagViewModel(definition, host),
            TagValueKind.Rating => new RatingTagViewModel(definition, host),
            TagValueKind.Text => new TextTagViewModel(definition, host, multiline: true),
            TagValueKind.StringList => new PeopleTagViewModel(definition, host),
            _ when definition.Id == TagId.Genre => new GenreTagViewModel(definition, host),
            _ => new TextTagViewModel(definition, host, multiline: false),
        };
        vm.Refresh();
        return vm;
    }
}

/// <summary>Single- or multi-line text, numbers, n/total pairs and dates.</summary>
public partial class TextTagViewModel(TagDefinition definition, ITagEditorHost host, bool multiline) : TagItemViewModel(definition, host)
{
    private bool _refreshing;

    public bool IsMultiline { get; } = multiline;

    public string Watermark => Definition.Kind switch
    {
        TagValueKind.IntegerPair => Strings.Tag_WatermarkPair,
        TagValueKind.Date => Strings.Tag_WatermarkDate,
        TagValueKind.Integer => "0",
        _ => string.Empty,
    };

    [ObservableProperty]
    private string _text = string.Empty;

    partial void OnTextChanged(string value)
    {
        if (!_refreshing)
            Commit(value);
    }

    public override void Refresh()
    {
        _refreshing = true;
        Text = Host.Metadata[Id] is { } v ? MetadataSet.FormatValue(Id, v) : string.Empty;
        _refreshing = false;
    }
}

public sealed partial class GenreTagViewModel(TagDefinition definition, ITagEditorHost host) : TextTagViewModel(definition, host, multiline: false)
{
    public IReadOnlyList<string> Suggestions => TagCatalog.VideoGenres;
}

/// <summary>Lists of names (cast, directors, …) edited as removable chips plus an entry box.</summary>
public sealed partial class PeopleTagViewModel(TagDefinition definition, ITagEditorHost host) : TagItemViewModel(definition, host)
{
    public ObservableCollection<string> People { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPersonCommand))]
    private string _newPerson = string.Empty;

    public override void Refresh()
    {
        People.Clear();
        foreach (var p in Host.Metadata.GetList(Id))
            People.Add(p);
    }

    private bool CanAddPerson() => !string.IsNullOrWhiteSpace(NewPerson);

    [RelayCommand(CanExecute = nameof(CanAddPerson))]
    private void AddPerson()
    {
        var names = NewPerson.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Commit(People.Concat(names).ToArray());
        NewPerson = string.Empty;
    }

    [RelayCommand]
    private void RemovePerson(string name) => Commit(People.Where(p => p != name).ToArray());
}

public sealed partial class BoolTagViewModel(TagDefinition definition, ITagEditorHost host) : TagItemViewModel(definition, host)
{
    private bool _refreshing;

    [ObservableProperty]
    private bool _value;

    partial void OnValueChanged(bool value)
    {
        if (!_refreshing)
            Commit(value);
    }

    public override void Refresh()
    {
        _refreshing = true;
        Value = Host.Metadata.GetBool(Id);
        _refreshing = false;
    }
}

public sealed partial class EnumTagViewModel(TagDefinition definition, ITagEditorHost host) : TagItemViewModel(definition, host)
{
    private bool _refreshing;

    public IReadOnlyList<EnumChoice> Choices => Definition.Choices ?? [];

    [ObservableProperty]
    private EnumChoice? _selected;

    partial void OnSelectedChanged(EnumChoice? value)
    {
        if (!_refreshing && value is not null)
            Commit(value.Value);
    }

    public override void Refresh()
    {
        _refreshing = true;
        var current = Host.Metadata.GetInt(Id);
        Selected = current is null ? null : Choices.FirstOrDefault(c => c.Value == current) ?? new EnumChoice(current.Value, string.Format(CultureInfo.CurrentCulture, Strings.Tag_UnknownChoiceFormat, current));
        _refreshing = false;
    }
}

public sealed partial class RatingTagViewModel(TagDefinition definition, ITagEditorHost host) : TagItemViewModel(definition, host)
{
    private bool _refreshing;

    public IReadOnlyList<ContentRatingEntry> Choices => Ratings.ForCountry(Host.RatingsCountry);

    [ObservableProperty]
    private ContentRatingEntry? _selected;

    /// <summary>Raw value shown when the stored rating is not in the list.</summary>
    [ObservableProperty]
    private string _raw = string.Empty;

    partial void OnSelectedChanged(ContentRatingEntry? value)
    {
        if (!_refreshing && value is not null)
            Commit(value.Encoded);
    }

    public override void Refresh()
    {
        _refreshing = true;
        var encoded = Host.Metadata.GetString(Id);
        Selected = Ratings.Find(encoded);
        Raw = Selected is null ? encoded ?? string.Empty : string.Empty;
        OnPropertyChanged(nameof(Choices));
        _refreshing = false;
    }
}
