using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugins.PlateSolvePlus;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusSequenceItems {

    [ExportMetadata("Name", "PlateSolvePlus: Secondary Autofocus")]
    [ExportMetadata("Description", "Runs the PlateSolvePlus secondary autofocus routine from the N.I.N.A. sequencer.")]
    [ExportMetadata("Icon", "Plugin_Test_SVG")]
    [ExportMetadata("Category", "PlateSolvePlus")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlatesolveplusSecondaryAutofocusInstruction : SequenceItem {

        private static readonly bool templatesLoaded = false;
        private static readonly object templateLock = new();

        static PlatesolveplusSecondaryAutofocusInstruction() {
            lock (templateLock) {
                if (templatesLoaded) return;

                try {
                    var itemTemplate = new ResourceDictionary {
                        Source = new Uri(
                            "pack://application:,,,/PlateSolvePlus;component/PlatesolveplusSequenceItems/Templates/PlatesolveplusSecondaryAutofocusInstructionTemplate.xaml",
                            UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(itemTemplate);

                    templatesLoaded = true;
                    Logger.Debug("PlatesolveplusSecondaryAutofocusInstruction template loaded successfully");
                } catch (Exception ex) {
                    Logger.Error($"Failed to load PlatesolveplusSecondaryAutofocusInstruction template: {ex}");
                }
            }
        }

        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;

        [ImportingConstructor]
        public PlatesolveplusSecondaryAutofocusInstruction(PlatesolveplusDockables.CameraDockable dockable) {
            _dockable = dockable ?? throw new ArgumentNullException(nameof(dockable));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            UseStoredSettings = true;
            WaitTimeoutSeconds = 300;

            ExposureSeconds = 5.0;
            Gain = 0;
            BinX = 1;
            BinY = 1;
            StepSize = 40;
            StepsOut = 4;
            StepsIn = 4;
            SettleTimeMs = 400;
            BacklashSteps = 0;
            BacklashMode = BacklashMode.OvershootReturn;
            AutofocusTimeoutSeconds = 180;
        }

        private PlatesolveplusSecondaryAutofocusInstruction(PlatesolveplusSecondaryAutofocusInstruction copyMe) : this(copyMe._dockable) {
            CopyMetaData(copyMe);
            UseStoredSettings = copyMe.UseStoredSettings;
            WaitTimeoutSeconds = copyMe.WaitTimeoutSeconds;
            ExposureSeconds = copyMe.ExposureSeconds;
            Gain = copyMe.Gain;
            BinX = copyMe.BinX;
            BinY = copyMe.BinY;
            StepSize = copyMe.StepSize;
            StepsOut = copyMe.StepsOut;
            StepsIn = copyMe.StepsIn;
            SettleTimeMs = copyMe.SettleTimeMs;
            BacklashSteps = copyMe.BacklashSteps;
            BacklashMode = copyMe.BacklashMode;
            AutofocusTimeoutSeconds = copyMe.AutofocusTimeoutSeconds;
        }

        private bool useStoredSettings;
        [JsonProperty]
        public bool UseStoredSettings {
            get => useStoredSettings;
            set {
                useStoredSettings = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsCustomSettingsEnabled));
            }
        }

        public bool IsCustomSettingsEnabled => !UseStoredSettings;

        private int waitTimeoutSeconds;
        [JsonProperty]
        public int WaitTimeoutSeconds {
            get => waitTimeoutSeconds;
            set { waitTimeoutSeconds = value; RaisePropertyChanged(); }
        }

        private double exposureSeconds;
        [JsonProperty]
        public double ExposureSeconds {
            get => exposureSeconds;
            set { exposureSeconds = value; RaisePropertyChanged(); }
        }

        private int gain;
        [JsonProperty]
        public int Gain {
            get => gain;
            set { gain = value; RaisePropertyChanged(); }
        }

        private int binX;
        [JsonProperty]
        public int BinX {
            get => binX;
            set { binX = value; RaisePropertyChanged(); }
        }

        private int binY;
        [JsonProperty]
        public int BinY {
            get => binY;
            set { binY = value; RaisePropertyChanged(); }
        }

        private int stepSize;
        [JsonProperty]
        public int StepSize {
            get => stepSize;
            set { stepSize = value; RaisePropertyChanged(); }
        }

        private int stepsOut;
        [JsonProperty]
        public int StepsOut {
            get => stepsOut;
            set { stepsOut = value; RaisePropertyChanged(); }
        }

        private int stepsIn;
        [JsonProperty]
        public int StepsIn {
            get => stepsIn;
            set { stepsIn = value; RaisePropertyChanged(); }
        }

        private int settleTimeMs;
        [JsonProperty]
        public int SettleTimeMs {
            get => settleTimeMs;
            set { settleTimeMs = value; RaisePropertyChanged(); }
        }

        private int backlashSteps;
        [JsonProperty]
        public int BacklashSteps {
            get => backlashSteps;
            set { backlashSteps = value; RaisePropertyChanged(); }
        }

        private BacklashMode backlashMode;
        [JsonProperty]
        public BacklashMode BacklashMode {
            get => backlashMode;
            set { backlashMode = value; RaisePropertyChanged(); }
        }

        private int autofocusTimeoutSeconds;
        [JsonProperty]
        public int AutofocusTimeoutSeconds {
            get => autofocusTimeoutSeconds;
            set { autofocusTimeoutSeconds = value; RaisePropertyChanged(); }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            var settingsOverride = UseStoredSettings ? null : BuildSettingsOverride();
            var waitBudgetSeconds = GetWaitBudgetSeconds();

            var start = await RunOnUiAsync(() => _dockable.ApiStartSecondaryAutofocusAsync(settingsOverride));
            if (string.Equals(start, "busy", StringComparison.OrdinalIgnoreCase)) {
                progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus busy - waiting for autofocus slot..." });
                await WaitUntilNotBusy(token, waitBudgetSeconds, 250, progress);
                start = await RunOnUiAsync(() => _dockable.ApiStartSecondaryAutofocusAsync(settingsOverride));
            }

            if (string.Equals(start, "failed", StringComparison.OrdinalIgnoreCase)) {
                var snapshot = await GetAutofocusStatusSnapshotAsync();
                throw new SequenceEntityFailedException(snapshot.ErrorMessage ?? snapshot.StatusText ?? "Secondary autofocus could not be started.");
            }

            if (!string.Equals(start, "started", StringComparison.OrdinalIgnoreCase)) {
                throw new SequenceEntityFailedException($"Secondary autofocus could not be started (result={start}).");
            }

            progress?.Report(new ApplicationStatus { Status = "PlateSolvePlus secondary autofocus running..." });

            var result = await WaitUntilAutofocusFinished(token, waitBudgetSeconds, 250, progress);
            if (string.Equals(result.Phase, nameof(SecondaryAfPhase.Completed), StringComparison.OrdinalIgnoreCase)) {
                var bestPosition = result.BestPosition.HasValue ? $", best position {result.BestPosition.Value}" : string.Empty;
                progress?.Report(new ApplicationStatus { Status = $"PlateSolvePlus secondary autofocus completed{bestPosition}." });
                return;
            }

            if (string.Equals(result.Phase, nameof(SecondaryAfPhase.Cancelled), StringComparison.OrdinalIgnoreCase)) {
                throw new SequenceEntityFailedException(result.ErrorMessage ?? "Secondary autofocus was cancelled.");
            }

            throw new SequenceEntityFailedException(result.ErrorMessage ?? result.StatusText ?? "Secondary autofocus failed.");
        }

        public override object Clone() => new PlatesolveplusSecondaryAutofocusInstruction(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(PlatesolveplusSecondaryAutofocusInstruction)}, UseStored={UseStoredSettings}, Wait={WaitTimeoutSeconds}s";

        private SecondaryAutofocusSettings BuildSettingsOverride() {
            return new SecondaryAutofocusSettings {
                ExposureSeconds = Math.Max(0.1, ExposureSeconds),
                Gain = Gain,
                BinX = Math.Max(1, BinX),
                BinY = Math.Max(1, BinY),
                StepSize = Math.Max(1, StepSize),
                StepsOut = Math.Max(1, StepsOut),
                StepsIn = Math.Max(1, StepsIn),
                SettleTimeMs = Math.Max(0, SettleTimeMs),
                BacklashSteps = Math.Max(0, BacklashSteps),
                BacklashMode = BacklashMode,
                TimeoutSeconds = Math.Max(10, AutofocusTimeoutSeconds)
            };
        }

        private int GetWaitBudgetSeconds() {
            var autofocusTimeout = UseStoredSettings
                ? (_dockable.PluginSettings?.Settings?.AfTimeoutSeconds ?? AutofocusTimeoutSeconds)
                : AutofocusTimeoutSeconds;

            return Math.Max(Math.Max(30, WaitTimeoutSeconds), autofocusTimeout + 15);
        }

        private async Task<AutofocusStatusSnapshot> GetAutofocusStatusSnapshotAsync() {
            var statusObj = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
            return ReadAutofocusStatusSnapshot(statusObj);
        }

        private async Task WaitUntilNotBusy(CancellationToken token, int timeoutSec, int pollMs, IProgress<ApplicationStatus>? progress) {
            var start = DateTime.UtcNow;

            while (true) {
                token.ThrowIfCancellationRequested();

                var statusObj = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
                var busy = TryReadBool(statusObj, "busy") ?? false;
                if (!busy) {
                    return;
                }

                if ((DateTime.UtcNow - start).TotalSeconds >= timeoutSec) {
                    throw new TimeoutException($"PlateSolvePlus remained busy for more than {timeoutSec}s.");
                }

                progress?.Report(new ApplicationStatus { Status = "Waiting for PlateSolvePlus to become idle..." });
                await Task.Delay(Math.Max(100, pollMs), token);
            }
        }

        private async Task<AutofocusStatusSnapshot> WaitUntilAutofocusFinished(CancellationToken token, int timeoutSec, int pollMs, IProgress<ApplicationStatus>? progress) {
            var start = DateTime.UtcNow;

            while (true) {
                token.ThrowIfCancellationRequested();

                var statusObj = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
                var snapshot = ReadAutofocusStatusSnapshot(statusObj);

                if (snapshot.IsTerminal) {
                    return snapshot;
                }

                if (!snapshot.Busy && string.IsNullOrWhiteSpace(snapshot.Phase)) {
                    return snapshot;
                }

                if ((DateTime.UtcNow - start).TotalSeconds >= timeoutSec) {
                    throw new TimeoutException($"Secondary autofocus did not finish within {timeoutSec}s.");
                }

                progress?.Report(new ApplicationStatus {
                    Status = string.IsNullOrWhiteSpace(snapshot.RunStatus)
                        ? "PlateSolvePlus secondary autofocus running..."
                        : $"Secondary autofocus: {snapshot.RunStatus}"
                });

                await Task.Delay(Math.Max(100, pollMs), token);
            }
        }

        private AutofocusStatusSnapshot ReadAutofocusStatusSnapshot(object statusObj) {
            var snapshot = new AutofocusStatusSnapshot {
                Busy = TryReadBool(statusObj, "busy") ?? false,
                StatusText = TryReadString(statusObj, "statusText")
            };

            var autofocusObj = TryGet(statusObj, "secondaryAutofocus");
            if (autofocusObj == null) {
                snapshot.ErrorMessage = snapshot.StatusText;
                return snapshot;
            }

            snapshot.Phase = TryReadString(autofocusObj, "Phase");
            snapshot.RunStatus = TryReadString(autofocusObj, "Status");
            snapshot.ErrorMessage = TryReadString(autofocusObj, "LastError") ?? snapshot.StatusText;
            snapshot.BestPosition = TryReadInt(autofocusObj, "BestPosition");
            snapshot.BestHfr = TryReadDouble(autofocusObj, "BestHfr");
            return snapshot;
        }

        private Task<T> RunOnUiAsync<T>(Func<T> func) {
            if (_dispatcher.CheckAccess()) return Task.FromResult(func());
            return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
        }

        private static object? TryGet(object? obj, string propertyName) {
            if (obj == null) return null;

            try {
                var property = obj.GetType().GetProperty(propertyName);
                return property?.GetValue(obj);
            } catch {
                return null;
            }
        }

        private static bool? TryReadBool(object? obj, string propertyName) {
            try {
                var value = TryGet(obj, propertyName);
                return value is bool boolean ? boolean : null;
            } catch {
                return null;
            }
        }

        private static int? TryReadInt(object? obj, string propertyName) {
            try {
                var value = TryGet(obj, propertyName);
                if (value == null) return null;
                return Convert.ToInt32(value);
            } catch {
                return null;
            }
        }

        private static double? TryReadDouble(object? obj, string propertyName) {
            try {
                var value = TryGet(obj, propertyName);
                if (value == null) return null;
                return Convert.ToDouble(value);
            } catch {
                return null;
            }
        }

        private static string? TryReadString(object? obj, string propertyName) {
            try {
                return TryGet(obj, propertyName)?.ToString();
            } catch {
                return null;
            }
        }

        private sealed class AutofocusStatusSnapshot {
            public bool Busy { get; set; }
            public string? Phase { get; set; }
            public string? RunStatus { get; set; }
            public string? StatusText { get; set; }
            public string? ErrorMessage { get; set; }
            public int? BestPosition { get; set; }
            public double? BestHfr { get; set; }

            public bool IsTerminal =>
                string.Equals(Phase, nameof(SecondaryAfPhase.Completed), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Phase, nameof(SecondaryAfPhase.Failed), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Phase, nameof(SecondaryAfPhase.Cancelled), StringComparison.OrdinalIgnoreCase);
        }
    }
}
