using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.Utils {

    internal static class StaTaskRunner {

        public static Task<T> RunAsync<T>(Func<T> action) {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var tcs = new TaskCompletionSource<T>();

            var thread = new Thread(() => {
                try {
                    var result = action();
                    tcs.SetResult(result);
                } catch (Exception ex) {
                    tcs.SetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }

        public static Task RunAsync(Action action) {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync(() => { action(); return true; });
        }
    }
}
