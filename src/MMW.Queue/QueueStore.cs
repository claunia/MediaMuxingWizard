using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MMW.Core.Diagnostics;
using MMW.Queue.Resources;

namespace MMW.Queue;

/// <summary>Persists the queue (items and options) as JSON.</summary>
public static class QueueStore
{
    private static readonly List<JsonDerivedType> s_external = [];

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(QueueAction) || info.PolymorphismOptions is null)
                        return;
                    lock (s_external)
                    {
                        foreach (var t in s_external)
                            info.PolymorphismOptions.DerivedTypes.Add(t);
                    }
                },
            },
        },
    };

    /// <summary>Registers an action type from another assembly; call before the first load or save.</summary>
    public static void RegisterAction<T>(string discriminator)
        where T : QueueAction
    {
        lock (s_external)
        {
            if (s_external.All(t => t.DerivedType != typeof(T)))
                s_external.Add(new JsonDerivedType(typeof(T), discriminator));
        }
    }

    private sealed class Snapshot
    {
        public QueueOptions Options { get; set; } = new();

        public List<QueueItem> Items { get; set; } = [];
    }

    public static void Save(QueueRunner runner, string path)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var snapshot = new Snapshot { Options = runner.Options, Items = runner.Items.Where(i => i.Status != QueueItemStatus.Completed).ToList() };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, s_options));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Load(QueueRunner runner, string path)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (!File.Exists(path))
            return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), s_options);
            if (snapshot is null)
                return;
            runner.Options = snapshot.Options;
            runner.Items.Clear();
            foreach (var item in snapshot.Items)
            {
                // Items interrupted while working run again.
                if (item.Status == QueueItemStatus.Working)
                    item.Reset();
                runner.Items.Add(item);
            }
        }
        catch (JsonException ex)
        {
            AppLog.Error(Strings.Log_CouldNotReadQueue, ex);
        }
    }

    /// <summary>Deep-copies actions so each item can be edited independently.</summary>
    public static IEnumerable<QueueAction> CloneActions(IEnumerable<QueueAction> actions)
    {
        var json = JsonSerializer.Serialize(actions.ToList(), s_options);
        return JsonSerializer.Deserialize<List<QueueAction>>(json, s_options) ?? [];
    }
}
