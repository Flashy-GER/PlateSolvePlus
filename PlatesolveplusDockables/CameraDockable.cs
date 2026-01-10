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

        // Cache für letzte laufende Config (damit wir nicht dauernd restarten)
        private bool lastApiEnabled;
        private int lastApiPort;
        private bool lastApiRequireToken;
        private string? lastApiToken;


        private IAscomDeviceDiscoveryService AscomDiscovery => Context.AscomDiscovery;
        private ISecondaryCameraService SecondaryCameraService => Context.GetActiveSecondaryCameraService();

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

                UpdateConnectionStateFromService();
            }
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

        public bool CanCaptureAndSolve =>
            importsReady &&
            IsMountConnected &&
            IsSecondaryConnected;

        private bool HasOffsetSet {
            get {
                if (!OffsetEnabled) return false;

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

        public bool CanCalibrateOffset =>
            importsReady &&
            IsMountConnected &&
            IsSecondaryConnected &&
            PluginSettings?.Settings != null &&
            !HasOffsetSet;

        private void RaiseCaptureCalibrateUiState() {
            RaisePropertyChanged(nameof(CanCaptureAndSolve));
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

        private bool isMountConnected;
        public bool IsMountConnected {
            get => isMountConnected;
            private set {
                if (isMountConnected == value) return;
                isMountConnected = value;
                RaisePropertyChanged(nameof(IsMountConnected));
                RaisePropertyChanged(nameof(MountStatusText));
                RaisePropertyChanged(nameof(MountDetailsText));
                RaiseCaptureCalibrateUiState();
            }
        }

        public string MountStatusText => IsMountConnected ? "Mount: Verbunden ✅" : "Mount: Nicht verbunden ❌";

        private string mountDetailsText = string.Empty;
        public string MountDetailsText {
            get => mountDetailsText;
            private set {
                if (mountDetailsText == value) return;
                mountDetailsText = value;
                RaisePropertyChanged(nameof(MountDetailsText));
            }
        }

        public string OffsetStatusText {
            get {
                if (!OffsetEnabled) return "Offset disabled";

                if (OffsetMode == OffsetMode.Rotation) {
                    return Math.Abs(RotationQw - 1.0) > 1e-6
                        ? $"Rotation offset active (qw={RotationQw:0.###})"
                        : "Rotation offset not set";
                }

                return (Math.Abs(OffsetRaArcsec) + Math.Abs(OffsetDecArcsec)) > 0
                    ? $"Arcsec offset active (ΔRA={OffsetRaArcsec:0.###}\", ΔDec={OffsetDecArcsec:0.###}\")"
                    : "Arcsec offset not set";
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
                UpdateConnectionStateFromService();

                HookPluginSettings();
                HookSettingsBus();

                WireTelescopeReferenceService();

                StartMountPoll();
                UpdateMountConnectionState();

                ApplySettings(ReadSettingsFromPluginInstance(), force: true);
                UpdateMountConnectionState();

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
            if (e.Success && e.RaDeg.HasValue && e.DecDeg.HasValue) {
                IsMountConnected = true;
                MountDetailsText = $"RA {AstroFormat.FormatRaHms(e.RaDeg.Value)} / Dec {AstroFormat.FormatDecDms(e.DecDeg.Value)}";
                return;
            }

            var connected = DetectMountConnected();
            IsMountConnected = connected;
            MountDetailsText = connected ? "Verbunden ✅ (Koordinaten noch nicht verfügbar…)" : string.Empty;
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
                : "Corrected: (Offset disabled or not set) → using guider solve as-is.";
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
                e.PropertyName == nameof(PlateSolvePlusSettings.ApiToken)){
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

        /// <summary>
        /// "Capture" (without sync/slew):
        /// - capture + solve guider
        /// - if offset is deleted/not present -> set NEW offset (using mount RA/Dec as main ref)
        /// - update texts
        /// </summary>
        private async Task CaptureOnlyAsync() {
            UpdateMountConnectionState();

            var solve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!solve.success) return;

            // remember guider solve
            lastGuiderSolveDeg = (solve.raDeg, solve.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", solve.raDeg, solve.decDeg);

            // If offset deleted / not present -> set NEW offset
            if (!HasOffsetSet) {
                await AutoSetOffsetIfMissingAsync(solve.raDeg, solve.decDeg).ConfigureAwait(false);
            }

            // corrected preview
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(solve.raDeg, solve.decDeg);
            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (Offset disabled or not set) → using guider solve as-is.";
        }

        /// <summary>
        /// "Capture + Sync" / "Capture + Slew":
        /// - capture + solve guider
        /// - apply offset (only if enabled AND set)
        /// - sync/slew to corrected coords
        /// - update texts
        /// </summary>
        private async Task CaptureAndSyncOrSlewAsync() {
            UpdateMountConnectionState();

            var solve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!solve.success) return;

            // compute corrected (only if enabled + set)
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(solve.raDeg, solve.decDeg);
            var target = lastCorrectedSolveDeg ?? (solve.raDeg, solve.decDeg);

            // ab hier NUR target verwenden, nicht "solve" außerhalb dieses Scopes verschieben

            if (!TryToCoordinates(target.raDeg, target.decDeg, out var targetCoords)) {
                return;
            }

            if (UseSlewInsteadOfSync) {
                // Capture + Slew = Centering loop like NINA (arcmin)
                var thr = PluginSettings?.Settings?.CenteringThresholdArcmin ?? 1.0;
                var max = PluginSettings?.Settings?.CenteringMaxAttempts ?? 5;

                await CenterWithSecondaryAsync(thr, max, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }


        private async Task AutoSetOffsetIfMissingAsync(double guiderRaDeg, double guiderDecDeg) {
            // need plugin settings to persist
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

            if (!TelescopeReferenceService.TryGetCurrentRaDec(out var mainRaDeg, out var mainDecDeg)) {
                StatusText = "Cannot set offset ❌";
                DetailsText = "Mount RA/Dec not available (TelescopeReferenceService).";
                return;
            }

            try {
                var svc = new OffsetService();

                // respects current Settings.OffsetMode (Rotation / Arcsec) inside OffsetService
                svc.Calibrate(PluginSettings.Settings, mainRaDeg, mainDecDeg, guiderRaDeg, guiderDecDeg);

                PluginSettings.Settings.OffsetEnabled = true;
                PluginSettings.Settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

                // mirror immediately
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
            if (!OffsetEnabled) return null;
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

        // ============================
        // Existing: manual Calibrate Offset (still valid)
        // ============================
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
            if (!IsMountConnected) {
                StatusText = "Mount not connected ❌";
                DetailsText = "Connect the telescope/mount in NINA first.";
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

            if (!TelescopeReferenceService.TryGetCurrentRaDec(out var mainRaDeg, out var mainDecDeg)) {
                StatusText = "Cannot read mount coordinates ❌";
                DetailsText = "Mount connected, but RA/Dec not available via TelescopeReferenceService.";
                return;
            }

            StatusText = "Calibrating offset…";
            DetailsText = "Capturing + solving guider frame.";

            var guiderSolve = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!guiderSolve.success) return;

            // remember guider solve
            lastGuiderSolveDeg = (guiderSolve.raDeg, guiderSolve.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", guiderSolve.raDeg, guiderSolve.decDeg);

            var svc = new OffsetService();
            svc.Calibrate(PluginSettings.Settings, mainRaDeg, mainDecDeg, guiderSolve.raDeg, guiderSolve.decDeg);

            PluginSettings.Settings.OffsetEnabled = true;
            PluginSettings.Settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

            StatusText = "Offset calibrated ✅";
            DetailsText =
                $"Main RA/Dec: {mainRaDeg:0.######}°, {mainDecDeg:0.######}°\n" +
                $"Guider RA/Dec: {guiderSolve.raDeg:0.######}°, {guiderSolve.decDeg:0.######}°\n" +
                $"Mode: {PluginSettings.Settings.OffsetMode}";

            ApplySettings(ReadSettingsFromPluginInstance(), force: false);

            // corrected preview
            lastCorrectedSolveDeg = ComputeCorrectedIfEnabled(guiderSolve.raDeg, guiderSolve.decDeg);
            CorrectedSolveText = lastCorrectedSolveDeg.HasValue
                ? FormatSolvedLine("Corrected", lastCorrectedSolveDeg.Value.raDeg, lastCorrectedSolveDeg.Value.decDeg)
                : "Corrected: (Offset disabled or not set) → using guider solve as-is.";
        }

        // ============================
        // Capture + Solve (guider)
        // ============================
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
                    LastCapturedImage = CreateGray16Bitmap(packed, frame.Width, frame.Height);
                    StatusText = "Building IImageData…";
                }

                var imageData = imgFactory.CreateBaseImageData(
                    input: packed,
                    width: frame.Width,
                    height: frame.Height,
                    bitDepth: frame.BitDepth,
                    isBayered: false,
                    metaData: new ImageMetaData());

                // IMPORTANT: we now use your plugin Settings from Options as source of truth
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

                // Solver selection comes from active profile plate solve settings (NINA internal)
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
            var connected = DetectMountConnected();
            IsMountConnected = connected;

            if (!connected) {
                MountDetailsText = string.Empty;
                return;
            }

            try {
                MountDetailsText = (TelescopeReferenceService != null &&
                                   TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg))
                    ? $"RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}"
                    : "Verbunden ✅ (Koordinaten noch nicht verfügbar…)";
            } catch {
                MountDetailsText = "Verbunden ✅";
            }
        }

        private void RefreshTelescopeCoordsFromService() {
            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;

                if (TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg)) {
                    IsMountConnected = true;
                    MountDetailsText = $"RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}";
                    return;
                }
            }

            var connected = DetectMountConnected();
            IsMountConnected = connected;
            MountDetailsText = connected ? "Verbunden ✅ (Koordinaten noch nicht verfügbar…)" : string.Empty;
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

                StatusText = "Connecting secondary camera...";
                DetailsText = $"ProgID: {SecondaryCameraService.ProgId}";

                await SecondaryCameraService.ConnectAsync(CancellationToken.None);
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
                // 1) Any public static method returning Angle with one double parameter
                var factories = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.ReturnType == t)
                    .Select(m => new { Method = m, Params = m.GetParameters() })
                    .Where(x => x.Params.Length == 1 && x.Params[0].ParameterType == typeof(double))
                    .ToList();

                // Prefer methods with "deg", then "rad", then anything
                foreach (var pref in new[] { "deg", "rad", "" }) {
                    foreach (var f in factories) {
                        var name = f.Method.Name.ToLowerInvariant();
                        if (pref.Length > 0 && !name.Contains(pref)) continue;

                        var arg = name.Contains("rad") ? rad : deg;

                        try {
                            angle = (Angle)f.Method.Invoke(null, new object[] { arg })!;
                            return true;
                        } catch {
                            // keep trying
                        }
                    }
                }

                // 2) ctor(double)
                var ctor1 = t.GetConstructor(new[] { typeof(double) });
                if (ctor1 != null) {
                    // try degrees first
                    try { angle = (Angle)ctor1.Invoke(new object[] { deg }); return true; } catch { }
                    // then radians
                    try { angle = (Angle)ctor1.Invoke(new object[] { rad }); return true; } catch { }
                }

                // 3) ctor(double, enum unit)
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

        /// <summary>
        /// Safe Coordinates builder (never throws).
        /// </summary>
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
                // Try (Angle, Angle, Epoch)
                var ctor3 = typeof(Coordinates).GetConstructor(new[] { typeof(Angle), typeof(Angle), typeof(Epoch) });
                if (ctor3 != null) {
                    coords = (Coordinates)ctor3.Invoke(new object[] { ra, dec, Epoch.J2000 })!;
                    return true;
                }

                // Fallback: (Angle, Angle)
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
                // look for static FromHours(double)
                var mi = angleType.GetMethod("FromHours", BindingFlags.Public | BindingFlags.Static);
                if (mi != null && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(double)) {
                    return mi.Invoke(null, new object[] { hours });
                }

                // try ctor(double) (assuming hours)
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

                // non-task method invoked successfully
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

            // typical ASCOM-ish names:
            var connected = GetBool("Connected") ?? GetBool("IsConnected");
            var parked = GetBool("AtPark") ?? GetBool("Parked") ?? GetBool("IsParked");
            var tracking = GetBool("Tracking") ?? GetBool("IsTracking");
            var slewing = GetBool("Slewing") ?? GetBool("IsSlewing");

            // many drivers expose RA in hours and Dec in degrees
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

            if (!IsMountConnected) {
                StatusText = "Mount not connected ❌";
                DetailsText = "Connect the telescope/mount in NINA first.";
                return;
            }

            if (!IsSecondaryConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            var useOffset = OffsetEnabled && HasOffsetSet && PluginSettings?.Settings != null;

            // 1) First solve defines the "desired" sky position (guider sky -> desired main sky)
            StatusText = "Centering: initial capture/solve…";
            DetailsText = $"Threshold={thresholdArcmin:0.###} arcmin, MaxAttempts={maxAttempts}";

            var first = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
            if (!first.success) return;

            lastGuiderSolveDeg = (first.raDeg, first.decDeg);
            LastGuiderSolveText = FormatSolvedLine("Guider", first.raDeg, first.decDeg);

            var desiredMain = useOffset ? MapGuiderToMain(first.raDeg, first.decDeg) : (first.raDeg, first.decDeg);

            lastCorrectedSolveDeg = useOffset ? desiredMain : (ValueTuple<double, double>?)null;
            CorrectedSolveText = FormatSolvedLine("Desired(main)", desiredMain.raDeg, desiredMain.decDeg);

            // 2) Iterative loop like NINA CenteringSolver
            for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                ct.ThrowIfCancellationRequested();

                StatusText = $"Centering: attempt {attempt}/{maxAttempts} – solving…";
                DetailsText = $"Desired(main): RA={desiredMain.raDeg:0.######}°, Dec={desiredMain.decDeg:0.######}°";

                var cur = await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
                if (!cur.success) return;

                lastGuiderSolveDeg = (cur.raDeg, cur.decDeg);
                LastGuiderSolveText = FormatSolvedLine("Guider", cur.raDeg, cur.decDeg);

                // Estimate current MAIN sky using guider solve + offset model (or passthrough)
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

                // --- NINA-style: try Sync to solved position; if Sync fails, compute offset and apply to target ---
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
                    // If sync worked: slew directly to desired target
                    slewRa = desiredMain.raDeg;
                    slewDec = desiredMain.decDeg;

                    StatusText = $"Centering: attempt {attempt}/{maxAttempts} – slewing…";
                    DetailsText = $"Sync OK. SlewTo(main target): RA={slewRa:0.######}°, Dec={slewDec:0.######}°";
                } else {
                    // Sync failed: compute pointing offset like CenteringSolver and slew to (target + offset)
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

        // Platesolve Center Helper

        private static double NormalizeDeltaDeg(double deltaDeg) {
            // wrap to [-180, +180)
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
            // small-angle approx (good for centering errors)
            var dRa = NormalizeDeltaDeg(ra2Deg - ra1Deg);
            var dDec = dec2Deg - dec1Deg;

            var decMidRad = ((dec1Deg + dec2Deg) * 0.5) * Math.PI / 180.0;

            var x = dRa * Math.Cos(decMidRad);
            var y = dDec;

            var sepDeg = Math.Sqrt(x * x + y * y);
            return sepDeg * 60.0;
        }

        private (double raDeg, double decDeg) MapGuiderToMain(double guiderRaDeg, double guiderDecDeg) {
            // If offset enabled+set => convert guider-sky coords to main-sky coords
            // else passthrough (useful for testing without offset)
            if (!OffsetEnabled || !HasOffsetSet || PluginSettings?.Settings == null) {
                return (guiderRaDeg, guiderDecDeg);
            }

            var svc = new OffsetService();
            return svc.ApplyToGuiderSolve(PluginSettings.Settings, guiderRaDeg, guiderDecDeg);
        }

        // API Wrapper Triggers
        internal string ApiCaptureOnlyAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () =>
            {
                try { await CaptureOnlyAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }

        internal string ApiCaptureAndSolveAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () =>
            {
                try { await CaptureAndSyncOrSlewAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }

        // Web API Status and Preview for REST)
        internal string ApiCalibrateOffsetAsync() {
            if (apiBusy) return "busy";
            apiBusy = true;
            _ = Task.Run(async () =>
            {
                try { await CalibrateOffsetAsync().ConfigureAwait(false); } finally { apiBusy = false; }
            });
            return "started";
        }

        internal object GetApiStatusObject() {
            return new {
                importsReady = importsReady,
                busy = apiBusy,
                mountConnected = IsMountConnected,
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
                correctedSolveText = CorrectedSolveText
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
           // Logger.Info($"[PlateSolvePlus] EnsureApiHostState: apiHostNull={(apiHost == null)} enabled={enabled} port={port}");
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
                // Snapshot 1
                var a1 = ReadMountSnapshot();

                // wait a bit
                await Task.Delay(1500).ConfigureAwait(false);

                // Snapshot 2
                var a2 = ReadMountSnapshot();

                // movement heuristics: Slewing flag OR coords changed noticeably
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
