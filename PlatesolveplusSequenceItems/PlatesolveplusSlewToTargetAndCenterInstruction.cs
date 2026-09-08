using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusSequenceItems {

    [ExportMetadata("Name", "PlateSolvePlus: Slew to Target & Center")]
    [ExportMetadata("Description", "Slews to the sequence target coordinates and centers using PlateSolvePlus.")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "PlateSolvePlus")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlatesolveplusSlewToTargetAndCenterInstruction : SequenceItem {

        private static readonly bool templatesLoaded = false;
        private static readonly object templateLock = new object();

        static PlatesolveplusSlewToTargetAndCenterInstruction() {
            lock (templateLock) {
                if (templatesLoaded) return;

                try {
                    var itemTemplate = new ResourceDictionary {
                        Source = new Uri(
                            "pack://application:,,,/PlateSolvePlus;component/PlatesolveplusSequenceItems/Templates/PlatesolveplusSlewToTargetAndCenterInstructionTemplate.xaml",
                            UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(itemTemplate);

                    templatesLoaded = true;
                    Logger.Debug("PlatesolveplusSlewToTargetAndCenterInstruction template loaded successfully");
                } catch (Exception ex) {
                    Logger.Error($"Failed to load PlatesolveplusSlewToTargetAndCenterInstruction template: {ex}");
                }
            }
        }

        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;

        [ImportingConstructor]
        public PlatesolveplusSlewToTargetAndCenterInstruction(PlatesolveplusDockables.CameraDockable dockable) {
            _dockable = dockable ?? throw new ArgumentNullException(nameof(dockable));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            UseSequenceTarget = true;
            TargetRaDeg = 0;
            TargetDecDeg = 0;
            UseSlewInsteadOfSync = true;

            TimeoutSeconds = 120; // Centering kann länger dauern
        }

        private PlatesolveplusSlewToTargetAndCenterInstruction(PlatesolveplusSlewToTargetAndCenterInstruction copyMe) : this(copyMe._dockable) {
            CopyMetaData(copyMe);
            UseSequenceTarget = copyMe.UseSequenceTarget;
            TargetRaDeg = copyMe.TargetRaDeg;
            TargetDecDeg = copyMe.TargetDecDeg;
            UseSlewInsteadOfSync = copyMe.UseSlewInsteadOfSync;
            TimeoutSeconds = copyMe.TimeoutSeconds;
        }

        private bool useSequenceTarget = true;
        [JsonProperty]
        public bool UseSequenceTarget {
            get => useSequenceTarget;
            set {
                useSequenceTarget = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsManualTargetEnabled));
            }
        }

        public bool IsManualTargetEnabled => !UseSequenceTarget;

        private double targetRaDeg;
        [JsonProperty]
        public double TargetRaDeg { get => targetRaDeg; set { targetRaDeg = value; RaisePropertyChanged(); } }

        private double targetDecDeg;
        [JsonProperty]
        public double TargetDecDeg { get => targetDecDeg; set { targetDecDeg = value; RaisePropertyChanged(); } }

        private bool useSlewInsteadOfSync = true;
        [JsonProperty]
        public bool UseSlewInsteadOfSync { get => useSlewInsteadOfSync; set { useSlewInsteadOfSync = value; RaisePropertyChanged(); } }

        private int timeoutSeconds;
        [JsonProperty]
        public int TimeoutSeconds { get => timeoutSeconds; set { timeoutSeconds = value; RaisePropertyChanged(); } }

        public override void AfterParentChanged() {
            // wie NINA: wenn inherited, gleich UI befüllen
            if (UseSequenceTarget) {
                var (raDeg, decDeg) = ResolveTargetDegFallbackToManual();
                TargetRaDeg = raDeg;
                TargetDecDeg = decDeg;
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            var (raDeg, decDeg) = ResolveTargetDegFallbackToManual();
            if (UseSequenceTarget) {
                TargetRaDeg = raDeg;
                TargetDecDeg = decDeg;
            }

            await RunOnUiAsync(() => {
                TrySet(_dockable, "UseSlewInsteadOfSync", UseSlewInsteadOfSync);
                return true;
            });

            // Start (fire-and-forget)
            await RunOnUiAsync(() => _dockable.ApiSlewToTargetAndCenterAsync(raDeg, decDeg));

            progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus slewing/centering…" });

            // Wait until finished
            await WaitUntilNotBusy(token, TimeoutSeconds, 250, progress);

            progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus centering finished." });
        }

        private (double raDeg, double decDeg) ResolveTargetDegFallbackToManual() {
            if (!UseSequenceTarget) return (TargetRaDeg, TargetDecDeg);

            try {
                // offizieller NINA-Weg
                var ctx = ItemUtility.RetrieveContextCoordinates(this.Parent);
                if (ctx?.Coordinates != null) {
                    var c = ctx.Coordinates;

                    // RA ist in NINA i.d.R. Stunden; Dec in Grad.
                    // Heuristik: wenn RA <= 24 -> hours->deg
                    double ra = c.RA;
                    double dec = c.Dec;

                    double raDeg = ra <= 24 ? ra * 15.0 : ra;
                    return (raDeg, dec);
                }
            } catch (Exception ex) {
                Logger.Error($"SlewToTarget: RetrieveContextCoordinates failed: {ex}");
            }

            Logger.Warning("SlewToTarget: No context target; using manual RA/Dec.");
            return (TargetRaDeg, TargetDecDeg);
        }

        public override object Clone() => new PlatesolveplusSlewToTargetAndCenterInstruction(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(PlatesolveplusSlewToTargetAndCenterInstruction)}, UseSequenceTarget={UseSequenceTarget}, RA={TargetRaDeg:0.#####}° Dec={TargetDecDeg:0.#####}° SlewInsteadOfSync={UseSlewInsteadOfSync}";

        private Task<T> RunOnUiAsync<T>(Func<T> func) {
            if (_dispatcher.CheckAccess()) return Task.FromResult(func());
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
        }

        private async Task WaitUntilNotBusy(CancellationToken token, int timeoutSec, int pollMs, IProgress<ApplicationStatus>? progress) {
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
