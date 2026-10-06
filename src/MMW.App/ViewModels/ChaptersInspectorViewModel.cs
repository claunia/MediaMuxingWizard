using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Services;
using MMW.Core.Actions;
using MMW.Core.Chapters;
using MMW.Core.Model;

namespace MMW.App.ViewModels;

/// <summary>Inspector for the chapter list.</summary>
public sealed partial class ChaptersInspectorViewModel : ViewModelBase
{
    private readonly MediaDocument _document;
    private readonly IDialogService _dialogs;

    public ChaptersInspectorViewModel(MediaDocument document, IDialogService dialogs)
    {
        _document = document;
        _dialogs = dialogs;
    }

    public ObservableCollection<Chapter> Chapters => _document.Chapters;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private Chapter? _selected;

    private static readonly (int Minutes, string Name)[] s_intervals =
        [(0, "At the beginning"), (1, "1 minute"), (2, "2 minutes"), (5, "5 minutes"), (10, "10 minutes"), (15, "15 minutes"), (20, "20 minutes"), (30, "30 minutes")];

    public IReadOnlyList<MenuNode> IntervalMenu => s_intervals.Select(i => new MenuNode(i.Name, InsertEveryCommand, i.Minutes)).ToList();

    [RelayCommand]
    private void Add()
    {
        var start = Selected is { } s ? s.Start + TimeSpan.FromSeconds(1) : Chapters.Count > 0 ? Chapters.Max(c => c.Start) + TimeSpan.FromMinutes(1) : TimeSpan.Zero;
        var chapter = new Chapter(start, $"Chapter {Chapters.Count + 1}");
        var index = Selected is null ? Chapters.Count : Chapters.IndexOf(Selected) + 1;
        Chapters.Insert(index, chapter);
        TrackActions.EnsureChapterTrack(_document);
        Selected = chapter;
    }

    private bool CanRemove() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        var index = Chapters.IndexOf(Selected!);
        Chapters.RemoveAt(index);
        Selected = Chapters.Count > 0 ? Chapters[Math.Min(index, Chapters.Count - 1)] : null;
    }

    [RelayCommand]
    private void RenameAll() => TrackActions.RenameChapters(_document);

    [RelayCommand]
    private void InsertEvery(int minutes) => TrackActions.InsertChaptersEvery(_document, minutes == 0 ? null : TimeSpan.FromMinutes(minutes));

    /// <summary>Sorts chapters by time after an edit of a start time.</summary>
    public void SortByTime()
    {
        var sorted = Chapters.OrderBy(c => c.Start).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Chapters.IndexOf(sorted[i]);
            if (current != i)
                Chapters.Move(current, i);
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        var files = await _dialogs.OpenFilesAsync("Import Chapters", [FileFilters.ChapterText, FileFilters.All], allowMultiple: false);
        if (files.Count == 1)
            await ImportFileAsync(files[0]);
    }

    /// <summary>Imports chapters from a text file, or renames chapters from a CSV file.</summary>
    public async Task ImportFileAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var titles = ChapterTextFormat.ParseTitlesCsv(text);
            if (titles.Count != Chapters.Count)
            {
                await _dialogs.ShowMessageAsync("Import Chapters", $"The CSV file has {titles.Count} titles but the file has {Chapters.Count} chapters.");
                return;
            }

            var ordered = Chapters.OrderBy(c => c.Start).ToList();
            for (var i = 0; i < titles.Count; i++)
                ordered[i].Title = titles[i];
            return;
        }

        var chapters = ChapterTextFormat.Parse(text);
        if (chapters.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Import Chapters", "No chapters were found in the file.");
            return;
        }

        TrackActions.ReplaceChapters(_document, chapters);
    }

    [RelayCommand]
    private async Task Export()
    {
        var name = Path.GetFileNameWithoutExtension(_document.Path ?? "chapters") + ".chapters.txt";
        var path = await _dialogs.SaveFileAsync("Export Chapters", name, [FileFilters.Text]);
        if (path is not null)
            await File.WriteAllTextAsync(path, ChapterTextFormat.ToOgg(Chapters));
    }
}
