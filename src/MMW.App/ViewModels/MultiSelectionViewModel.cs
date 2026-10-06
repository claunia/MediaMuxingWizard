using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
using MMW.Core.Languages;
using MMW.Core.Model;
using MMW.Core.Undo;

namespace MMW.App.ViewModels;

/// <summary>Edits the properties shared by several selected tracks at once.</summary>
public sealed partial class MultiSelectionViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly UndoStack _undo;

    public MultiSelectionViewModel(IReadOnlyList<Track> tracks, UndoStack undo)
    {
        _tracks = tracks;
        _undo = undo;
    }

    public string Text => string.Format(CultureInfo.CurrentCulture, Strings.MultiSelection_CountFormat, _tracks.Count);

    public bool HasTracks => _tracks.Count > 0;

    public IReadOnlyList<Language> Languages => LanguageTable.All;

    /// <summary>The common language, or null when the tracks differ.</summary>
    public Language? SelectedLanguage
    {
        get
        {
            var tags = _tracks.Select(t => t.Language).Distinct().ToList();
            return tags.Count == 1 ? LanguageTable.Find(tags[0]) : null;
        }
        set
        {
            if (value is null)
                return;
            using (_undo.Transaction(Strings.Undo_ChangeLanguage))
            {
                foreach (var t in _tracks)
                    t.Language = value.Tag;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>True/false when all tracks agree, null when mixed.</summary>
    public bool? Enabled
    {
        get
        {
            var values = _tracks.Select(t => t.Enabled).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }
        set
        {
            if (value is null)
                return;
            using (_undo.Transaction(value.Value ? Strings.Undo_EnableTracks : Strings.Undo_DisableTracks))
            {
                foreach (var t in _tracks)
                    t.Enabled = value.Value;
            }

            OnPropertyChanged();
        }
    }

    public Choice<int>? SelectedAlternateGroup
    {
        get
        {
            var groups = _tracks.Select(t => t.AlternateGroup).Distinct().ToList();
            return groups.Count == 1 ? TrackInspectorViewModel.AlternateGroups.FirstOrDefault(g => g.Value == groups[0]) : null;
        }
        set
        {
            if (value is null)
                return;
            using (_undo.Transaction(Strings.Undo_ChangeAlternateGroup))
            {
                foreach (var t in _tracks)
                    t.AlternateGroup = value.Value;
            }

            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ClearNames()
    {
        using (_undo.Transaction(Strings.Undo_ClearTrackNames))
        {
            foreach (var t in _tracks)
                t.Name = string.Empty;
        }
    }
}
