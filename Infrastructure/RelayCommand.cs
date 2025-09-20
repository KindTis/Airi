using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Airi.Infrastructure
{
    /// <summary>
    /// Simplified command implementation used for Stage 0 interactions.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private readonly Dispatcher? _dispatcher;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _dispatcher = Application.Current?.Dispatcher;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged()
        {
            if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
            }
            else
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
