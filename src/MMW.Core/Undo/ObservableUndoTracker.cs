using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Globalization;
using MMW.Core.Resources;

namespace MMW.Core.Undo;

/// <summary>
/// Records undoable edits automatically for observable model objects (property changes) and observable
/// collections (insert/remove/move). Consecutive changes of the same property are merged into one step.
/// </summary>
public sealed class ObservableUndoTracker
{
    private static readonly TimeSpan s_mergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly UndoStack _stack;
    private readonly Dictionary<(object, string), object?> _pending = [];
    private readonly HashSet<object> _tracked = new(ReferenceEqualityComparer.Instance);
    private PropertyEdit? _last;

    public ObservableUndoTracker(UndoStack stack)
    {
        _stack = stack;
        _stack.Changed += (_, _) =>
        {
            // Any step recorded elsewhere (or undo/redo) ends merging.
            if (_stack.UndoDescription != _last?.Description || !_stack.CanUndo)
                _last = null;
        };
    }

    /// <summary>Starts tracking an object (and any observable collections it exposes).</summary>
    public void Track(INotifyPropertyChanged item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_tracked.Add(item))
            return;
        if (item is INotifyPropertyChanging changing)
            changing.PropertyChanging += OnPropertyChanging;
        item.PropertyChanged += OnPropertyChanged;

        foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length == 0 && typeof(INotifyCollectionChanged).IsAssignableFrom(prop.PropertyType) &&
                prop.GetValue(item) is INotifyCollectionChanged collection and IList)
                TrackCollection(collection, prop.Name);
        }
    }

    /// <summary>Tracks a collection; added items that are observable are tracked too.</summary>
    public void TrackCollection(INotifyCollectionChanged collection, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        description ??= Strings.Undo_Items;
        if (!_tracked.Add(collection))
            return;
        foreach (var item in (IList)collection)
        {
            if (item is INotifyPropertyChanged npc)
                Track(npc);
        }

        collection.CollectionChanged += (s, e) => OnCollectionChanged((IList)s!, e, description);
    }

    private void OnPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (sender is null || e.PropertyName is null || _stack.IsReplaying)
            return;
        var prop = sender.GetType().GetProperty(e.PropertyName);
        if (prop is { CanRead: true, CanWrite: true })
            _pending[(sender, e.PropertyName)] = prop.GetValue(sender);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is null || e.PropertyName is null || !_pending.Remove((sender, e.PropertyName), out var oldValue) || _stack.IsReplaying)
            return;
        var prop = sender.GetType().GetProperty(e.PropertyName)!;
        var newValue = prop.GetValue(sender);
        if (Equals(oldValue, newValue))
            return;

        var now = DateTime.UtcNow;
        if (_last is { } last && ReferenceEquals(last.Target, sender) && last.Property == prop && now - last.Time < s_mergeWindow &&
            _stack.UndoDescription == last.Description)
        {
            last.NewValue = newValue;
            last.Time = now;
            return;
        }

        var edit = new PropertyEdit(sender, prop, oldValue, newValue, now);
        _stack.Record(edit);
        _last = edit;
    }

    private void OnCollectionChanged(IList list, NotifyCollectionChangedEventArgs e, string description)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is INotifyPropertyChanged npc)
                    Track(npc);
            }
        }

        if (_stack.IsReplaying)
            return;
        _last = null;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
            {
                var items = e.NewItems.Cast<object>().ToArray();
                var index = e.NewStartingIndex;
                _stack.Record(new DelegateEdit(string.Format(CultureInfo.CurrentCulture, Strings.Undo_Add, description),
                    () => Insert(list, index, items),
                    () => RemoveAt(list, index, items.Length)));
                break;
            }

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
            {
                var items = e.OldItems.Cast<object>().ToArray();
                var index = e.OldStartingIndex;
                _stack.Record(new DelegateEdit(string.Format(CultureInfo.CurrentCulture, Strings.Undo_Remove, description),
                    () => RemoveAt(list, index, items.Length),
                    () => Insert(list, index, items)));
                break;
            }

            case NotifyCollectionChangedAction.Move when e.OldItems is { Count: 1 }:
            {
                var from = e.OldStartingIndex;
                var to = e.NewStartingIndex;
                _stack.Record(new DelegateEdit(string.Format(CultureInfo.CurrentCulture, Strings.Undo_Move, description),
                    () => Move(list, from, to),
                    () => Move(list, to, from)));
                break;
            }

            case NotifyCollectionChangedAction.Replace when e.OldItems is { Count: 1 } && e.NewItems is { Count: 1 }:
            {
                var index = e.NewStartingIndex;
                var oldItem = e.OldItems[0];
                var newItem = e.NewItems[0];
                _stack.Record(new DelegateEdit(string.Format(CultureInfo.CurrentCulture, Strings.Undo_Change, description),
                    () => list[index] = newItem,
                    () => list[index] = oldItem));
                break;
            }

            case NotifyCollectionChangedAction.Reset:
                // A reset loses the previous content; it cannot be undone.
                _stack.Clear();
                break;
        }
    }

    private static void Insert(IList list, int index, object[] items)
    {
        for (var i = 0; i < items.Length; i++)
            list.Insert(index + i, items[i]);
    }

    private static void RemoveAt(IList list, int index, int count)
    {
        for (var i = 0; i < count; i++)
            list.RemoveAt(index);
    }

    private static void Move(IList list, int from, int to)
    {
        var method = list.GetType().GetMethod("Move", [typeof(int), typeof(int)]);
        if (method is not null)
        {
            method.Invoke(list, [from, to]);
            return;
        }

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    private sealed class PropertyEdit(object target, PropertyInfo property, object? oldValue, object? newValue, DateTime time) : IEditCommand
    {
        public object Target { get; } = target;

        public PropertyInfo Property { get; } = property;

        public object? OldValue { get; } = oldValue;

        public object? NewValue { get; set; } = newValue;

        public DateTime Time { get; set; } = time;

        public string Description { get; } = string.Format(CultureInfo.CurrentCulture, Strings.Undo_Change, Humanize(property.Name));

        public void Do() => Property.SetValue(Target, NewValue);

        public void Undo() => Property.SetValue(Target, OldValue);

        private static string Humanize(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length + 4);
            foreach (var c in name)
            {
                if (char.IsUpper(c) && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(sb.Length == 0 ? c : char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }
    }
}
