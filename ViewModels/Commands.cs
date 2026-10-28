using System;
using System.Threading.Tasks;
using System.Windows.Input;
namespace XrayMetro.ViewModels;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<object?, Task> execute, Action<Exception> onError, Predicate<object?>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Notify();
        try { await execute(parameter); }
        catch (Exception exception) { onError(exception); }
        finally { _running = false; Notify(); }
    }
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
