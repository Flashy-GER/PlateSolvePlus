using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Plugins.PlateSolvePlus.PlateSolving;
using NINA.Plugins.PlateSolvePlus.Services;
using NINA.Plugins.PlateSolvePlus.Services.Api;
using NINA.Plugins.PlateSolvePlus.Utils;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables {

    [Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public sealed class CameraDockable : DockableVM,
        NINA.Equipment.Interfaces.ViewModel.IDockableVM,
        IDisposable,
        IPartImportsSatisfiedNotification {

        private const string FallbackSecondaryCameraProgId = "ASCOM.Simulator.Camera";

        private readonly IProfileService profileService;

        [Import]
        private Lazy<IPlateSolvePlusContext> ContextLazy { get; set; } = null!;
        private IPlateSolvePlusContext Context => ContextLazy.Value;

        [Import(AllowDefault = true)]
        public Platesolveplus? PluginSettings { get; set; }

        [Import(AllowDefault = true)]
        public ITelescopeMediator? TelescopeMediator { get; set; }

        // Preferred, robust telescope coordinate access used for connection state & RA/Dec
        [Import(AllowDefault = true)]
        internal ITelescopeReferenceService? TelescopeReferenceService { get; set; }

        [Import(AllowDefault = true)]
        public IImageDataFactory? ImageDataFactory { get; set; }

        [Import(AllowDefault = true)]
        public IPlateSolverFactory? PlateSolverFactory { get; set; }

        private bool importsReady;
        private bool disposed;

        private bool pluginSettingsHooked;
        private bool busHooked;
        private bool telescopeReferenceHooked;

        // WEB API Variablen
        private PlateSolvePlusApiHost? apiHost;
        private bool apiBusy;

        // API state from bus / settings (source of truth for EnsureApiHostState)
        private bool apiEnabledState;
        private int apiPortState = 1899;
        private bool apiRequireTokenState;
        private string? apiTokenState;
        private bool apiStateInitialized;

        internal IAscomDeviceDiscoveryService AscomDiscovery => Context.AscomDiscovery;
        internal ISecondaryCameraService SecondaryCameraService => EnsureSecondaryCameraService();

        // ===== Dropdown =====
        public ObservableCollection<AscomDeviceInfo> SecondaryCameraDevices { get; } =
            new ObservableCollection<AscomDeviceInfo>();

        private AscomDeviceInfo? selectedSecondaryCamera;
        public AscomDeviceInfo? SelectedSecondaryCamera {
            get => selectedSecondaryCamera;
            set {
                if (ReferenceEquals(selectedSecondaryCamera, value)) return;
                selectedSecondaryCamera = value;
                RaisePropertyChanged(nameof(SelectedSecondaryCamera));
                SelectedSecondaryCameraProgId = selectedSecondaryCamera?.ProgId;
                _ = EnsureSecondaryCameraService();
                UpdateConnectionStateFromService();
            }
        }

        private string? selectedSecondaryCameraProgId;
        public string? SelectedSecondaryCameraProgId {
            get => selectedSecondaryCameraProgId;
            set {
                if (string.Equals(selectedSecondaryCameraProgId, value, StringComparison.OrdinalIgnoreCase)) return;
                selectedSecondaryCameraProgId = value;
                RaisePropertyChanged(nameof(SelectedSecondaryCameraProgId));

                if (!importsReady) return;

                var progId = selectedSecondaryCameraProgId ?? FallbackSecondaryCameraProgId;

                Context.SetActiveSecondaryCameraProgId(progId);
                Context.CurrentSecondaryCameraProgId = progId;
                // refresh cached service instance so subsequent calls use the same object
                _ = EnsureSecondaryCameraService();

                UpdateConnectionStateFromService();
            }
        }

        private ISecondaryCameraService? secondaryCameraServiceCached;
        private string? secondaryCameraProgIdCached;

        private ISecondaryCameraService EnsureSecondaryCameraService() {
            var desired = SelectedSecondaryCameraProgId
                ?? Context.CurrentSecondaryCameraProgId
                ?? FallbackSecondaryCameraProgId;

            if (secondaryCameraServiceCached != null &&
                string.Equals(secondaryCameraProgIdCached, desired, StringComparison.OrdinalIgnoreCase)) {
                return secondaryCameraServiceCached;
            }

            // driver changed → dispose old instance (should be disconnected already)
            try { secondaryCameraServiceCached?.Dispose(); } catch { }

            secondaryCameraServiceCached = new SecondaryCameraService(desired);
            secondaryCameraProgIdCached = desired;

            return secondaryCameraServiceCached;
        }

        // ============================================================
        // Dockable mirror properties (used by CameraDockableView.xaml)
        // ============================================================
        private DispatcherTimer? mountPollTimer;
        private static readonly TimeSpan MountPollInterval = TimeSpan.FromSeconds(1);

        private void StartMountPoll() {
            if (mountPollTimer != null) return;

            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            mountPollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) {
                Interval = MountPollInterval
            };

            mountPollTimer.Tick += (_, __) => {
                try {
                    RefreshTelescopeCoordsFromService();
                } catch (Exception ex) {
                    Logger.Debug($"Mount poll tick failed: {ex.Message}");
                }
            };

            mountPollTimer.Start();
        }

        private void StopMountPoll() {
            if (mountPollTimer == null) return;
            try { mountPollTimer.Stop(); } catch { }
            mountPollTimer = null;
        }

        // ===== Settings mirror (readonly) =====
        private double guideExposureSeconds = 2.0;
        public double GuideExposureSeconds {
            get => guideExposureSeconds;
            private set { if (Math.Abs(guideExposureSeconds - value) < 0.000001) return; guideExposureSeconds = value; RaisePropertyChanged(nameof(GuideExposureSeconds)); }
        }

        private int guideGain = -1;
        public int GuideGain {
            get => guideGain;
            private set { if (guideGain == value) return; guideGain = value; RaisePropertyChanged(nameof(GuideGain)); }
        }

        private int guideBinning = 1;
        public int GuideBinning {
            get => guideBinning;
            private set { if (guideBinning == value) return; guideBinning = value; RaisePropertyChanged(nameof(GuideBinning)); }
        }

        private double guideFocalLengthMm = 240.0;
        public double GuideFocalLengthMm {
            get => guideFocalLengthMm;
            private set { if (Math.Abs(guideFocalLengthMm - value) < 0.000001) return; guideFocalLengthMm = value; RaisePropertyChanged(nameof(GuideFocalLengthMm)); }
        }

        private bool useCameraPixelSize = true;
        public bool UseCameraPixelSize {
            get => useCameraPixelSize;
            private set { if (useCameraPixelSize == value) return; useCameraPixelSize = value; RaisePropertyChanged(nameof(UseCameraPixelSize)); }
        }

        private double guidePixelSizeUm = 3.75;
        public double GuidePixelSizeUm {
            get => guidePixelSizeUm;
            private set { if (Math.Abs(guidePixelSizeUm - value) < 0.000001) return; guidePixelSizeUm = value; RaisePropertyChanged(nameof(GuidePixelSizeUm)); }
        }

        // ===== Offset mirror (status / display) =====
        private bool offsetEnabled;
        public bool OffsetEnabled {
            get => offsetEnabled;
            private set { if (offsetEnabled == value) return; offsetEnabled = value; RaisePropertyChanged(nameof(OffsetEnabled)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        private OffsetMode offsetMode = OffsetMode.Rotation;
        public OffsetMode OffsetMode {
            get => offsetMode;
            private set { if (offsetMode == value) return; offsetMode = value; RaisePropertyChanged(nameof(OffsetMode)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        private double rotationQw = 1.0;
        public double RotationQw {
            get => rotationQw;
            private set { if (Math.Abs(rotationQw - value) < 0.000001) return; rotationQw = value; RaisePropertyChanged(nameof(RotationQw)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        private double rotationQx = 0.0;
        public double RotationQx {
            get => rotationQx;
            private set { if (Math.Abs(rotationQx - value) < 0.000001) return; rotationQx = value; RaisePropertyChanged(nameof(RotationQx)); }
        }

        private double rotationQy = 0.0;
        public double RotationQy {
            get => rotationQy;
            private set { if (Math.Abs(rotationQy - value) < 0.000001) return; rotationQy = value; RaisePropertyChanged(nameof(RotationQy)); }
        }

        private double rotationQz = 0.0;
        public double RotationQz {
            get => rotationQz;
            private set { if (Math.Abs(rotationQz - value) < 0.000001) return; rotationQz = value; RaisePropertyChanged(nameof(RotationQz)); }
        }

        private double offsetRaArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            private set { if (Math.Abs(offsetRaArcsec - value) < 0.000001) return; offsetRaArcsec = value; RaisePropertyChanged(nameof(OffsetRaArcsec)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        private double offsetDecArcsec;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            private set { if (Math.Abs(offsetDecArcsec - value) < 0.000001) return; offsetDecArcsec = value; RaisePropertyChanged(nameof(OffsetDecArcsec)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        private DateTime? offsetLastCalibratedUtc;
        public DateTime? OffsetLastCalibratedUtc {
            get => offsetLastCalibratedUtc;
            private set { if (offsetLastCalibratedUtc == value) return; offsetLastCalibratedUtc = value; RaisePropertyChanged(nameof(OffsetLastCalibratedUtc)); RaisePropertyChanged(nameof(OffsetStatusText)); }
        }

        // ===== Image preview =====
        private BitmapSource? lastCapturedImage;
        public BitmapSource? LastCapturedImage {
            get => lastCapturedImage;
            set { lastCapturedImage = value; RaisePropertyChanged(nameof(LastCapturedImage)); }
        }

        private string lastSolveSummary = "";
        public string LastSolveSummary {
            get => lastSolveSummary;
            set { lastSolveSummary = value; RaisePropertyChanged(nameof(LastSolveSummary)); }
        }

        // ===== New: text lines for your UI sketch =====
        private string lastGuiderSolveText = "-";
        public string LastGuiderSolveText {
            get => lastGuiderSolveText;
            set { if (lastGuiderSolveText == value) return; lastGuiderSolveText = value; RaisePropertyChanged(nameof(LastGuiderSolveText)); }
        }

        private string correctedSolveText = "-";
        public string CorrectedSolveText {
            get => correctedSolveText;
            set { if (correctedSolveText == value) return; correctedSolveText = value; RaisePropertyChanged(nameof(CorrectedSolveText)); }
        }

        // internal cached solves
        private (double raDeg, double decDeg)? lastGuiderSolveDeg;
        private (double raDeg, double decDeg)? lastCorrectedSolveDeg;

        // ===== Commands =====
        public ICommand RefreshSecondaryCameraListCommand { get; }
        public ICommand OpenDriverSettingsCommand { get; }
        public ICommand ConnectSecondaryCommand { get; }
        public ICommand DisconnectSecondaryCommand { get; }

        // NEW: Capture-only (sets offset if not present)
        public ICommand CaptureOnlyCommand { get; }

        // Existing: "Capture + Sync/Slew" button in your view binds to this name
        public ICommand CaptureAndSolveCommand { get; }

        public ICommand CalibrateOffsetCommand { get; }

        // NEW: Slew to a given target (main coords) and center using secondary camera + offset mapping
        public ICommand SlewToTargetAndCenterCommand { get; }

        // ===== Target coordinates (Main) for "Slew to Target + Center" =====
        private double targetRaDeg = 0.0;
        public double TargetRaDeg {
            get => targetRaDeg;
            set {
                var v = NormalizeRaDeg(value);
                if (Math.Abs(targetRaDeg - v) < 1e-9) return;
                targetRaDeg = v;
                RaisePropertyChanged(nameof(TargetRaDeg));
                RaiseCaptureCalibrateUiState();
            }
        }

        private double targetDecDeg = 0.0;
        public double TargetDecDeg {
            get => targetDecDeg;
            set {
                var v = Math.Max(-90.0, Math.Min(90.0, value));
                if (Math.Abs(targetDecDeg - v) < 1e-9) return;
                targetDecDeg = v;
                RaisePropertyChanged(nameof(TargetDecDeg));
                RaiseCaptureCalibrateUiState();
            }
        }


        // ===== View bindings compatibility =====
        public bool UseSlewInsteadOfSync {
            get => useSlewInsteadOfSync;
            set {
                if (useSlewInsteadOfSync == value) return;
                useSlewInsteadOfSync = value;
                RaisePropertyChanged(nameof(UseSlewInsteadOfSync));
                RaiseCaptureCalibrateUiState();
            }
        }
        private bool useSlewInsteadOfSync;

        public string CaptureAndSolveButtonText =>
            UseSlewInsteadOfSync ? "Center + Sync" : "Capture + Sync";

        // ✅ require actual coordinates, not just "some connected flag"
        public bool CanCaptureAndSolve =>
            importsReady &&
            MountState == MountConnectionState.ConnectedWithCoords &&
            IsSecondaryConnected;

        // Capture-only should work without mount coordinates (pure solve).
        public bool CanCaptureOnly =>
            importsReady &&
            IsSecondaryConnected;

        // "Center" (Capture + Slew) requires a calibrated offset AND mount coords.
        public bool CanCenterWithOffset =>
            importsReady &&
            MountState == MountConnectionState.ConnectedWithCoords &&
            IsSecondaryConnected &&
            HasOffsetSet;

        // Single enable flag for the Capture+Sync/Slew button in the view.
        // - Sync mode: CanCaptureAndSolve
        // - Center mode: CanCenterWithOffset
        public bool CanCaptureAndSolveAction =>
            UseSlewInsteadOfSync ? CanCenterWithOffset : CanCaptureAndSolve;

        // Enable flag for the "Slew to Target + Center" button.
        public bool CanSlewToTargetAndCenter =>
            CanCenterWithOffset &&
            IsValidRaDec(TargetRaDeg, TargetDecDeg);


        private bool HasOffsetSet {
            get {
                if (OffsetMode == OffsetMode.Rotation) {
                    var isIdentity = Math.Abs(RotationQw - 1.0) < 1e-6 &&
                                     Math.Abs(RotationQx) < 1e-6 &&
                                     Math.Abs(RotationQy) < 1e-6 &&
                                     Math.Abs(RotationQz) < 1e-6;
                    return !isIdentity;
                }

                return Math.Abs(OffsetRaArcsec) > 1e-6 || Math.Abs(OffsetDecArcsec) > 1e-6;
            }
        }

        // ✅ require coordinates for calibration too
        public bool CanCalibrateOffset =>
            importsReady &&
            MountState == MountConnectionState.ConnectedWithCoords &&
            IsSecondaryConnected &&
            PluginSettings?.Settings != null &&
            !HasOffsetSet;

        private void RaiseCaptureCalibrateUiState() {
            RaisePropertyChanged(nameof(CanCaptureOnly));
            RaisePropertyChanged(nameof(CanCenterWithOffset));
            RaisePropertyChanged(nameof(CanCaptureAndSolve));
            RaisePropertyChanged(nameof(CanCaptureAndSolveAction));
            RaisePropertyChanged(nameof(CanSlewToTargetAndCenter));
            RaisePropertyChanged(nameof(CanCalibrateOffset));
            RaisePropertyChanged(nameof(CaptureAndSolveButtonText));
        }

        private bool isSecondaryConnected;
        public bool IsSecondaryConnected {
            get => isSecondaryConnected;
            set {
                isSecondaryConnected = value;
                RaisePropertyChanged(nameof(IsSecondaryConnected));
                RaiseCaptureCalibrateUiState();
            }
        }

        private void UpdateConnectionStateFromService() {
            try { IsSecondaryConnected = SecondaryCameraService.IsConnected; } catch { IsSecondaryConnected = false; }
            RaiseCaptureCalibrateUiState();
        }

        private string statusText = "Initializing…";
        public string StatusText {
            get => statusText;
            set { statusText = value; RaisePropertyChanged(nameof(StatusText)); }
        }

        private string detailsText = "";
        public string DetailsText {
            get => detailsText;
            set { detailsText = value; RaisePropertyChanged(nameof(DetailsText)); }
        }

        // ============================================================
        // Mount 3-State Logic
        // ============================================================

        public enum MountConnectionState {
            Disconnected = 0,
            ConnectedNoCoords = 1,
            ConnectedWithCoords = 2
        }

        private MountConnectionState mountState;
        public MountConnectionState MountState {
            get => mountState;
            private set {
                if (mountState == value) return;
                mountState = value;
                RaisePropertyChanged(nameof(MountState));
                RaisePropertyChanged(nameof(IsMountConnected));
                RaisePropertyChanged(nameof(MountStatusText));
                RaisePropertyChanged(nameof(MountDetailsText));
                RaiseCaptureCalibrateUiState();
            }
        }

        // Compatibility for existing bindings/logic
        public bool IsMountConnected => MountState != MountConnectionState.Disconnected;

        public string MountStatusText => MountState switch {
            MountConnectionState.ConnectedWithCoords => "Mount: Verbunden ✅",
            MountConnectionState.ConnectedNoCoords => "Mount: Verbunden ⚠️ (keine Koordinaten)",
            _ => "Mount: Nicht verbunden ❌"
        };

        private string mountDetailsText = string.Empty;
        public string MountDetailsText {
            get => mountDetailsText;
            private set {
                if (mountDetailsText == value) return;
                mountDetailsText = value;
                RaisePropertyChanged(nameof(MountDetailsText));
            }
        }

        private void SetMountState(MountConnectionState state, string? details) {
            MountDetailsText = details ?? string.Empty;
            MountState = state;
        }

        private static bool IsValidRaDec(double raDeg, double decDeg) {
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg)) return false;
            if (double.IsInfinity(raDeg) || double.IsInfinity(decDeg)) return false;

            if (decDeg < -90.0 || decDeg > 90.0) return false;
            if (raDeg < 0.0 || raDeg >= 360.0) return false;

            // key: treat exact 0/0 as "dummy coordinates when disconnected"
            if (Math.Abs(raDeg) < 1e-9 && Math.Abs(decDeg) < 1e-9) return false;

            return true;
        }

        public string OffsetStatusText {
            get {
                if (!HasOffsetSet) return "Offset not set";

                if (OffsetMode == OffsetMode.Rotation) {
                    return $"Rotation offset set (qw={RotationQw:0.###})";
                }

                return $"Arcsec offset set (ΔRA={OffsetRaArcsec:0.###}\", ΔDec={OffsetDecArcsec:0.###}\")";
            }
        }

[ImportingConstructor]
        public CameraDockable(IProfileService profileService) : base(profileService) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));

            Title = "PlateSolvePlus – Camera";
            IsVisible = true;

            RefreshSecondaryCameraListCommand = new RelayCommand(_ => RefreshSecondaryCameraListSafe());
            OpenDriverSettingsCommand = new SimpleAsyncCommand(OpenDriverSettingsAsync);
            ConnectSecondaryCommand = new SimpleAsyncCommand(ConnectSecondaryAsync);
            DisconnectSecondaryCommand = new SimpleAsyncCommand(DisconnectSecondaryAsync);

            // NEW: capture-only
            CaptureOnlyCommand = new SimpleAsyncCommand(CaptureOnlyAsync);

            // Existing: capture + sync/slew
            CaptureAndSolveCommand = new SimpleAsyncCommand(CaptureAndSyncOrSlewAsync);

            CalibrateOffsetCommand = new SimpleAsyncCommand(CalibrateOffsetAsync);

            SlewToTargetAndCenterCommand = new SimpleAsyncCommand(SlewToTargetAndCenterAsync);


            StatusText = "Waiting for MEF imports…";
            this.profileService.ProfileChanged += ProfileService_ProfileChanged;
        }

        public void OnImportsSatisfied() {
            importsReady = true;

            try {
                var initialProgId = !string.IsNullOrWhiteSpace(Context.CurrentSecondaryCameraProgId)
                    ? Context.CurrentSecondaryCameraProgId!
                    : FallbackSecondaryCameraProgId;

                Context.SetActiveSecondaryCameraProgId(initialProgId);
                Context.CurrentSecondaryCameraProgId = initialProgId;

                SelectedSecondaryCameraProgId = initialProgId;

                StatusText = "Ready";
                RefreshSecondaryCameraList();
                HookPluginSettings();
                HookSettingsBus();

                WireTelescopeReferenceService();

                StartMountPoll();
                UpdateMountConnectionState();

                ApplySettings(ReadSettingsFromPluginInstance(), force: true);
                UpdateMountConnectionState();
                UpdateConnectionStateFromService();

                // ---- API state init (one time) ----
                var s = PluginSettings?.Settings;
                if (s != null) {
                    apiEnabledState = s.ApiEnabled;
                    apiPortState = s.ApiPort;
                    apiRequireTokenState = s.ApiRequireToken;
                    apiTokenState = s.ApiToken;
                    apiStateInitialized = true;
                }

                // ✅ Single source of truth: EnsureApiHostState controls create/start/stop
                EnsureApiHostState();

            } catch (Exception ex) {
                StatusText = "Initialization failed ❌";
                DetailsText = ex.ToString();
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;

            // Stop API host
            apiHost?.Dispose();
            apiHost = null;

            // Unhook in exactly one place each
            UnhookPluginSettings();
            UnhookSettingsBus();

            UnwireTelescopeReferenceService();
            StopMountPoll();

            if (profileService != null) {
                profileService.ProfileChanged -= ProfileService_ProfileChanged;
            }
            try { secondaryCameraServiceCached?.Dispose(); } catch { }
            secondaryCameraServiceCached = null;
            secondaryCameraProgIdCached = null;

        }

        private void ProfileService_ProfileChanged(object? sender, EventArgs e) {
            ApplySettings(ReadSettingsFromPluginInstance(), force: true);

            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;
            }

            UpdateMountConnectionState();
        }

        private void HookPluginSettings() {
            if (pluginSettingsHooked) return;
            if (PluginSettings == null) return;

            PluginSettings.PropertyChanged += PluginSettings_PropertyChanged;

            // Änderungen aus Options.xaml passieren auf Settings
            if (PluginSettings.Settings != null) {
                PluginSettings.Settings.PropertyChanged += Settings_PropertyChanged;
            }

            pluginSettingsHooked = true;
        }

        private void UnhookPluginSettings() {
            if (!pluginSettingsHooked) return;

            if (PluginSettings != null) {
                PluginSettings.PropertyChanged -= PluginSettings_PropertyChanged;

                if (PluginSettings.Settings != null) {
                    PluginSettings.Settings.PropertyChanged -= Settings_PropertyChanged;
                }
            }

            pluginSettingsHooked = false;
        }

        private bool settingsBusHooked;

        private void HookSettingsBus() {
            if (settingsBusHooked) return;
            PlateSolvePlusSettingsBus.SettingChanged += SettingsBus_SettingChanged;
            settingsBusHooked = true;
        }

        private void UnhookSettingsBus() {
            if (!settingsBusHooked) return;
            PlateSolvePlusSettingsBus.SettingChanged -= SettingsBus_SettingChanged;
            settingsBusHooked = false;
        }

        private void WireTelescopeReferenceService() {
            if (telescopeReferenceHooked) return;

            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;
                TelescopeReferenceService.ReferenceUpdated += TelescopeReferenceService_ReferenceUpdated;
                telescopeReferenceHooked = true;
            }

            UpdateMountConnectionState();
        }

        private void UnwireTelescopeReferenceService() {
            if (!telescopeReferenceHooked) return;

            try {
                if (TelescopeReferenceService != null) {
                    TelescopeReferenceService.ReferenceUpdated -= TelescopeReferenceService_ReferenceUpdated;
                    TelescopeReferenceService.TelescopeMediator = null;
                }
            } catch { }

            telescopeReferenceHooked = false;
        }

        private void TelescopeReferenceService_ReferenceUpdated(object? sender, TelescopeReferenceUpdatedEventArgs e) {
            try {
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess()) {
                    Application.Current.Dispatcher.InvokeAsync(() => ApplyTelescopeReferenceUpdate(e));
                } else {
                    ApplyTelescopeReferenceUpdate(e);
                }
            } catch { }
        }

        private void ApplyTelescopeReferenceUpdate(TelescopeReferenceUpdatedEventArgs e) {
            if (e.Success && e.RaDeg.HasValue && e.DecDeg.HasValue &&
                IsValidRaDec(e.RaDeg.Value, e.DecDeg.Value)) {

                SetMountState(
                    MountConnectionState.ConnectedWithCoords,
                    $"RA {AstroFormat.FormatRaHms(e.RaDeg.Value)} / Dec {AstroFormat.FormatDecDms(e.DecDeg.Value)}");
                return;
            }

            var connected = DetectMountConnected();
            if (connected) {
                SetMountState(MountConnectionState.ConnectedNoCoords, "Verbunden ⚠️ (Koordinaten noch nicht verfügbar…)");
            } else {
                SetMountState(MountConnectionState.Disconnected, string.Empty);
            }
        }

        private void SettingsBus_SettingChanged(object? sender, PlateSolvePlusSettingChangedEventArgs e) {
            if (e == null) return;

            switch (e.Key) {
                case nameof(PlateSolvePlusSettings.ApiEnabled):
                case nameof(PlateSolvePlusSettings.ApiPort):
                case nameof(PlateSolvePlusSettings.ApiRequireToken):
                case nameof(PlateSolvePlusSettings.ApiToken):

                    Logger.Info($"[PlateSolvePlus] SettingsBus: {e.Key} changed -> {e.Value}");
                    EnsureApiHostState();
                    break;
            }
            try {
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess()) {
                    Application.Current.Dispatcher.InvokeAsync(() => ApplySingleFromBus(e.Key, e.Value));
                } else {
                    ApplySingleFromBus(e.Key, e.Value);
                }
            } catch { }
        }

        private void ApplySingleFromBus(string key, object? value) {
            switch (key) {
                case nameof(PlateSolvePlusSettings.GuideExposureSeconds):
                    if (value is double d1) GuideExposureSeconds = d1;
                    break;

                case nameof(PlateSolvePlusSettings.GuideGain):
                    if (value is int i1) GuideGain = i1;
                    break;

                case nameof(PlateSolvePlusSettings.GuideBinning):
                    if (value is int i2) GuideBinning = Math.Max(1, i2);
                    break;

                case nameof(PlateSolvePlusSettings.GuideFocalLengthMm):
                    if (value is double d2) GuideFocalLengthMm = Math.Max(1.0, d2);
                    break;

                case nameof(PlateSolvePlusSettings.UseCameraPixelSize):
                    if (value is bool b1) UseCameraPixelSize = b1;
                    break;

                case nameof(PlateSolvePlusSettings.GuidePixelSizeUm):
                    if (value is double d3) GuidePixelSizeUm = Math.Max(0.1, d3);
                    break;

                case nameof(PlateSolvePlusSettings.OffsetEnabled):
                    if (value is bool bo) OffsetEnabled = bo;
                    break;

                case nameof(PlateSolvePlusSettings.OffsetRaArcsec):
                    if (value is double dra) OffsetRaArcsec = dra;
                    break;

                case nameof(PlateSolvePlusSettings.OffsetDecArcsec):
                    if (value is double ddec) OffsetDecArcsec = ddec;
                    break;

                case nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc):
                    if (value is DateTime dt) OffsetLastCalibratedUtc = dt;
                    else OffsetLastCalibratedUtc = null;
                    break;

                case nameof(PlateSolvePlusSettings.OffsetMode):
                    if (value is OffsetMode om) OffsetMode = om;
                    break;

                case nameof(PlateSolvePlusSettings.RotationQw):
                    if (value is double qw) RotationQw = qw;
                    break;

                case nameof(PlateSolvePlusSettings.RotationQx):
                    if (value is double qx) RotationQx = qx;
                    break;

                case nameof(PlateSolvePlusSettings.RotationQy):
                    if (value is double qy) RotationQy = qy;
                    break;

                case nameof(PlateSolvePlusSettings.RotationQz):
                    if (value is double qz) RotationQz = qz;
                    break;

                case nameof(PlateSolvePlusSettings.ApiEnabled):
                    if (value is bool b) { apiEnabledState = b; apiStateInitialized = true; }
                    EnsureApiHostState();
                    break;

                case nameof(PlateSolvePlusSettings.ApiPort):
                    if (value is int p) { apiPortState = p; apiStateInitialized = true; }
                    EnsureApiHostState();
                    break;

                case nameof(PlateSolvePlusSettings.ApiRequireToken):
                    if (value is bool rt) { apiRequireTokenState = rt; apiStateInitialized = true; }
                    EnsureApiHostState();
                    break;

                case nameof(PlateSolvePlusSettings.ApiToken):
                    apiTokenState = value as string;
                    apiStateInitialized = true;
                    EnsureApiHostState();
                    break;

                default:
                    break;
            }

            // if we already have a guider solve -> refresh corrected display immediately
            UpdateSolvedTextsAfterOffsetChange();
            RaiseCaptureCalibrateUiState();
        }

        private void UpdateSolvedTextsAfterOffsetChange() {
            if (!lastGuiderSolveDeg.HasValue) return;

            var (ra, dec) = lastGuiderSolveDeg.Value;
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(ra, dec);

            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (No offset set) → using solve as-is.";
        }

        private void PluginSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
            ApplySettings(ReadSettingsFromPluginInstance(), force: false);
            UpdateSolvedTextsAfterOffsetChange();
            var s = PluginSettings?.Settings;
            if (s != null) {
                switch (e.PropertyName) {
                    case nameof(PlateSolvePlusSettings.ApiEnabled):
                        PlateSolvePlusSettingsBus.Publish(nameof(PlateSolvePlusSettings.ApiEnabled), s.ApiEnabled);
                        break;
                    case nameof(PlateSolvePlusSettings.ApiPort):
                        PlateSolvePlusSettingsBus.Publish(nameof(PlateSolvePlusSettings.ApiPort), s.ApiPort);
                        break;
                    case nameof(PlateSolvePlusSettings.ApiRequireToken):
                        PlateSolvePlusSettingsBus.Publish(nameof(PlateSolvePlusSettings.ApiRequireToken), s.ApiRequireToken);
                        break;
                    case nameof(PlateSolvePlusSettings.ApiToken):
                        PlateSolvePlusSettingsBus.Publish(nameof(PlateSolvePlusSettings.ApiToken), s.ApiToken);
                        break;
                }
            }

            return;
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
            // Optional: nur auf API-relevante Properties reagieren
            if (e.PropertyName == nameof(PlateSolvePlusSettings.ApiEnabled) ||
                e.PropertyName == nameof(PlateSolvePlusSettings.ApiPort) ||
                e.PropertyName == nameof(PlateSolvePlusSettings.ApiRequireToken) ||
                e.PropertyName == nameof(PlateSolvePlusSettings.ApiToken)) {
                return;
            }
        }

        private PlateSolvePlusSettings ReadSettingsFromPluginInstance() {
            var ps = PluginSettings;
            var s = new PlateSolvePlusSettings();
            if (ps?.Settings == null) return s;

            var src = ps.Settings;

            s.GuideExposureSeconds = src.GuideExposureSeconds;
            s.GuideGain = src.GuideGain;
            s.GuideBinning = src.GuideBinning;

            s.GuideFocalLengthMm = src.GuideFocalLengthMm;
            s.UseCameraPixelSize = src.UseCameraPixelSize;
            s.GuidePixelSizeUm = src.GuidePixelSizeUm;

            s.OffsetEnabled = src.OffsetEnabled;
            s.OffsetMode = src.OffsetMode;

            s.OffsetRaArcsec = src.OffsetRaArcsec;
            s.OffsetDecArcsec = src.OffsetDecArcsec;
            s.OffsetLastCalibratedUtc = src.OffsetLastCalibratedUtc;

            s.RotationQw = src.RotationQw;
            s.RotationQx = src.RotationQx;
            s.RotationQy = src.RotationQy;
            s.RotationQz = src.RotationQz;

            return s;
        }

        private void ApplySettings(PlateSolvePlusSettings s, bool force) {
            if (force) {
                GuideExposureSeconds = s.GuideExposureSeconds;
                GuideGain = s.GuideGain;
                GuideBinning = s.GuideBinning;
                GuideFocalLengthMm = s.GuideFocalLengthMm;
                UseCameraPixelSize = s.UseCameraPixelSize;
                GuidePixelSizeUm = s.GuidePixelSizeUm;

                OffsetEnabled = s.OffsetEnabled;
                OffsetMode = s.OffsetMode;
                OffsetRaArcsec = s.OffsetRaArcsec;
                OffsetDecArcsec = s.OffsetDecArcsec;
                OffsetLastCalibratedUtc = s.OffsetLastCalibratedUtc;

                RotationQw = s.RotationQw;
                RotationQx = s.RotationQx;
                RotationQy = s.RotationQy;
                RotationQz = s.RotationQz;

                RaiseCaptureCalibrateUiState();
                return;
            }

            if (Math.Abs(GuideExposureSeconds - s.GuideExposureSeconds) > 0.000001) GuideExposureSeconds = s.GuideExposureSeconds;
            if (GuideGain != s.GuideGain) GuideGain = s.GuideGain;
            if (GuideBinning != s.GuideBinning) GuideBinning = s.GuideBinning;

            if (Math.Abs(GuideFocalLengthMm - s.GuideFocalLengthMm) > 0.000001) GuideFocalLengthMm = s.GuideFocalLengthMm;
            if (UseCameraPixelSize != s.UseCameraPixelSize) UseCameraPixelSize = s.UseCameraPixelSize;
            if (Math.Abs(GuidePixelSizeUm - s.GuidePixelSizeUm) > 0.000001) GuidePixelSizeUm = s.GuidePixelSizeUm;

            if (OffsetEnabled != s.OffsetEnabled) OffsetEnabled = s.OffsetEnabled;
            if (OffsetMode != s.OffsetMode) OffsetMode = s.OffsetMode;

            if (Math.Abs(OffsetRaArcsec - s.OffsetRaArcsec) > 0.000001) OffsetRaArcsec = s.OffsetRaArcsec;
            if (Math.Abs(OffsetDecArcsec - s.OffsetDecArcsec) > 0.000001) OffsetDecArcsec = s.OffsetDecArcsec;
            if (OffsetLastCalibratedUtc != s.OffsetLastCalibratedUtc) OffsetLastCalibratedUtc = s.OffsetLastCalibratedUtc;

            if (Math.Abs(RotationQw - s.RotationQw) > 0.000001) RotationQw = s.RotationQw;
            if (Math.Abs(RotationQx - s.RotationQx) > 0.000001) RotationQx = s.RotationQx;
            if (Math.Abs(RotationQy - s.RotationQy) > 0.000001) RotationQy = s.RotationQy;
            if (Math.Abs(RotationQz - s.RotationQz) > 0.000001) RotationQz = s.RotationQz;

            RaiseCaptureCalibrateUiState();
        }

        // ============================
        // Your requested workflow
        // ============================

        private async Task CaptureOnlyAsync() {
            UpdateMountConnectionState();

            var solve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!solve.success) return;

            // remember guider solve (always)
            lastGuiderSolveDeg = (solve.raDeg, solve.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", solve.raDeg, solve.decDeg);

            // corrected preview (only if offset is set)
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(solve.raDeg, solve.decDeg);
            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (No offset set) → using solve as-is.";

            // CaptureOnly MUST NOT change any state (no auto offset, no sync, no slew)
            // If mount coords are available, compare and suggest recalibration when offset is active.
            if (MountState == MountConnectionState.ConnectedWithCoords &&
                TelescopeReferenceService != null &&
                TelescopeReferenceService.TryGetCurrentRaDec(out var mainRaDeg, out var mainDecDeg) &&
                IsValidRaDec(mainRaDeg, mainDecDeg)) {

                var compare = lastCorrectedSolveDeg ?? (solve.raDeg, solve.decDeg);

                var deltaRaDeg = NormalizeDeltaDeg(compare.raDeg - mainRaDeg);
                var deltaDecDeg = compare.decDeg - mainDecDeg;

                var decMidRad = ((mainDecDeg + compare.decDeg) * 0.5) * Math.PI / 180.0;
                var deltaRaArcsec = deltaRaDeg * 3600.0 * Math.Cos(decMidRad);
                var deltaDecArcsec = deltaDecDeg * 3600.0;

                var sepArcmin = SeparationArcmin(mainRaDeg, mainDecDeg, compare.raDeg, compare.decDeg);
                var sepArcsec = sepArcmin * 60.0;

                var thrArcmin = PluginSettings?.Settings?.CenteringThresholdArcmin ?? 1.0;
                var suggestRecal = HasOffsetSet && sepArcmin > thrArcmin;

                StatusText = suggestRecal ? "CaptureOnly: Offset mismatch ⚠️" : "CaptureOnly done ✅";

                DetailsText =
                    $"Mount: RA {AstroFormat.FormatRaHms(mainRaDeg)} / Dec {AstroFormat.FormatDecDms(mainDecDeg)} (deg: {mainRaDeg:0.######}, {mainDecDeg:0.######})" +
                    $"{(lastCorrectedSolveDeg.HasValue ? "Compared(Corrected)" : "Compared(Solved)")}: RA {AstroFormat.FormatRaHms(compare.raDeg)} / Dec {AstroFormat.FormatDecDms(compare.decDeg)} (deg: {compare.raDeg:0.######}, {compare.decDeg:0.######})" +
                    $"Δ: dRA={deltaRaArcsec:+0.0;-0.0;0.0}\"  dDec={deltaDecArcsec:+0.0;-0.0;0.0}\"  Sep={sepArcsec:0.0}\"" +
                    (!HasOffsetSet ? "Offset not calibrated → run Calibrate Offset." : string.Empty) +
                    (suggestRecal ? $"Offset likely outdated (Sep>{thrArcmin:0.###} arcmin). Consider recalibrating the offset." : "Offset seems consistent with current mount position.");
            } else {
                StatusText = "CaptureOnly done ✅";
                DetailsText =
                    "Platesolve completed." +
                    (MountState == MountConnectionState.ConnectedWithCoords
                        ? "Mount RA/Dec not available via TelescopeReferenceService."
                        : "Mount not connected / coordinates not available → no comparison possible.") +
                    (!HasOffsetSet ? "Offset not calibrated → run Calibrate Offset." : "");
            }
        }
        private async Task CaptureAndSyncOrSlewAsync() {
            UpdateMountConnectionState();

            if (MountState != MountConnectionState.ConnectedWithCoords) {
                StatusText = "Mount not ready ❌";
                DetailsText = "Mount not connected OR coordinates not available.";
                return;
            }

            if (!IsSecondaryConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            var solve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!solve.success) return;

            // compute corrected (only if enabled + set)
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(solve.raDeg, solve.decDeg);
            var target = lastCorrectedSolveDeg ?? (solve.raDeg, solve.decDeg);

            // remember guider solve (for UI)
            lastGuiderSolveDeg = (solve.raDeg, solve.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", solve.raDeg, solve.decDeg);

            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (No offset set) → using solve as-is.";

            if (!TryToCoordinates(target.raDeg, target.decDeg, out var targetCoords)) {
                return;
            }

            if (UseSlewInsteadOfSync) {
                // Capture + Slew (Center) requires an active + calibrated offset
                if (!HasOffsetSet) {
                    StatusText = "Center not available ❌";
                    DetailsText = "Capture + Slew (Center) requires a calibrated offset.";
                    return;
                }

                var thr = PluginSettings?.Settings?.CenteringThresholdArcmin ?? 1.0;
                var max = PluginSettings?.Settings?.CenteringMaxAttempts ?? 5;

                await CenterWithSecondaryAsync(thr, max, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // Capture + Sync
            if (TelescopeMediator == null) {
                StatusText = "Cannot sync mount ❌";
                DetailsText = "TelescopeMediator not available (MEF import failed).";
                return;
            }

            try {
                StatusText = "Syncing mount…";
                DetailsText = $"SyncTo: RA={target.raDeg:0.######}°, Dec={target.decDeg:0.######}°";

                var ok = await TelescopeMediator.Sync(targetCoords).ConfigureAwait(false);

                StatusText = ok ? "Capture + Sync done ✅" : "Sync failed ❌";
                DetailsText = ok
                    ? $"Mount synced to {(lastCorrectedSolveDeg.HasValue ? "corrected" : "solved")} coordinates." +
                      $"RA={target.raDeg:0.######}°, Dec={target.decDeg:0.######}°"
                    : $"Sync returned false. Target was: RA={target.raDeg:0.######}°, Dec={target.decDeg:0.######}°";
            } catch (Exception ex) {
                StatusText = "Sync failed ❌";
                DetailsText = ex.ToString();
            }
        }

        private async Task AutoSetOffsetIfMissingAsync(double guiderRaDeg, double guiderDecDeg) {
            if (PluginSettings?.Settings == null) {
                StatusText = "Cannot set offset ❌";
                DetailsText = "PluginSettings.Settings not available.";
                return;
            }

            if (TelescopeReferenceService == null) {
                StatusText = "Cannot set offset ❌";
                DetailsText = "TelescopeReferenceService not available.";
                return;
            }

            if (!TelescopeReferenceService.TryGetCurrentRaDec(out var mainRaDeg, out var mainDecDeg) ||
                !IsValidRaDec(mainRaDeg, mainDecDeg)) {

                StatusText = "Cannot set offset ❌";
                DetailsText = "Mount RA/Dec not available (or dummy 0/0).";
                return;
            }

            try {
                var svc = new OffsetService();
                svc.Calibrate(PluginSettings.Settings, mainRaDeg, mainDecDeg, guiderRaDeg, guiderDecDeg);

                PluginSettings.Settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

                ApplySettings(ReadSettingsFromPluginInstance(), force: false);

                StatusText = "Offset set automatically ✅";
                DetailsText =
                    $"Main RA/Dec: {mainRaDeg:0.######}°, {mainDecDeg:0.######}°\n" +
                    $"Guider RA/Dec: {guiderRaDeg:0.######}°, {guiderDecDeg:0.######}°\n" +
                    $"Mode: {PluginSettings.Settings.OffsetMode}";
            } catch (Exception ex) {
                StatusText = "Auto offset failed ❌";
                DetailsText = ex.ToString();
            }
        }

        private (double raDeg, double decDeg)? ComputeCorrectedIfEnabled(double raDeg, double decDeg) {
            if (!HasOffsetSet) return null;
            if (PluginSettings?.Settings == null) return null;

            try {
                var svc = new OffsetService();
                var res = svc.ApplyToGuiderSolve(PluginSettings.Settings, raDeg, decDeg);
                return res;
            } catch {
                return null;
            }
        }

        private static string FormatSolvedLine(string label, double raDeg, double decDeg) {
            return $"{label}: RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}  (deg: {raDeg:0.######}, {decDeg:0.######})";
        }

        private async Task CalibrateOffsetAsync() {
            if (!importsReady) {
                StatusText = "Not ready yet…";
                DetailsText = "MEF imports are not satisfied yet.";
                return;
            }

            if (PluginSettings == null) {
                StatusText = "PluginSettings not available ❌";
                DetailsText = "MEF did not provide Platesolveplus instance.";
                return;
            }

            UpdateMountConnectionState();
            if (MountState != MountConnectionState.ConnectedWithCoords) {
                StatusText = "Mount not ready ❌";
                DetailsText = "Mount not connected OR coordinates not available.";
                return;
            }

            if (!SecondaryCameraService.IsConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            if (TelescopeReferenceService == null) {
                StatusText = "Cannot read mount coordinates ❌";
                DetailsText = "TelescopeReferenceService not available (MEF import failed).";
                return;
            }

            if (!TelescopeReferenceService.TryGetCurrentRaDec(out var mainRaDeg, out var mainDecDeg) ||
                !IsValidRaDec(mainRaDeg, mainDecDeg)) {

                StatusText = "Cannot read mount coordinates ❌";
                DetailsText = "Mount connected, but RA/Dec not available (or dummy 0/0).";
                return;
            }

            StatusText = "Calibrating offset…";
            DetailsText = "Capturing + solving guider frame.";

            var guiderSolve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!guiderSolve.success) return;

            lastGuiderSolveDeg = (guiderSolve.raDeg, guiderSolve.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", guiderSolve.raDeg, guiderSolve.decDeg);

            var svc = new OffsetService();
            svc.Calibrate(PluginSettings.Settings, mainRaDeg, mainDecDeg, guiderSolve.raDeg, guiderSolve.decDeg);

                        PluginSettings.Settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

            StatusText = "Offset calibrated ✅";
            DetailsText =
                $"Main RA/Dec: {mainRaDeg:0.######}°, {mainDecDeg:0.######}°\n" +
                $"Guider RA/Dec: {guiderSolve.raDeg:0.######}°, {guiderSolve.decDeg:0.######}°\n" +
                $"Mode: {PluginSettings.Settings.OffsetMode}";

            ApplySettings(ReadSettingsFromPluginInstance(), force: false);

            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(guiderSolve.raDeg, guiderSolve.decDeg);
            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (No offset set) → using solve as-is.";
        }

        private async Task<(bool success, double raDeg, double decDeg)> CaptureAndSolveGuiderAsync(bool updateUi) {
            if (!importsReady) return (false, 0, 0);

            try {
                var progId = SelectedSecondaryCameraProgId ?? FallbackSecondaryCameraProgId;
                Context.SetActiveSecondaryCameraProgId(progId);
                Context.CurrentSecondaryCameraProgId = progId;

                if (!SecondaryCameraService.IsConnected) {
                    if (updateUi) {
                        StatusText = "Secondary camera not connected ❌";
                        DetailsText = "Click Connect first.";

                        // refresh cached service instance so subsequent calls use the same object
                        _ = EnsureSecondaryCameraService();
                        UpdateConnectionStateFromService();
                    }
                    return (false, 0, 0);
                }

                var imgFactory = ImageDataFactory;
                var psFactory = PlateSolverFactory;

                if (imgFactory == null || psFactory == null) {
                    if (updateUi) {
                        StatusText = "Missing NINA factories ❌";
                        DetailsText = $"ImageDataFactory={imgFactory != null}, PlateSolverFactory={psFactory != null}";
                    }
                    return (false, 0, 0);
                }

                var exposure = GuideExposureSeconds;
                var bin = Math.Max(1, GuideBinning);
                var gain = GetEffectiveGain();

                if (updateUi) {
                    StatusText = "Capturing frame…";
                    DetailsText = $"Exposure={exposure:0.###}s, Bin={bin}, Gain={(gain.HasValue ? gain.Value.ToString() : "auto")}";
                }

                var frame = await SecondaryCameraService.CaptureAsync(
                    exposureSeconds: exposure,
                    binX: bin,
                    binY: bin,
                    gain: gain,
                    ct: CancellationToken.None).ConfigureAwait(false);

                ushort[] packed = ConvertToUShortRowMajor(frame.Pixels, frame.Width, frame.Height);

                if (updateUi) {
                    var opts = new PreviewRenderOptions {
                        AutoStretch = PreviewAutoStretchEnabled,
                        StretchLowPercentile = 0.01,
                        StretchHighPercentile = 0.995,
                        Gamma = 0.9
                    };

                    var preview = previewRenderService.RenderPreview(
                        new CapturedFrame(frame.Width, frame.Height, frame.BitDepth, frame.Pixels),
                        opts);

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        LastCapturedImage = preview;
                        StatusText = "Building IImageData…";
                    });
                }

                var imageData = imgFactory.CreateBaseImageData(
                    input: packed,
                    width: frame.Width,
                    height: frame.Height,
                    bitDepth: frame.BitDepth,
                    isBayered: false,
                    metaData: new ImageMetaData());

                var s = PluginSettings?.Settings;
                double searchRadiusDeg = s?.SolverSearchRadiusDeg ?? 5.0;
                int downsample = s?.SolverDownsample ?? 2;
                int timeoutSec = s?.SolverTimeoutSec ?? 60;

                double focalLengthMm = GuideFocalLengthMm;
                double pixelSizeUm = GuidePixelSizeUm;

                var parameter = NinaPlateSolveParameterFactory.Create(
                    searchRadiusDeg: searchRadiusDeg,
                    downsample: downsample,
                    timeoutSec: timeoutSec,
                    focalLengthMm: focalLengthMm,
                    pixelSizeUm: pixelSizeUm
                );

                var psSettings = profileService.ActiveProfile.PlateSolveSettings;
                var solver = psFactory.GetPlateSolver(psSettings);

                if (updateUi) StatusText = "Solving…";

                var result = await solver.SolveAsync(imageData, parameter, null, CancellationToken.None).ConfigureAwait(false);

                if (!result.Success || result.Coordinates == null) {
                    if (updateUi) {
                        StatusText = "Solve failed ❌";
                        DetailsText = "Plate solver did not return valid coordinates.";
                    }
                    return (false, 0, 0);
                }

                var raHours = GetRightAscensionHours(result.Coordinates);
                var raDeg = raHours * 15.0;
                var decDeg = GetDeclinationDegrees(result.Coordinates);

                if (updateUi) {
                    StatusText = "Solve finished ✅";

                    LastSolveSummary =
                        $"RA={raDeg:0.######}°, Dec={decDeg:0.######}°\n" +
                        $"Frame: {frame.Width}x{frame.Height}, bitDepth={frame.BitDepth}\n" +
                        $"Exposure={exposure:0.###}s, Bin={bin}, Gain={(gain.HasValue ? gain.Value.ToString() : "auto")}\n" +
                        $"SearchRadius={searchRadiusDeg}°, Downsample={downsample}, Timeout={timeoutSec}s\n" +
                        $"FocalLength={focalLengthMm}mm, PixelSize={pixelSizeUm}µm";

                    Context.SetLastGuiderSolve(new GuiderSolveSnapshot(
                        utcTimestamp: DateTime.UtcNow,
                        raDeg: raDeg,
                        decDeg: decDeg,
                        summary: LastSolveSummary
                    ));

                    DetailsText = $"Solved ✅  RA={raDeg:0.######}°, Dec={decDeg:0.######}°";
                }

                return (true, raDeg, decDeg);

            } catch (Exception ex) {
                if (updateUi) {
                    StatusText = "Capture / Solve failed ❌";
                    DetailsText = ex.ToString();
                }
                return (false, 0, 0);
            }
        }

        private int? GetEffectiveGain() {
            var g = GuideGain;
            return g >= 0 ? g : (int?)null;
        }

        // ============================
        // Mount connection + coords
        // ============================
        private void UpdateMountConnectionState() {
            RefreshTelescopeCoordsFromService();
        }

        private void RefreshTelescopeCoordsFromService() {
            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;

                if (TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg) &&
                    IsValidRaDec(raDeg, decDeg)) {

                    SetMountState(
                        MountConnectionState.ConnectedWithCoords,
                        $"RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}");
                    return;
                }
            }

            var connected = DetectMountConnected();
            if (connected) {
                SetMountState(MountConnectionState.ConnectedNoCoords, "Verbunden ⚠️ (Koordinaten noch nicht verfügbar…)");
                return;
            }

            SetMountState(MountConnectionState.Disconnected, string.Empty);
        }

        private bool DetectMountConnected() {
            var tm = TelescopeMediator;
            if (tm == null) return false;

            var info = InvokeGetInfo(tm);
            var c = TryGetProp(info, "Connected");
            if (c is bool b) return b;

            var knownNames = new[] { "IsConnected", "Connected", "DeviceConnected", "IsDeviceConnected", "HasConnection" };

            foreach (var name in knownNames) {
                var v = TryGetProp(tm, name);
                if (v is bool bb) return bb;
            }

            var dev = TryGetProp(tm, "Device") ?? TryGetProp(tm, "Telescope");
            if (dev != null) {
                foreach (var name in knownNames) {
                    var v = TryGetProp(dev, name);
                    if (v is bool bb) return bb;
                }
            }

            return false;
        }

        private static object? InvokeGetInfo(ITelescopeMediator? tm) {
            if (tm == null) return null;
            try {
                var mi = tm.GetType().GetMethod("GetInfo",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                return mi?.Invoke(tm, null);
            } catch {
                return null;
            }
        }

        // ============================
        // Secondary camera device list / connect
        // ============================
        private void RefreshSecondaryCameraListSafe() {
            if (!importsReady) {
                StatusText = "Not ready yet…";
                DetailsText = "MEF imports are not satisfied yet.";
                return;
            }
            RefreshSecondaryCameraList();
        }

        private void RefreshSecondaryCameraList() {
            SecondaryCameraDevices.Clear();

            var cams = AscomDiscovery.GetCameras();
            foreach (var c in cams) SecondaryCameraDevices.Add(c);

            if (SecondaryCameraDevices.Count == 0) {
                SecondaryCameraDevices.Add(new AscomDeviceInfo("ASCOM Simulator Camera (fallback)", FallbackSecondaryCameraProgId));
                DetailsText = "No ASCOM cameras found.\n" + (AscomDiscovery.GetLastError() ?? "n/a");
            } else {
                DetailsText = $"Found {SecondaryCameraDevices.Count} ASCOM cameras.";
            }

            var desired = SelectedSecondaryCameraProgId ?? FallbackSecondaryCameraProgId;
            var match = SecondaryCameraDevices.FirstOrDefault(x =>
                string.Equals(x.ProgId, desired, StringComparison.OrdinalIgnoreCase));

            SelectedSecondaryCamera = match ?? SecondaryCameraDevices.FirstOrDefault();
        }

        private async Task OpenDriverSettingsAsync() {
            if (!importsReady) {
                StatusText = "Not ready yet…";
                DetailsText = "MEF imports are not satisfied yet.";
                return;
            }

            try {
                var progId = SelectedSecondaryCameraProgId ?? FallbackSecondaryCameraProgId;
                Context.SetActiveSecondaryCameraProgId(progId);
                Context.CurrentSecondaryCameraProgId = progId;

                StatusText = "Opening driver settings…";
                DetailsText = $"ProgID: {progId}";

                var ok = await SecondaryCameraService.OpenSetupDialogAsync();

                StatusText = ok ? "Driver settings updated ✅" : "Driver settings not available ⚠️";
                if (!ok) DetailsText = "The selected ASCOM driver did not expose SetupDialog() or opening it failed.";
            } catch (Exception ex) {
                StatusText = "Driver settings failed ❌";
                DetailsText = ex.ToString();
            }
        }

        private async Task ConnectSecondaryAsync() {
            if (!importsReady) {
                StatusText = "Not ready yet…";
                DetailsText = "MEF imports are not satisfied yet.";
                return;
            }

            try {
                var progId = SelectedSecondaryCameraProgId ?? FallbackSecondaryCameraProgId;
                Context.SetActiveSecondaryCameraProgId(progId);
                Context.CurrentSecondaryCameraProgId = progId;

                // Ensure correct cached instance BEFORE connecting
                var svc = EnsureSecondaryCameraService();

                StatusText = "Connecting secondary camera...";
                DetailsText = $"ProgID: {svc.ProgId}";

                await svc.ConnectAsync(CancellationToken.None);

                UpdateConnectionStateFromService();
                StatusText = IsSecondaryConnected ? "Secondary camera connected ✅" : "Secondary camera not connected ❌";

            } catch (Exception ex) {
                StatusText = "Connect failed ❌";
                DetailsText = ex.ToString();
                IsSecondaryConnected = false;
            }
        }

        private async Task DisconnectSecondaryAsync() {
            if (!importsReady) return;

            try {
                StatusText = "Disconnecting secondary camera...";
                await SecondaryCameraService.DisconnectAsync(CancellationToken.None);
                // refresh cached service instance so subsequent calls use the same object
                _ = EnsureSecondaryCameraService();
                UpdateConnectionStateFromService();
                StatusText = "Disconnected ✅";
            } catch (Exception ex) {
                StatusText = "Disconnect failed ❌";
                DetailsText = ex.ToString();
            }
        }

        // ============================
        // Helpers: data conversion + coords parsing
        // ============================
        private static ushort[] ConvertToUShortRowMajor(int[,] pixels, int width, int height) {
            var packed = new ushort[width * height];
            int idx = 0;

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int v = pixels[y, x];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    packed[idx++] = (ushort)v;
                }
            }

            return packed;
        }

        private static BitmapSource CreateGray16Bitmap(ushort[] packed, int width, int height) {
            int stride = width * 2;

            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray16, null);
            wb.WritePixels(new Int32Rect(0, 0, width, height), packed, stride, 0);

            wb.Freeze();
            return wb;
        }

        private static double GetRightAscensionHours(Coordinates c) {
            object? v =
                TryGetProp(c, "RA") ??
                TryGetProp(c, "Ra") ??
                TryGetProp(c, "RightAscension") ??
                TryGetProp(c, "RightAscensionHours") ??
                TryGetProp(c, "RAHours");

            if (v == null) return 0.0;

            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;

            var hours = TryGetDoubleProp(v, "Hours") ?? TryGetDoubleProp(v, "TotalHours");
            if (hours.HasValue) return hours.Value;

            var deg = TryGetDoubleProp(v, "Degrees") ?? TryGetDoubleProp(v, "TotalDegrees");
            if (deg.HasValue) return deg.Value / 15.0;

            return double.TryParse(v.ToString(), out var parsed) ? parsed : 0.0;
        }

        private static double GetDeclinationDegrees(Coordinates c) {
            object? v =
                TryGetProp(c, "Dec") ??
                TryGetProp(c, "DEC") ??
                TryGetProp(c, "Declination") ??
                TryGetProp(c, "Decl");

            if (v == null) return 0.0;

            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;

            var deg = TryGetDoubleProp(v, "Degrees") ?? TryGetDoubleProp(v, "TotalDegrees") ?? TryGetDoubleProp(v, "Value");
            if (deg.HasValue) return deg.Value;

            return double.TryParse(v.ToString(), out var parsed) ? parsed : 0.0;
        }

        private static object? TryGetProp(object? obj, string name) {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return p?.GetValue(obj);
        }

        private static double? TryGetDoubleProp(object obj, string name) {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return null;

            var v = p.GetValue(obj);
            if (v == null) return null;

            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;

            return double.TryParse(v.ToString(), out var parsed) ? parsed : (double?)null;
        }

        private static bool TryCreateAngleFromDegrees(double deg, out Angle angle, out string why) {
            angle = default;
            why = "";

            var t = typeof(Angle);
            var rad = deg * Math.PI / 180.0;

            try {
                var factories = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.ReturnType == t)
                    .Select(m => new { Method = m, Params = m.GetParameters() })
                    .Where(x => x.Params.Length == 1 && x.Params[0].ParameterType == typeof(double))
                    .ToList();

                foreach (var pref in new[] { "deg", "rad", "" }) {
                    foreach (var f in factories) {
                        var name = f.Method.Name.ToLowerInvariant();
                        if (pref.Length > 0 && !name.Contains(pref)) continue;

                        var arg = name.Contains("rad") ? rad : deg;

                        try {
                            angle = (Angle)f.Method.Invoke(null, new object[] { arg })!;
                            return true;
                        } catch {
                        }
                    }
                }

                var ctor1 = t.GetConstructor(new[] { typeof(double) });
                if (ctor1 != null) {
                    try { angle = (Angle)ctor1.Invoke(new object[] { deg }); return true; } catch { }
                    try { angle = (Angle)ctor1.Invoke(new object[] { rad }); return true; } catch { }
                }

                var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                foreach (var c in ctors) {
                    var ps = c.GetParameters();
                    if (ps.Length != 2) continue;
                    if (ps[0].ParameterType != typeof(double)) continue;

                    if (!ps[1].ParameterType.IsEnum) continue;

                    var enumType = ps[1].ParameterType;
                    var enumNames = Enum.GetNames(enumType);

                    var degName = enumNames.FirstOrDefault(n => n.ToLowerInvariant().Contains("deg"));
                    if (degName != null) {
                        try {
                            var unit = Enum.Parse(enumType, degName);
                            angle = (Angle)c.Invoke(new object[] { deg, unit })!;
                            return true;
                        } catch { }
                    }

                    var radName = enumNames.FirstOrDefault(n => n.ToLowerInvariant().Contains("rad"));
                    if (radName != null) {
                        try {
                            var unit = Enum.Parse(enumType, radName);
                            angle = (Angle)c.Invoke(new object[] { rad, unit })!;
                            return true;
                        } catch { }
                    }
                }

                why = "No usable Angle factory/ctor found in this NINA build.";
                return false;

            } catch (Exception ex) {
                why = ex.Message;
                return false;
            }
        }

        private bool TryToCoordinates(double raDeg, double decDeg, out Coordinates coords) {
            coords = default;

            if (!TryCreateAngleFromDegrees(raDeg, out var ra, out var whyRa)) {
                StatusText = "Cannot build Coordinates (Angle API mismatch) ❌";
                DetailsText = $"Angle(RA) creation failed.\n{whyRa}";
                return false;
            }

            if (!TryCreateAngleFromDegrees(decDeg, out var dec, out var whyDec)) {
                StatusText = "Cannot build Coordinates (Angle API mismatch) ❌";
                DetailsText = $"Angle(Dec) creation failed.\n{whyDec}";
                return false;
            }

            try {
                var ctor3 = typeof(Coordinates).GetConstructor(new[] { typeof(Angle), typeof(Angle), typeof(Epoch) });
                if (ctor3 != null) {
                    coords = (Coordinates)ctor3.Invoke(new object[] { ra, dec, Epoch.J2000 })!;
                    return true;
                }

                var ctor2 = typeof(Coordinates).GetConstructor(new[] { typeof(Angle), typeof(Angle) });
                if (ctor2 != null) {
                    coords = (Coordinates)ctor2.Invoke(new object[] { ra, dec })!;
                    return true;
                }

                StatusText = "Cannot build Coordinates (ctor missing) ❌";
                DetailsText = "No Coordinates ctor found for (Angle, Angle[, Epoch]).";
                return false;

            } catch (Exception ex) {
                StatusText = "Cannot build Coordinates ❌";
                DetailsText = ex.ToString();
                return false;
            }
        }

        private static object? TryCreateAngleFromHours(Type angleType, double hours) {
            try {
                var mi = angleType.GetMethod("FromHours", BindingFlags.Public | BindingFlags.Static);
                if (mi != null && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(double)) {
                    return mi.Invoke(null, new object[] { hours });
                }

                var ctor = angleType.GetConstructor(new[] { typeof(double) });
                if (ctor != null) return ctor.Invoke(new object[] { hours });

                return null;
            } catch {
                return null;
            }
        }

        private static async Task<bool> TryInvokeAsync(MethodInfo mi, object target, object?[] args) {
            try {
                var res = mi.Invoke(target, args);

                if (res is Task t) {
                    await t.ConfigureAwait(false);
                    return true;
                }

                return true;
            } catch {
                return false;
            }
        }

        private (bool? connected, bool? parked, bool? tracking, bool? slewing, double? raDeg, double? decDeg) ReadMountSnapshot() {
            object obj = TelescopeMediator!;
            var dev = TryGetProp(obj, "Device") ?? TryGetProp(obj, "Telescope");
            if (dev != null) obj = dev;

            bool? GetBool(string name) => TryGetProp(obj, name) as bool?;
            double? GetDouble(string name) {
                var v = TryGetProp(obj, name);
                if (v is double d) return d;
                if (v is float f) return f;
                if (v is int i) return i;
                return null;
            }

            var connected = GetBool("Connected") ?? GetBool("IsConnected");
            var parked = GetBool("AtPark") ?? GetBool("Parked") ?? GetBool("IsParked");
            var tracking = GetBool("Tracking") ?? GetBool("IsTracking");
            var slewing = GetBool("Slewing") ?? GetBool("IsSlewing");

            var raHours = GetDouble("RightAscension") ?? GetDouble("RA");
            var dec = GetDouble("Declination") ?? GetDouble("Dec");

            double? raDeg = raHours.HasValue ? raHours.Value * 15.0 : (double?)null;
            double? decDeg = dec;

            return (connected, parked, tracking, slewing, raDeg, decDeg);
        }

        // =====================================
        // Center Platesolving
        // =====================================

        private async Task CenterWithSecondaryAsync(double thresholdArcmin, int maxAttempts, CancellationToken ct) {
            UpdateMountConnectionState();

            if (MountState != MountConnectionState.ConnectedWithCoords) {
                StatusText = "Mount not ready ❌";
                DetailsText = "Mount not connected OR coordinates not available.";
                return;
            }

            if (!IsSecondaryConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            var useOffset = HasOffsetSet && PluginSettings?.Settings != null;

            // Centering with secondary camera is only valid when an offset is active AND calibrated
            if (!useOffset) {
                StatusText = "Center not available ❌";
                DetailsText = "Centering requires a calibrated offset (HasOffsetSet).";
                return;
            }

            StatusText = "Centering: initial capture/solve…";
            DetailsText = $"Threshold={thresholdArcmin:0.###} arcmin, MaxAttempts={maxAttempts}";

            var first = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!first.success) return;

            lastGuiderSolveDeg = (first.raDeg, first.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", first.raDeg, first.decDeg);

            var desiredMain = useOffset ? MapGuiderToMain(first.raDeg, first.decDeg) : (first.raDeg, first.decDeg);

            lastCorrectedSolveDeg = useOffset ? desiredMain : (ValueTuple<double, double>?)null;
            CorrectedSolveText = FormatSolvedLine("Desired(main)", desiredMain.raDeg, desiredMain.decDeg);

            for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                ct.ThrowIfCancellationRequested();

                StatusText = $"Centering: attempt {attempt}/{maxAttempts} – solving…";
                DetailsText = $"Desired(main): RA={desiredMain.raDeg:0.######}°, Dec={desiredMain.decDeg:0.######}°";

                var cur = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
                if (!cur.success) return;

                lastGuiderSolveDeg = (cur.raDeg, cur.decDeg);
                LastGuiderSolveText = FormatSolvedLine("Guider", cur.raDeg, cur.decDeg);

                var solvedMain = useOffset ? MapGuiderToMain(cur.raDeg, cur.decDeg) : (cur.raDeg, cur.decDeg);

                var errArcmin = SeparationArcmin(
                    solvedMain.raDeg, solvedMain.decDeg,
                    desiredMain.raDeg, desiredMain.decDeg);

                CorrectedSolveText =
                    $"{FormatSolvedLine("Solved(main)", solvedMain.raDeg, solvedMain.decDeg)}\n" +
                    $"Error: {errArcmin:0.###} arcmin (threshold {thresholdArcmin:0.###})";

                if (errArcmin <= thresholdArcmin) {
                    StatusText = "Centering done ✅";
                    DetailsText = $"Reached threshold: {errArcmin:0.###} arcmin ≤ {thresholdArcmin:0.###}";
                    return;
                }

                bool syncOk = false;
                try {
                    if (!TryToCoordinates(solvedMain.raDeg, solvedMain.decDeg, out var solvedCoords)) return;

                    StatusText = $"Centering: attempt {attempt}/{maxAttempts} – syncing…";
                    DetailsText = $"SyncTo(main): RA={solvedMain.raDeg:0.######}°, Dec={solvedMain.decDeg:0.######}°";

                    syncOk = await TelescopeMediator!.Sync(solvedCoords).ConfigureAwait(false);
                } catch {
                    syncOk = false;
                }

                double slewRa;
                double slewDec;

                if (syncOk) {
                    slewRa = desiredMain.raDeg;
                    slewDec = desiredMain.decDeg;

                    StatusText = $"Centering: attempt {attempt}/{maxAttempts} – slewing…";
                    DetailsText = $"Sync OK. SlewTo(main target): RA={slewRa:0.######}°, Dec={slewDec:0.######}°";
                } else {
                    var deltaRa = NormalizeDeltaDeg(desiredMain.raDeg - solvedMain.raDeg);
                    var deltaDec = desiredMain.decDeg - solvedMain.decDeg;

                    slewRa = NormalizeRaDeg(desiredMain.raDeg + deltaRa);
                    slewDec = Math.Max(-90.0, Math.Min(90.0, desiredMain.decDeg + deltaDec));

                    StatusText = $"Centering: attempt {attempt}/{maxAttempts} – slewing…";
                    DetailsText =
                        $"Sync failed → using offset correction.\n" +
                        $"OffsetΔ: dRA={deltaRa:0.######}°, dDec={deltaDec:0.######}°\n" +
                        $"SlewTo(main): RA={slewRa:0.######}°, Dec={slewDec:0.######}°";
                }

                if (!TryToCoordinates(slewRa, slewDec, out var slewCoords2)) return;

                await TelescopeMediator!.SlewToCoordinatesAsync(slewCoords2, ct).ConfigureAwait(false);
            }

            StatusText = "Centering not reached ⚠️";
            DetailsText = $"Max attempts ({maxAttempts}) reached. Consider increasing threshold or improving offset calibration.";
        }



        // =====================================
        // Slew to Target + Center (NEW command)
        // =====================================

        private async Task SlewToTargetAndCenterAsync() {
            UpdateMountConnectionState();

            if (MountState != MountConnectionState.ConnectedWithCoords) {
                StatusText = "Mount not ready ❌";
                DetailsText = "Mount not connected OR coordinates not available.";
                return;
            }

            if (!IsSecondaryConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            if (!HasOffsetSet) {
                StatusText = "Center not available ❌";
                DetailsText = "Slew to Target + Center requires an active and calibrated offset.";
                return;
            }

            if (!IsValidRaDec(TargetRaDeg, TargetDecDeg)) {
                StatusText = "Invalid target coordinates ❌";
                DetailsText = $"TargetRaDeg={TargetRaDeg:0.######}°, TargetDecDeg={TargetDecDeg:0.######}°";
                return;
            }

            if (TelescopeMediator == null) {
                StatusText = "Cannot slew ❌";
                DetailsText = "TelescopeMediator not available (MEF import failed).";
                return;
            }

            var thr = PluginSettings?.Settings?.CenteringThresholdArcmin ?? 1.0;
            var max = PluginSettings?.Settings?.CenteringMaxAttempts ?? 5;

            // 1) Slew to the target first (coarse slew)
            if (!TryToCoordinates(TargetRaDeg, TargetDecDeg, out var targetCoords)) return;

            StatusText = "Slewing to target…";
            DetailsText = $"Target(main): RA={TargetRaDeg:0.######}°, Dec={TargetDecDeg:0.######}°";

            await TelescopeMediator.SlewToCoordinatesAsync(targetCoords, CancellationToken.None).ConfigureAwait(false);

            // 2) Center loop on the same target (fine centering)
            await CenterOnTargetWithSecondaryAsync(TargetRaDeg, TargetDecDeg, thr, max, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task CenterOnTargetWithSecondaryAsync(double desiredRaDeg, double desiredDecDeg, double thresholdArcmin, int maxAttempts, CancellationToken ct) {
            UpdateMountConnectionState();

            if (MountState != MountConnectionState.ConnectedWithCoords) {
                StatusText = "Mount not ready ❌";
                DetailsText = "Mount not connected OR coordinates not available.";
                return;
            }

            if (!IsSecondaryConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            if (!HasOffsetSet || PluginSettings?.Settings == null) {
                StatusText = "Center not available ❌";
                DetailsText = "Offset not active or not calibrated.";
                return;
            }

            if (!IsValidRaDec(desiredRaDeg, desiredDecDeg)) {
                StatusText = "Invalid desired coordinates ❌";
                DetailsText = $"Desired(main): RA={desiredRaDeg:0.######}°, Dec={desiredDecDeg:0.######}°";
                return;
            }

            StatusText = "Centering on target…";
            DetailsText = $"Desired(main): RA={desiredRaDeg:0.######}°, Dec={desiredDecDeg:0.######}°" +
                          $"Threshold={thresholdArcmin:0.###} arcmin, MaxAttempts={maxAttempts}";

            CorrectedSolveText = FormatSolvedLine("Desired(main)", desiredRaDeg, desiredDecDeg);

            for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                ct.ThrowIfCancellationRequested();

                StatusText = $"Centering(target): attempt {attempt}/{maxAttempts} – solving…";
                DetailsText = $"Desired(main): RA={desiredRaDeg:0.######}°, Dec={desiredDecDeg:0.######}°";

                var cur = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
                if (!cur.success) return;

                lastGuiderSolveDeg = (cur.raDeg, cur.decDeg);
                LastGuiderSolveText = FormatSolvedLine("Guider", cur.raDeg, cur.decDeg);

                // Map guider solve to main via offset
                var solvedMain = MapGuiderToMain(cur.raDeg, cur.decDeg);

                var errArcmin = SeparationArcmin(
                    solvedMain.raDeg, solvedMain.decDeg,
                    desiredRaDeg, desiredDecDeg);

                CorrectedSolveText =
                    $"{FormatSolvedLine("Solved(main)", solvedMain.raDeg, solvedMain.decDeg)}" +
                    $"Error: {errArcmin:0.###} arcmin (threshold {thresholdArcmin:0.###})";

                if (errArcmin <= thresholdArcmin) {
                    StatusText = "Centering done ✅";
                    DetailsText = $"Reached threshold: {errArcmin:0.###} arcmin ≤ {thresholdArcmin:0.###}";
                    return;
                }

                bool syncOk = false;
                try {
                    if (!TryToCoordinates(solvedMain.raDeg, solvedMain.decDeg, out var solvedCoords)) return;

                    StatusText = $"Centering(target): attempt {attempt}/{maxAttempts} – syncing…";
                    DetailsText = $"SyncTo(main): RA={solvedMain.raDeg:0.######}°, Dec={solvedMain.decDeg:0.######}°";

                    syncOk = await TelescopeMediator!.Sync(solvedCoords).ConfigureAwait(false);
                } catch {
                    syncOk = false;
                }

                double slewRa;
                double slewDec;

                if (syncOk) {
                    slewRa = desiredRaDeg;
                    slewDec = desiredDecDeg;

                    StatusText = $"Centering(target): attempt {attempt}/{maxAttempts} – slewing…";
                    DetailsText = $"Sync OK. SlewTo(main target): RA={slewRa:0.######}°, Dec={slewDec:0.######}°";
                } else {
                    // Fallback: still slew to desired (main). Sync failure shouldn't prevent centering.
                    slewRa = desiredRaDeg;
                    slewDec = desiredDecDeg;

                    StatusText = $"Centering(target): attempt {attempt}/{maxAttempts} – slewing…";
                    DetailsText =
                        $"Sync failed → slewing anyway." +
                        $"SlewTo(main target): RA={slewRa:0.######}°, Dec={slewDec:0.######}°";
                }

                if (!TryToCoordinates(slewRa, slewDec, out var slewCoords2)) return;

                await TelescopeMediator!.SlewToCoordinatesAsync(slewCoords2, ct).ConfigureAwait(false);
            }

            StatusText = "Centering not reached ⚠️";
            DetailsText = $"Max attempts ({maxAttempts}) reached. Consider increasing threshold or improving offset calibration.";
        }
        private static double NormalizeDeltaDeg(double deltaDeg) {
            deltaDeg %= 360.0;
            if (deltaDeg >= 180.0) deltaDeg -= 360.0;
            if (deltaDeg < -180.0) deltaDeg += 360.0;
            return deltaDeg;
        }

        private static double NormalizeRaDeg(double raDeg) {
            raDeg %= 360.0;
            if (raDeg < 0) raDeg += 360.0;
            return raDeg;
        }

        private static double SeparationArcmin(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
            var dRa = NormalizeDeltaDeg(ra2Deg - ra1Deg);
            var dDec = dec2Deg - dec1Deg;

            var decMidRad = ((dec1Deg + dec2Deg) * 0.5) * Math.PI / 180.0;

            var x = dRa * Math.Cos(decMidRad);
            var y = dDec;

            var sepDeg = Math.Sqrt(x * x + y * y);
            return sepDeg * 60.0;
        }

        private (double raDeg, double decDeg) MapGuiderToMain(double guiderRaDeg, double guiderDecDeg) {
            if (!HasOffsetSet || PluginSettings?.Settings == null) {
                return (guiderRaDeg, guiderDecDeg);
            }

            var svc = new OffsetService();
            return svc.ApplyToGuiderSolve(PluginSettings.Settings, guiderRaDeg, guiderDecDeg);
        }

        // API Wrapper Triggers
        internal string ApiCaptureOnlyAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () => {
                try { await CaptureOnlyAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }

        internal string ApiCaptureAndSolveAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () => {
                try { await CaptureAndSyncOrSlewAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }


        /// <summary>
        /// API: Capture + Sync (solve on secondary and sync mount; uses corrected coords when offset is enabled).
        /// Runs async (fire-and-forget) and returns a small state string for the REST endpoint.
        /// </summary>
        internal string ApiCaptureAndSyncAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;

            _ = Task.Run(async () => {
                try {
                    // Ensure correct mode
                    UseSlewInsteadOfSync = false;
                    await CaptureAndSyncOrSlewAsync().ConfigureAwait(false);
                } finally {
                    apiBusy = false;
                }
            });

            return "started";
        }

        /// <summary>
        /// API: Capture + Slew (Center using secondary; requires offset enabled+set).
        /// Runs async (fire-and-forget) and returns a small state string for the REST endpoint.
        /// </summary>
        internal string ApiCaptureAndCenterAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;

            _ = Task.Run(async () => {
                try {
                    // Ensure center mode
                    UseSlewInsteadOfSync = true;
                    await CaptureAndSyncOrSlewAsync().ConfigureAwait(false);
                } finally {
                    apiBusy = false;
                }
            });

            return "started";
        }

        /// <summary>
        /// API: Slew to a given main-target (deg) and center using secondary + offset mapping.
        /// Runs async (fire-and-forget) and returns a small state string for the REST endpoint.
        /// </summary>
        internal string ApiSlewToTargetAndCenterAsync(double targetRaDeg, double targetDecDeg) {
            if (apiBusy) return "busy";
            apiBusy = true;

            _ = Task.Run(async () => {
                try {
                    TargetRaDeg = targetRaDeg;
                    TargetDecDeg = targetDecDeg;
                    await SlewToTargetAndCenterAsync().ConfigureAwait(false);
                } finally {
                    apiBusy = false;
                }
            });

            return "started";
        }

        internal string ApiCalibrateOffsetAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () => {
                try { await CalibrateOffsetAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }

        internal object GetApiStatusObject() {
            return new {
                importsReady = importsReady,
                busy = apiBusy,
                mountConnected = IsMountConnected,
                mountState = MountState.ToString(),
                secondaryConnected = IsSecondaryConnected,
                offsetEnabled = OffsetEnabled,
                offsetMode = OffsetMode.ToString(),
                offsetRaArcsec = OffsetRaArcsec,
                offsetDecArcsec = OffsetDecArcsec,
                rotation = new { qw = RotationQw, qx = RotationQx, qy = RotationQy, qz = RotationQz },
                statusText = StatusText,
                detailsText = DetailsText,
                lastSolveSummary = LastSolveSummary,
                lastGuiderSolveText = LastGuiderSolveText,
                correctedSolveText = CorrectedSolveText,
                targetRaDeg = TargetRaDeg,
                targetDecDeg = TargetDecDeg
            };
        }

        internal byte[]? GetLastPreviewAsJpegBytes() {
            if (LastCapturedImage == null) return null;

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(LastCapturedImage));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private readonly IPreviewRenderService previewRenderService = new PreviewRenderService();

        public BitmapSource? SolvedPreviewImage { get; private set; }

        public bool PreviewDebayerEnabled { get; set; } = true;
        public bool PreviewAutoStretchEnabled { get; set; } = true;
        public bool PreviewUnlinkedStretchEnabled { get; set; } = false;

        private void EnsureApiHostState() {

            bool enabled;
            int port;
            bool requireToken;
            string? token;

            if (apiStateInitialized) {
                enabled = apiEnabledState;
                port = apiPortState;
                requireToken = apiRequireTokenState;
                token = apiTokenState;
            } else {
                var s = PluginSettings?.Settings;
                enabled = s?.ApiEnabled ?? false;
                port = s?.ApiPort ?? 1899;
                requireToken = s?.ApiRequireToken ?? false;
                token = s?.ApiToken;
            }

            Logger.Info(enabled
            ? "[PlateSolvePlus] API HOST START"
            : "[PlateSolvePlus] API HOST STOP");

            Logger.Info($"[PlateSolvePlus] EnsureApiHostState ENTER apiHostNull={(apiHost == null)} enabled={enabled} port={port}");

            if (!enabled) {
                Logger.Info("[PlateSolvePlus] EnsureApiHostState -> DISABLE path (disposing)");
                apiHost?.Dispose();
                apiHost = null;
                return;
            }

            if (apiHost == null) {
                Logger.Info("[PlateSolvePlus] EnsureApiHostState -> CREATE host");
                apiHost = new PlateSolvePlusApiHost(this, port, true, requireToken, token);
                Logger.Info($"[PlateSolvePlus] EnsureApiHostState -> CREATED apiHostNull={(apiHost == null)}");
                apiHost.Start();
                Logger.Info("[PlateSolvePlus] EnsureApiHostState -> START called");
                return;
            }

            Logger.Info("[PlateSolvePlus] EnsureApiHostState -> already running (maybe restart check)");
        }

#if DEBUG
        private static void DebugDumpSyncSlewMethods(string label, object obj) {
            try {
                Logger.Debug($"===== Sync/Slew methods dump ({label}) :: {obj.GetType().FullName} =====");

                var ms = obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m =>
                        m.Name.IndexOf("sync", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("slew", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(m => m.Name)
                    .ToArray();

                foreach (var m in ms) {
                    var ps = m.GetParameters();
                    var sig = string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Logger.Debug($"  {m.Name}({sig}) : {m.ReturnType.Name}");
                }

                Logger.Debug("============================================================");
            } catch (Exception ex) {
                Logger.Debug($"[DebugDumpSyncSlewMethods] failed: {ex}");
            }
        }

        private async Task<bool> VerifyMountMovementAsync() {
            try {
                var a1 = ReadMountSnapshot();
                await Task.Delay(1500).ConfigureAwait(false);
                var a2 = ReadMountSnapshot();

                if (a2.slewing == true) return true;

                if (a1.raDeg.HasValue && a2.raDeg.HasValue && Math.Abs(a2.raDeg.Value - a1.raDeg.Value) > 0.01) return true;
                if (a1.decDeg.HasValue && a2.decDeg.HasValue && Math.Abs(a2.decDeg.Value - a1.decDeg.Value) > 0.01) return true;

                return false;
            } catch {
                return false;
            }
        }
#endif
    }
}
