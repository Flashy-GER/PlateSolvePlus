using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusSequenceItems {

    [ExportMetadata("Name", "PlateSolvePlus: Plate Solve (Secondary)")]
    [ExportMetadata("Description", "Captures an image from the secondary camera and performs a plate solve using PlateSolvePlus.")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "PlateSolvePlus")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlatesolveplusSecondarySolveInstruction : SequenceItem {

        private static bool templatesLoaded = false;
        private static readonly object templateLock = new object();

        static PlatesolveplusSecondarySolveInstruction() {
            lock (templateLock) {
                if (templatesLoaded) return;

                try {
                    var itemTemplate = new ResourceDictionary {
                        Source = new Uri(
                            "pack://application:,,,/PlateSolvePlus;component/PlatesolveplusSequenceItems/Templates/PlatesolveplusSecondarySolveInstructionTemplate.xaml",
                            UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(itemTemplate);

                    templatesLoaded = true;
                    Logger.Debug("PlatesolveplusSecondarySolveInstruction template loaded successfully");
                } catch (Exception ex) {
                    Logger.Error($"Failed to load PlatesolveplusSecondarySolveInstruction template: {ex}");
                }
            }
        }

        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;

        [ImportingConstructor]
        public PlatesolveplusSecondarySolveInstruction(PlatesolveplusDockables.CameraDockable dockable) {
            _dockable = dockable ?? throw new ArgumentNullException(nameof(dockable));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            // sane defaults
            ExposureSeconds = 2.0;
            Gain = -1;
            Binning = 1;
            Downsample = 2;
            SearchRadiusDeg = 5.0;
            TimeoutSeconds = 60;
        }

        private PlatesolveplusSecondarySolveInstruction(PlatesolveplusSecondarySolveInstruction copyMe) : this(copyMe._dockable) {
            CopyMetaData(copyMe);
            ExposureSeconds = copyMe.ExposureSeconds;
            Gain = copyMe.Gain;
            Binning = copyMe.Binning;
            Downsample = copyMe.Downsample;
            SearchRadiusDeg = copyMe.SearchRadiusDeg;
            TimeoutSeconds = copyMe.TimeoutSeconds;
        }

        private double exposureSeconds;
        [JsonProperty]
        public double ExposureSeconds { get => exposureSeconds; set { exposureSeconds = value; RaisePropertyChanged(); } }

        private int gain;
        [JsonProperty]
        public int Gain { get => gain; set { gain = value; RaisePropertyChanged(); } }

        private int binning;
        [JsonProperty]
        public int Binning { get => binning; set { binning = value; RaisePropertyChanged(); } }

        private int downsample;
        [JsonProperty]
        public int Downsample { get => downsample; set { downsample = value; RaisePropertyChanged(); } }

        private double searchRadiusDeg;
        [JsonProperty]
        public double SearchRadiusDeg { get => searchRadiusDeg; set { searchRadiusDeg = value; RaisePropertyChanged(); } }

        private int timeoutSeconds;
        [JsonProperty]
        public int TimeoutSeconds { get => timeoutSeconds; set { timeoutSeconds = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            // 1) Apply settings on UI thread
            await RunOnUiAsync(() => {
                // secondary camera capture settings
                TrySet(_dockable, "GuideExposureSeconds", ExposureSeconds);
                TrySet(_dockable, "GuideBinning", Binning);
                TrySet(_dockable, "GuideGain", Gain);

                // plugin solve settings (solver itself is taken from NINA/Plugin configuration)
                var settingsObj =
                    TryGet(_dockable, "PluginSettings") is object ps
                        ? TryGet(ps, "Settings")
                        : null;

                if (settingsObj != null) {
                    TrySet(settingsObj, "SearchRadius", SearchRadiusDeg);
                    TrySet(settingsObj, "SearchRadiusDeg", SearchRadiusDeg);
                    TrySet(settingsObj, "Downsample", Downsample);
                    TrySet(settingsObj, "Timeout", TimeoutSeconds);
                    TrySet(settingsObj, "TimeoutSeconds", TimeoutSeconds);
                }

                return true;
            });

            // 2) Start solve (dockable starts background work)
            var start = await RunOnUiAsync(() => _dockable.ApiCaptureAndSolveAsync());

            if (string.Equals(start, "busy", StringComparison.OrdinalIgnoreCase)) {
                progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus busy – waiting…" });
                await WaitUntilNotBusy(token, TimeoutSeconds, 250);
                start = await RunOnUiAsync(() => _dockable.ApiCaptureAndSolveAsync());
            }

            if (!string.Equals(start, "started", StringComparison.OrdinalIgnoreCase)) {
                throw new SequenceEntityFailedException($"PlateSolvePlus secondary solve could not start (result={start}).");
            }

            progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus solving (secondary)…" });

            // 3) Wait until finished
            await WaitUntilNotBusy(token, TimeoutSeconds, 250);

            progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus secondary solve finished." });
        }

        public override object Clone() => new PlatesolveplusSecondarySolveInstruction(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(PlatesolveplusSecondarySolveInstruction)}, Exp={ExposureSeconds:0.##}s Bin={Binning} Gain={Gain} DS={Downsample} R={SearchRadiusDeg:0.##}°";

        private Task<T> RunOnUiAsync<T>(Func<T> func) {
            if (_dispatcher.CheckAccess()) return Task.FromResult(func());
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
        }

        private async Task WaitUntilNotBusy(CancellationToken token, int timeoutSec, int pollMs) {
            var start = DateTime.UtcNow;

            while (true) {
                token.ThrowIfCancellationRequested();

                var statusObj = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
                bool importsReady = TryReadBool(statusObj, "importsReady") ?? false;
                bool busy = TryReadBool(statusObj, "busy") ?? false;

                if (importsReady && !busy) return;

                if ((DateTime.UtcNow - start).TotalSeconds >= timeoutSec) {
                    throw new TimeoutException($"PlateSolvePlus did not finish within {timeoutSec}s (busy={busy}, importsReady={importsReady}).");
                }

                await Task.Delay(Math.Max(100, pollMs), token);
            }
        }

        private static bool? TryReadBool(object obj, string propName) {
            try {
                var p = obj.GetType().GetProperty(propName);
                if (p == null) return null;
                var v = p.GetValue(obj);
                return v is bool b ? b : null;
            } catch { return null; }
        }

        private static object? TryGet(object obj, string propName) {
            try {
                var p = obj.GetType().GetProperty(propName);
                return p?.GetValue(obj);
            } catch { return null; }
        }

        private static bool TrySet(object obj, string propName, object value) {
            try {
                var p = obj.GetType().GetProperty(propName);
                if (p == null || !p.CanWrite) return false;
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var v = Convert.ChangeType(value, t);
                p.SetValue(obj, v);
                return true;
            } catch { return false; }
        }
    }
}
