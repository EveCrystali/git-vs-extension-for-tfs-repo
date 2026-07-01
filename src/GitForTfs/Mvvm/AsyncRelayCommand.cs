using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GitForTfs.Mvvm
{
    /// <summary>
    /// An <see cref="ICommand"/> that runs an asynchronous handler and prevents re-entrancy
    /// while the handler is still running.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<object, bool> _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) =>
            !_isRunning && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
                return;

            _isRunning = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute(parameter);
            }
            finally
            {
                _isRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
