using System.Windows.Input;

namespace SCFA.ContentCenter.Commands;

public sealed class AsyncItemCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null) : ICommand where T : class
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && parameter is T item && (canExecute?.Invoke(item) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || parameter is not T item) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await execute(item); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
