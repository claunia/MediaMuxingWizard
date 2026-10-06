using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMW.App.Resources;
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

    private static readonly int[] s_intervals = [0, 1, 2, 5, 10, 15, 20, 30];

    public IReadOnlyList<MenuNode> IntervalMenu => s_intervals.Select(m => new MenuNode(IntervalName(m), InsertEveryCommand, m)).ToList();

    private static string IntervalName(int minutes) => minutes switch
    {
        0 => Strings.Chapters_Interval_AtBeginning,
        1 => Strings.Chapters_Interval_OneMinute,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Chapters_Interval_MinutesFormat, minutes),
    };

    [RelayCommand]
    private void Add()
    {
        var start = Selected is { } s ? s.Start + TimeSpan.FromSeconds(1) : Chapters.Count > 0 ? Chapters.Max(c => c.Start) + TimeSpan.FromMinutes(1) : TimeSpan.Zero;
        var chapter = new Chapter(start, string.Format(CultureInfo.CurrentCulture, Strings.Chapters_NewTitleFormat, Chapters.Count + 1));
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

    public bool CanMakeThumbnails => MMW.Media.Conversion.MediaConversion.IsAvailable && _document.Path is not null &&
                                     _document.Tracks.OfType<VideoTrack>().Any();

    [ObservableProperty]
    private bool _isMakingThumbnails;

    /// <summary>Captures a frame at every chapter start (preview images shown in the list).</summary>
    [RelayCommand]
    private async Task MakeThumbnails()
    {
        if (!CanMakeThumbnails || IsMakingThumbnails)
            return;
        IsMakingThumbnails = true;
        try
        {
            var chapters = Chapters.ToList();
            var video = _document.Tracks.OfType<VideoTrack>().First();
            var images = await MMW.Media.Conversion.ThumbnailGenerator.CaptureManyAsync(_document.Path!, video.Source?.TrackId ?? video.Id,
                chapters.Select(c => c.Start).ToList(), 240);
            for (var i = 0; i < chapters.Count && i < images.Count; i++)
                chapters[i].Thumbnail = images[i];
        }
        finally
        {
            IsMakingThumbnails = false;
        }
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
        var files = await _dialogs.OpenFilesAsync(Strings.Dialog_ImportChapters_Title, [FileFilters.ChapterText, FileFilters.All], allowMultiple: false);
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
                await _dialogs.ShowMessageAsync(Strings.Dialog_ImportChapters_Title, string.Format(CultureInfo.CurrentCulture, Strings.Dialog_ImportChapters_CountMismatchFormat, titles.Count, Chapters.Count));
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
            await _dialogs.ShowMessageAsync(Strings.Dialog_ImportChapters_Title, Strings.Dialog_ImportChapters_NoneFound);
            return;
        }

        TrackActions.ReplaceChapters(_document, chapters);
    }

    [RelayCommand]
    private async Task Export()
    {
        var name = Path.GetFileNameWithoutExtension(_document.Path ?? "chapters") + ".chapters.txt";
        var path = await _dialogs.SaveFileAsync(Strings.Dialog_ExportChapters_Title, name, [FileFilters.Text]);
        if (path is not null)
            await File.WriteAllTextAsync(path, ChapterTextFormat.ToOgg(Chapters));
    }
}
