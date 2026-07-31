using System.Windows.Input;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// A small <see cref="ICommand"/> for the shell's contextual actions. Execution is
/// asynchronous and re-entrancy guarded; <see cref="RaiseCanExecuteChanged"/> lets the
/// owning view model re-evaluate availability when the publisher snapshot changes.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private bool running;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
