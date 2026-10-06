using System.Text.Json.Serialization;
using MMW.Core.Actions;
using MMW.Core.Chapters;
using MMW.Core.Metadata;
using MMW.Core.Model;

namespace MMW.Queue;

/// <summary>
/// A step applied to every queue item before it is saved. Actions defined in other assemblies are registered
/// with <see cref="QueueStore.RegisterAction{T}"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$action")]
[JsonDerivedType(typeof(ApplyPresetAction), "applyPreset")]
[JsonDerivedType(typeof(ClearMetadataAction), "clearMetadata")]
[JsonDerivedType(typeof(SetOutputFileNameAction), "setOutputFileName")]
[JsonDerivedType(typeof(OrganizeGroupsAction), "organizeGroups")]
[JsonDerivedType(typeof(FixFallbacksAction), "fixFallbacks")]
[JsonDerivedType(typeof(CompleteLanguagesAction), "completeLanguages")]
[JsonDerivedType(typeof(EnableTrackWithLanguageAction), "enableTrackWithLanguage")]
[JsonDerivedType(typeof(ClearTrackNamesAction), "clearTrackNames")]
[JsonDerivedType(typeof(PrettifyAudioNamesAction), "prettifyAudioNames")]
[JsonDerivedType(typeof(RenameChaptersAction), "renameChapters")]
[JsonDerivedType(typeof(ApplyColorSpaceAction), "applyColorSpace")]
[JsonDerivedType(typeof(ImportChaptersFileAction), "importChaptersFile")]
public abstract class QueueAction
{
    /// <summary>Short description shown in the queue window.</summary>
    [JsonIgnore]
    public abstract string Description { get; }

    public abstract Task ApplyAsync(QueueContext context, CancellationToken cancellationToken);
}

/// <summary>Applies a saved set of tags and artwork.</summary>
public sealed class ApplyPresetAction : QueueAction
{
    public MetadataPreset Preset { get; set; } = new();

    public override string Description => $"Apply set \"{Preset.Name}\"";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        Preset.ApplyTo(context.Document.Metadata);
        return Task.CompletedTask;
    }
}

public sealed class ClearMetadataAction : QueueAction
{
    public override string Description => "Clear existing metadata";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        context.Document.Metadata.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Names the output file from its tags.</summary>
public sealed class SetOutputFileNameAction : QueueAction
{
    public string MovieFormat { get; set; } = FileNameFormatter.DefaultMovieFormat;

    public string TvFormat { get; set; } = FileNameFormatter.DefaultTvFormat;

    public override string Description => "Set output file name";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        if (FileNameFormatter.FormatFor(context.Document.Metadata, MovieFormat, TvFormat) is { Length: > 0 } name)
            context.OutputBaseName = name;
        return Task.CompletedTask;
    }
}

public sealed class OrganizeGroupsAction : QueueAction
{
    public bool InferMediaCharacteristics { get; set; } = true;

    public override string Description => "Organize alternate groups";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        GroupActions.OrganizeAlternateGroups(context.Document, InferMediaCharacteristics);
        return Task.CompletedTask;
    }
}

public sealed class FixFallbacksAction : QueueAction
{
    public override string Description => "Fix audio fallbacks";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        GroupActions.FixAudioFallbacks(context.Document);
        return Task.CompletedTask;
    }
}

public sealed class CompleteLanguagesAction : QueueAction
{
    public string Language { get; set; } = "en";

    public override string Description => $"Complete track languages ({Core.Languages.LanguageTable.DisplayName(Language)})";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        GroupActions.CompleteLanguages(context.Document, Language);
        return Task.CompletedTask;
    }
}

public sealed class EnableTrackWithLanguageAction : QueueAction
{
    public TrackKind Kind { get; set; } = TrackKind.Audio;

    public string Language { get; set; } = "en";

    public override string Description => $"Enable {(Kind == TrackKind.Audio ? "audio" : "subtitle")} track in {Core.Languages.LanguageTable.DisplayName(Language)}";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        if (!GroupActions.EnableTrackWithLanguage(context.Document, Kind, Language))
            context.Log($"No {Kind.ToString().ToLowerInvariant()} track in {Language}.");
        return Task.CompletedTask;
    }
}

public sealed class ClearTrackNamesAction : QueueAction
{
    public override string Description => "Clear track names";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        TrackActions.ClearTrackNames(context.Document);
        return Task.CompletedTask;
    }
}

public sealed class PrettifyAudioNamesAction : QueueAction
{
    public override string Description => "Prettify audio track names";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        TrackActions.PrettifyAudioNames(context.Document);
        return Task.CompletedTask;
    }
}

public sealed class RenameChaptersAction : QueueAction
{
    public override string Description => "Rename chapters";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        TrackActions.RenameChapters(context.Document);
        return Task.CompletedTask;
    }
}

public sealed class ApplyColorSpaceAction : QueueAction
{
    public int Primaries { get; set; } = 1;

    public int Transfer { get; set; } = 1;

    public int Matrix { get; set; } = 1;

    public override string Description => $"Apply colour space {Primaries}-{Transfer}-{Matrix}";

    public override Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        GroupActions.ApplyColorSpace(context.Document, new ColorInfo(Primaries, Transfer, Matrix));
        return Task.CompletedTask;
    }
}

/// <summary>Imports a chapter text file found next to the source (same base name, .txt).</summary>
public sealed class ImportChaptersFileAction : QueueAction
{
    public override string Description => "Load chapters from a .txt file next to the source";

    public override async Task ApplyAsync(QueueContext context, CancellationToken cancellationToken)
    {
        var source = context.Item.SourcePath;
        var candidate = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source) + ".txt");
        if (!File.Exists(candidate))
            return;
        var chapters = ChapterTextFormat.Parse(await File.ReadAllTextAsync(candidate, cancellationToken).ConfigureAwait(false));
        if (chapters.Count > 0)
        {
            TrackActions.ReplaceChapters(context.Document, chapters);
            context.Log($"Loaded {chapters.Count} chapters from {Path.GetFileName(candidate)}.");
        }
    }
}
