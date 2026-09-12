using System.Windows.Input;

namespace MultiBT.App;

/// <summary>
/// Minimal always-executable <see cref="ICommand"/> for menu items and key bindings.
/// </summary>
/// <remarks>
/// <see cref="CanExecuteChanged"/> is implemented with real (if empty) accessors rather than a bare
/// field: a field-like event on a command that never raises it produces a "never used" warning, and
/// that warning is a genuine signal that the command never re-evaluates. These commands are always
/// executable, so there is nothing to re-evaluate.
/// </remarks>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }
}
