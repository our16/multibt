using MultiBT.Core.Config;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The undo/redo history behind Ctrl+Z / Ctrl+Y.
/// </summary>
/// <remarks>
/// Pure logic, so it is tested directly rather than through the UI. The cases that matter are the ones
/// that make undo feel broken to a user: a no-op change creating an entry, a new edit leaving a stale
/// redo available, and the undo stack growing without limit.
/// </remarks>
public sealed class SettingsHistoryTests
{
    [Fact]
    public void SeededHistoryHasNothingToUndoOrRedo()
    {
        var history = new SettingsHistory();
        history.Seed("a");

        Assert.Equal(1, history.Depth);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.TryUndo(out _));
        Assert.False(history.TryRedo(out _));
    }

    [Fact]
    public void CommittingAChangeMakesItUndoable()
    {
        var history = new SettingsHistory();
        history.Seed("a");

        Assert.True(history.Commit("b"));

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        Assert.True(history.TryUndo(out string? previous));
        Assert.Equal("a", previous);
        Assert.False(history.CanUndo);

        Assert.True(history.CanRedo);
        Assert.True(history.TryRedo(out string? forward));
        Assert.Equal("b", forward);
    }

    [Fact]
    public void CommittingTheSameStateRecordsNothing()
    {
        // Auto-save runs on a debounce and can fire when nothing actually changed. Recording that would
        // make the first Ctrl+Z appear to do nothing at all.
        var history = new SettingsHistory();
        history.Seed("a");
        history.Commit("b");

        Assert.False(history.Commit("b"));
        Assert.Equal(2, history.Depth);
    }

    [Fact]
    public void EditingAfterUndoDiscardsTheRedoTail()
    {
        var history = new SettingsHistory();
        history.Seed("a");
        history.Commit("b");
        history.Commit("c");

        Assert.True(history.TryUndo(out _));   // back to "b"
        Assert.True(history.CanRedo);

        Assert.True(history.Commit("d"));      // a new edit from "b"

        Assert.False(history.CanRedo);
        Assert.Equal(3, history.Depth);

        Assert.True(history.TryUndo(out string? previous));
        Assert.Equal("b", previous);
    }

    [Fact]
    public void HistoryIsBoundedAndDropsTheOldestEntries()
    {
        var history = new SettingsHistory();
        history.Seed("s0");

        for (int i = 1; i <= SettingsHistory.MaxDepth + 20; i++)
        {
            history.Commit($"s{i}");
        }

        Assert.Equal(SettingsHistory.MaxDepth, history.Depth);

        // The cursor must still sit at the newest entry, and stepping all the way back must stay inside
        // the retained window rather than running off the front.
        int steps = 0;
        while (history.TryUndo(out _))
        {
            steps++;
        }

        Assert.Equal(SettingsHistory.MaxDepth - 1, steps);
    }

    [Fact]
    public void UndoRedoWalksBackAndForthThroughManyStates()
    {
        var history = new SettingsHistory();
        history.Seed("s0");

        for (int i = 1; i <= 5; i++)
        {
            history.Commit($"s{i}");
        }

        for (int i = 4; i >= 0; i--)
        {
            Assert.True(history.TryUndo(out string? value));
            Assert.Equal($"s{i}", value);
        }

        for (int i = 1; i <= 5; i++)
        {
            Assert.True(history.TryRedo(out string? value));
            Assert.Equal($"s{i}", value);
        }

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ResetDiscardsHistoryAndReseeds()
    {
        var history = new SettingsHistory();
        history.Seed("a");
        history.Commit("b");
        history.Commit("c");

        history.Reset("fresh");

        Assert.Equal(1, history.Depth);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void EmptySnapshotsAreRejected()
    {
        var history = new SettingsHistory();

        Assert.Throws<ArgumentException>(() => history.Seed(string.Empty));
        Assert.Throws<ArgumentException>(() => history.Commit(string.Empty));
    }
}
