using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus.Utils {

    public sealed class SimpleAsyncCommand : ICommand {
        private readonly Func<Task> execute;
        private bool isExecuting;

        public SimpleAsyncCommand(Func<Task> execute) => this.execute = execute ?? throw new ArgumentNullException(nameof(execute));

        public bool CanExecute(object parameter) => !isExecuting;
        public event EventHandler? CanExecuteChanged;

        public async void Execute(object parameter) {
            if (isExecuting) return;

            try {
                isExecuting = true;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                await execute();
            } finally {
                isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
