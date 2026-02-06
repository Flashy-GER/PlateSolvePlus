using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus.Utils {
    /// <summary>
    /// Async ICommand wrapper that never blocks the UI thread.
    /// Supports both Func&lt;Task&gt; and Func&lt;object?, Task&gt; delegates.
    /// </summary>
    public sealed class SimpleAsyncCommand : ICommand {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged;

        // ✅ Constructor for methods like: Task FooAsync()
        public SimpleAsyncCommand(Func<Task> executeAsync, Func<bool>? canExecute = null) {
            if (executeAsync == null) throw new ArgumentNullException(nameof(executeAsync));

            _executeAsync = _ => executeAsync();
            if (canExecute != null)
                _canExecute = _ => canExecute();
        }

        // ✅ Constructor for methods like: Task FooAsync(object? p)
        public SimpleAsyncCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null) {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) {
            if (_isExecuting) return false;
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter) {
            // Never block UI thread here
            _ = ExecuteInternalAsync(parameter);
        }

        private async Task ExecuteInternalAsync(object? parameter) {
            if (!CanExecute(parameter)) return;

            try {
                _isExecuting = true;
                RaiseCanExecuteChanged();

                // Keep the synchronization context (WPF) so UI-bound code continues to work
                await _executeAsync(parameter).ConfigureAwait(true);
            } catch (Exception ex) {
                // Avoid throwing on UI thread; write to debug output at least.
                System.Diagnostics.Debug.WriteLine(ex);
            } finally {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
