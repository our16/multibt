namespace MultiBT.Core.Config;

/// <summary>
/// A bounded undo/redo history of settings snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots are the settings document's serialised JSON — the same form that is written to disk. Using
/// the persisted representation rather than object clones means undo restores EXACTLY what a restart
/// would, so there is no second definition of "the settings" that can drift from the first.
/// </para>
/// <para>
/// A cursor into a list, rather than two stacks: undo/redo is then just moving the cursor, and the
/// classic bug where the redo stack is not cleared on a new edit disappears, because a new snapshot
/// simply truncates everything after the cursor.
/// </para>
/// <para>
/// Changes are coalesced upstream (the view model commits on a debounce), so one slider drag produces
/// one history entry rather than one per pixel — otherwise Ctrl+Z would step back through hundreds of
/// intermediate values instead of returning to where the user started.
/// </para>
/// </remarks>
public sealed class SettingsHistory
{
    /// <summary>Maximum retained snapshots. Bounded so a long session cannot grow without limit.</summary>
    public const int MaxDepth = 100;

    private readonly List<string> _snapshots = [];
    private int _cursor = -1;

    /// <summary>Number of retained snapshots.</summary>
    public int Depth => _snapshots.Count;

    /// <summary>Whether an undo is available.</summary>
    public bool CanUndo => _cursor > 0;

    /// <summary>Whether a redo is available.</summary>
    public bool CanRedo => _cursor >= 0 && _cursor < _snapshots.Count - 1;

    /// <summary>Index of the snapshot currently in effect, or -1 when empty.</summary>
    public int Cursor => _cursor;

    /// <summary>Records the starting state without making it undoable.</summary>
    public void Seed(string snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshot);

        _snapshots.Clear();
        _snapshots.Add(snapshot);
        _cursor = 0;
    }

    /// <summary>
    /// Records a new state.
    /// </summary>
    /// <returns><c>true</c> when the state was different from the current one and was recorded.</returns>
    public bool Commit(string snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshot);

        if (_cursor >= 0 && _snapshots[_cursor] == snapshot)
        {
            // Nothing changed. Recording it would make Ctrl+Z appear to do nothing.
            return false;
        }

        // A new edit invalidates everything that was ahead of the cursor.
        if (_cursor < _snapshots.Count - 1)
        {
            _snapshots.RemoveRange(_cursor + 1, _snapshots.Count - _cursor - 1);
        }

        _snapshots.Add(snapshot);
        _cursor = _snapshots.Count - 1;

        if (_snapshots.Count > MaxDepth)
        {
            int excess = _snapshots.Count - MaxDepth;
            _snapshots.RemoveRange(0, excess);
            _cursor -= excess;
        }

        return true;
    }

    /// <summary>Steps back one snapshot.</summary>
    public bool TryUndo(out string? snapshot)
    {
        if (!CanUndo)
        {
            snapshot = null;
            return false;
        }

        _cursor--;
        snapshot = _snapshots[_cursor];
        return true;
    }

    /// <summary>Steps forward one snapshot.</summary>
    public bool TryRedo(out string? snapshot)
    {
        if (!CanRedo)
        {
            snapshot = null;
            return false;
        }

        _cursor++;
        snapshot = _snapshots[_cursor];
        return true;
    }

    /// <summary>Clears the history and seeds it with the given state.</summary>
    public void Reset(string snapshot) => Seed(snapshot);
}
