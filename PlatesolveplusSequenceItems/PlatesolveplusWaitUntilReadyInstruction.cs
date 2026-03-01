using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusSequenceItems {

    [ExportMetadata("Name", "PlateSolvePlus: Wait until Ready (Health OK)")]
    [ExportMetadata("Description", "Waits until PlateSolvePlus is ready (imports ready / not busy).")]
    [ExportMetadata("Icon", "Plugin_Test_SVG")]
    [ExportMetadata("Category", "PlateSolvePlus")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlatesolveplusWaitUntilReadyInstruction : SequenceItem {

        private static bool templatesLoaded = false;
        private static readonly object templateLock = new object();

        static PlatesolveplusWaitUntilReadyInstruction() {
            lock (templateLock) {
                if (templatesLoaded) return;

                try {
                    var itemTemplate = new ResourceDictionary {
                        Source = new Uri(
                            "pack://application:,,,/PlateSolvePlus;component/PlatesolveplusSequenceItems/Templates/PlatesolveplusWaitUntilReadyInstructionTemplate.xaml",
                            UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(itemTemplate);

                    templatesLoaded = true;
                    Logger.Debug("PlatesolveplusWaitUntilReadyInstruction template loaded successfully");
                } catch (Exception ex) {
                    Logger.Error($"Failed to load PlatesolveplusWaitUntilReadyInstruction template: {ex}");
                }
            }
        }

        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;

        [ImportingConstructor]
        public PlatesolveplusWaitUntilReadyInstruction(PlatesolveplusDockables.CameraDockable dockable) {
            _dockable = dockable ?? throw new ArgumentNullException(nameof(dockable));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            TimeoutSeconds = 60;
            PollIntervalMs = 500;
        }

        private PlatesolveplusWaitUntilReadyInstruction(PlatesolveplusWaitUntilReadyInstruction copyMe) : this(copyMe._dockable) {
            CopyMetaData(copyMe);
            TimeoutSeconds = copyMe.TimeoutSeconds;
            PollIntervalMs = copyMe.PollIntervalMs;
        }

        private int timeoutSeconds;
        [JsonProperty]
        public int TimeoutSeconds { get => timeoutSeconds; set { timeoutSeconds = value; RaisePropertyChanged(); } }

        private int pollIntervalMs;
        [JsonProperty]
        public int PollIntervalMs { get => pollIntervalMs; set { pollIntervalMs = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var sw = Stopwatch.StartNew();

            while (true) {
                token.ThrowIfCancellationRequested();

                var statusObj = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
                bool importsReady = TryReadBool(statusObj, "importsReady") ?? false;
                bool busy = TryReadBool(statusObj, "busy") ?? false;

                if (importsReady && !busy) {
                    progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus ready." });
                    return;
                }

                if (sw.Elapsed.TotalSeconds >= TimeoutSeconds) {
                    throw new TimeoutException($"PlateSolvePlus not ready after {TimeoutSeconds}s (importsReady={importsReady}, busy={busy}).");
                }

                progress?.Report(new ApplicationStatus { Status = "Waiting for PlateSolvePlus…" });
                await Task.Delay(Math.Max(100, PollIntervalMs), token);
            }
        }

        public override object Clone() => new PlatesolveplusWaitUntilReadyInstruction(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(PlatesolveplusWaitUntilReadyInstruction)}, Timeout={TimeoutSeconds}s Poll={PollIntervalMs}ms";

        private Task<T> RunOnUiAsync<T>(Func<T> func) {
            if (_dispatcher.CheckAccess()) return Task.FromResult(func());
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
        }

        private static bool? TryReadBool(object obj, string propName) {
            try {
                var p = obj.GetType().GetProperty(propName);
                if (p == null) return null;
                var v = p.GetValue(obj);
                return v is bool b ? b : null;
            } catch { return null; }
        }
    }
}
