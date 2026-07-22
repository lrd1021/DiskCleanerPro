using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DiskCleaner.Helpers
{
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private int _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) =>
            _isExecuting == 0 && (_canExecute?.Invoke() ?? true);

        public async void Execute(object parameter)
        {
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;
            RaiseCanExecuteChanged();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                Logger.Error("AsyncRelayCommand 执行异常", ex);
            }
            finally
            {
                _isExecuting = 0;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T, Task> _execute;
        private readonly Predicate<T> _canExecute;
        private int _isExecuting;

        public AsyncRelayCommand(Func<T, Task> execute, Predicate<T> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) =>
            _isExecuting == 0 && (_canExecute == null || (parameter is T t && _canExecute(t)));

        public async void Execute(object parameter)
        {
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;
            RaiseCanExecuteChanged();
            try
            {
                await _execute((T)parameter);
            }
            catch (Exception ex)
            {
                Logger.Error("AsyncRelayCommand<T> 执行异常", ex);
            }
            finally
            {
                _isExecuting = 0;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
