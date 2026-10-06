using CommunityToolkit.Mvvm.ComponentModel;

namespace MMW.Core.Undo;

/// <summary>A reversible edit.</summary>
public interface IEditCommand
{
    string Description { get; }

    void Do();

    void Undo();
}

/// <summary>Edit built from a pair of delegates.</summary>
public sealed class DelegateEdit(string description, Action redo, Action undo) : IEditCommand
{
    public string Description { get; } = description;

    public void Do() => redo();

    public void Undo() => undo();
}

/// <summary>Several edits applied and reverted as one step.</summary>
public sealed class CompositeEdit(string description, IReadOnlyList<IEditCommand> edits) : IEditCommand
{
    public string Description { get; } = description;

    public void Do()
    {
        foreach (var e in edits)
            e.Do();
    }

    public void Undo()
    {
        for (var i = edits.Count - 1; i >= 0; i--)
            edits[i].Undo();
    }
}

/// <summary>Undo/redo history for one document.</summary>
public sealed partial class UndoStack : ObservableObject
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();
    private List<IEditCommand>? _group;
    private string _groupDescription = string.Empty;
    private int _groupDepth;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.TryPeek(out var c) ? c.Description : null;

    public string? RedoDescription => _redo.TryPeek(out var c) ? c.Description : null;

    /// <summary>Raised after any do/undo/redo, so documents can mark themselves dirty.</summary>
    public event EventHandler? Changed;

    /// <summary>True while an undo or redo is being applied (edits must not be recorded then).</summary>
    public bool IsReplaying { get; private set; }

    /// <summary>Executes <paramref name="command"/> and records it.</summary>
    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        Record(command);
    }

    /// <summary>Records an edit that has already been applied.</summary>
    public void Record(IEditCommand command)
    {
        if (IsReplaying)
            return;
        if (_group is not null)
        {
            _group.Add(command);
            return;
        }

        _undo.Push(command);
        _redo.Clear();
        Notify();
    }

    /// <summary>Records a property change from <paramref name="oldValue"/> to <paramref name="newValue"/>.</summary>
    public void RecordChange<T>(string description, Action<T> setter, T oldValue, T newValue)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
            return;
        Record(new DelegateEdit(description, () => setter(newValue), () => setter(oldValue)));
    }

    /// <summary>Groups every edit recorded until the returned scope is disposed into one undo step.</summary>
    public IDisposable Transaction(string description)
    {
        if (_groupDepth++ == 0)
        {
            _group = [];
            _groupDescription = description;
        }

        return new TransactionScope(this);
    }

    private void EndTransaction()
    {
        if (--_groupDepth > 0)
            return;
        var edits = _group!;
        _group = null;
        if (edits.Count == 0)
            return;
        Record(edits.Count == 1 ? edits[0] : new CompositeEdit(_groupDescription, edits));
    }

    private sealed class TransactionScope(UndoStack stack) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            stack.EndTransaction();
        }
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var c))
            return;
        Replay(c.Undo);
        _redo.Push(c);
        Notify();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var c))
            return;
        Replay(c.Do);
        _undo.Push(c);
        Notify();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Notify();
    }

    private void Replay(Action action)
    {
        IsReplaying = true;
        try
        {
            action();
        }
        finally
        {
            IsReplaying = false;
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
