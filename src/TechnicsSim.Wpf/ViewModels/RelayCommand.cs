using System.Windows.Input;

namespace TechnicsSim.Wpf.ViewModels;

/// <summary>A minimal ICommand, so the panel needs no MVVM framework dependency.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>An ICommand taking one typed argument.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T typed ? canExecute?.Invoke(typed) ?? true : parameter is null;

    public void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            execute(typed);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
