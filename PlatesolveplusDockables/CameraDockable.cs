using Accord;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Plugins.PlateSolvePlus.PlateSolving;
using NINA.Plugins.PlateSolvePlus.Services;
using NINA.Plugins.PlateSolvePlus.Utils;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Text;
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

            // Ensure timer is created on UI dispatcher
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            mountPollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) {
                Interval = MountPollInterval
            };

            mountPollTimer.Tick += (_, __) => {
                try {
                    
                    RefreshTelescopeCoordsFromService();
                } catch (Exception ex) {
                    // polling must never crash the UI
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

        private int? GetEffectiveGain() {
            var g = GuideGain;
            return g >= 0 ? g : (int?)null;
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

        // ===== Commands =====
        public ICommand RefreshSecondaryCameraListCommand { get; }
        public ICommand OpenDriverSettingsCommand { get; }
        public ICommand ConnectSecondaryCommand { get; }
        public ICommand DisconnectSecondaryCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand CalibrateOffsetCommand { get; }

        // ===== View bindings (CameraDockableView.xaml expects these names) =====
        // Keep the existing internal command names, but expose aliases that match the XAML bindings.
        public ICommand CaptureAndSolveCommand => CaptureCommand;

        private bool useSlewInsteadOfSync;
        public bool UseSlewInsteadOfSync {
            get => useSlewInsteadOfSync;
            set {
                if (useSlewInsteadOfSync == value) return;
                useSlewInsteadOfSync = value;
                RaisePropertyChanged(nameof(UseSlewInsteadOfSync));
                RaiseCaptureCalibrateUiState(); // Text + IsEnabled aktualisieren
            }
        }

        public string CaptureAndSolveButtonText =>
            UseSlewInsteadOfSync ? "Capture + Slew" : "Capture + Sync";

        public bool CanCaptureAndSolve =>
            importsReady &&
            IsMountConnected &&
            IsSecondaryConnected;

        private bool HasOffsetSet {
            get {
                // „Offset gesetzt“ wenn enabled UND irgendein Wert wirklich ungleich default ist
                if (!OffsetEnabled) return false;

                if (OffsetMode == OffsetMode.Rotation) {
                    // Identity quaternion ~ kein Offset
                    var isIdentity = Math.Abs(RotationQw - 1.0) < 1e-6 &&
                                     Math.Abs(RotationQx) < 1e-6 &&
                                     Math.Abs(RotationQy) < 1e-6 &&
                                     Math.Abs(RotationQz) < 1e-6;
                    return !isIdentity;
                }

                // Arcsec mode
                return Math.Abs(OffsetRaArcsec) > 1e-6 || Math.Abs(OffsetDecArcsec) > 1e-6;
            }
        }

        public bool CanCalibrateOffset =>
            importsReady &&
            IsMountConnected &&
            IsSecondaryConnected &&
            PluginSettings?.Settings != null &&
            !HasOffsetSet; // nur wenn noch kein Offset gesetzt


        private void RaiseCaptureCalibrateUiState() {
            RaisePropertyChanged(nameof(CanCaptureAndSolve));
            RaisePropertyChanged(nameof(CanCalibrateOffset));
            RaisePropertyChanged(nameof(CaptureAndSolveButtonText));
            RaisePropertyChanged(nameof(CaptureAndSolveCommand)); // optional, aber harmlos
        }

        private void RaiseActionCanExecuteChanged() {
            RaisePropertyChanged(nameof(CanCaptureAndSolve));
            RaisePropertyChanged(nameof(CanCalibrateOffset));
            RaisePropertyChanged(nameof(CaptureAndSolveButtonText));

        }

        // ===== UI state =====
        private bool isSecondaryConnected;
        public bool IsSecondaryConnected {
            get => isSecondaryConnected;
            set { isSecondaryConnected = value; RaisePropertyChanged(nameof(IsSecondaryConnected));
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

            CaptureCommand = new SimpleAsyncCommand(CaptureAndSolveAsync);
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
                Logger.Debug(
                    $"[CameraDockable] OnImportsSatisfied | " +
                    $"TRS instance={TelescopeReferenceService?.GetHashCode()} | " +
                    $"Mediator={TelescopeReferenceService?.TelescopeMediator?.GetHashCode()} | " +
                    $"Dockable this={this.GetHashCode()}"
                 );

                StartMountPoll();
                UpdateMountConnectionState();

                // initial sync (from plugin settings instance)
                ApplySettings(ReadSettingsFromPluginInstance(), force: true);
                UpdateMountConnectionState();
            } catch (Exception ex) {
                StatusText = "Initialization failed ❌";
                DetailsText = ex.ToString();
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;

            UnhookPluginSettings();
            UnhookSettingsBus();
            UnwireTelescopeReferenceService();
            StopMountPoll();

            this.profileService.ProfileChanged -= ProfileService_ProfileChanged;
        }

        private void ProfileService_ProfileChanged(object? sender, EventArgs e) {
            ApplySettings(ReadSettingsFromPluginInstance(), force: true);

            // In case the mediator instance changed with profile/equipment changes
            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;
            }

            UpdateMountConnectionState();
            DebugDumpMountBoolProperties("After ProfileChanged");

        }

        private void HookPluginSettings() {
            if (pluginSettingsHooked) return;
            if (PluginSettings == null) return;

            PluginSettings.PropertyChanged += PluginSettings_PropertyChanged;
            pluginSettingsHooked = true;
        }

        private void UnhookPluginSettings() {
            if (!pluginSettingsHooked) return;
            if (PluginSettings != null) {
                PluginSettings.PropertyChanged -= PluginSettings_PropertyChanged;
            }
            pluginSettingsHooked = false;
        }

        private void HookSettingsBus() {
            if (busHooked) return;
            PlateSolvePlusSettingsBus.SettingChanged += SettingsBus_SettingChanged;
            busHooked = true;
        }

        private void UnhookSettingsBus() {
            if (!busHooked) return;
            PlateSolvePlusSettingsBus.SettingChanged -= SettingsBus_SettingChanged;
            busHooked = false;
        }

        private void WireTelescopeReferenceService() {
            if (telescopeReferenceHooked) return;

            // Ensure our reference service uses the current mediator
            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;
                TelescopeReferenceService.ReferenceUpdated += TelescopeReferenceService_ReferenceUpdated;
                telescopeReferenceHooked = true;
            }

            // Initial refresh (also covers the case where service is null)
            UpdateMountConnectionState();
            DebugDumpMountBoolProperties("After Connection");

        }

        private void UnwireTelescopeReferenceService() {
            if (!telescopeReferenceHooked) return;

            try {
                if (TelescopeReferenceService != null) {
                    TelescopeReferenceService.ReferenceUpdated -= TelescopeReferenceService_ReferenceUpdated;
                    TelescopeReferenceService.TelescopeMediator = null;
                }
            } catch {
                // ignore
            }

            telescopeReferenceHooked = false;
        }

        private void TelescopeReferenceService_ReferenceUpdated(object? sender, TelescopeReferenceUpdatedEventArgs e) {
            try {
                // We want this to reflect in UI quickly even if fired from background context.
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess()) {
                    Application.Current.Dispatcher.InvokeAsync(() => ApplyTelescopeReferenceUpdate(e));
                } else {
                    ApplyTelescopeReferenceUpdate(e);
                }
            } catch {
                // ignore
            }
        }

        private void SettingsBus_SettingChanged(object? sender, PlateSolvePlusSettingChangedEventArgs e) {
            // UI thread safe
            try {
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess()) {
                    Application.Current.Dispatcher.InvokeAsync(() => ApplySingleFromBus(e.Key, e.Value));
                } else {
                    ApplySingleFromBus(e.Key, e.Value);
                }
            } catch {
                // ignore
            }
        }

        private void ApplySingleFromBus(string key, object? value) {
            switch (key) {

                // Capture / optics
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

                // Offset / rotation
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
                    // Bus kann DateTime oder null schicken
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

                default:
                    break;
            }
        }


        private void PluginSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
            ApplySettings(ReadSettingsFromPluginInstance(), force: false);
        }


        // ============================
        // Read + Apply (ONLY from plugin instance)
        // ============================

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
        }

        // ============================
        // Calibrate Offset -> writes directly into Options (PluginSettings.Settings)
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

            // Prefer TelescopeReferenceService (same path as PlatesolveplusDockable)
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
            if (!guiderSolve.success) {
                return; // helper sets UI already
            }

            // Compute + write into Options (Settings is the source of truth)
            var svc = new OffsetService();
            svc.Calibrate(PluginSettings.Settings, mainRaDeg, mainDecDeg, guiderSolve.raDeg, guiderSolve.decDeg);

            PluginSettings.Settings.OffsetEnabled = true;
            PluginSettings.Settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

            StatusText = "Offset calibrated ✅";
            DetailsText =
                $"Main RA/Dec: {mainRaDeg:0.######}°, {mainDecDeg:0.######}°\n" +
                $"Guider RA/Dec: {guiderSolve.raDeg:0.######}°, {guiderSolve.decDeg:0.######}°\n" +
                $"Mode: {PluginSettings.Settings.OffsetMode}";

            // Mirror UI immediately (bus will also follow)
            ApplySettings(ReadSettingsFromPluginInstance(), force: false);
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

                var psSettings = profileService.ActiveProfile.PlateSolveSettings;

                double searchRadiusDeg = ReadDouble(psSettings, "SearchRadius", "SearchRadiusDeg", "Radius", "RadiusDeg") ?? 5.0;
                int downsample = ReadInt(psSettings, "Downsample", "DownSample", "DownSampling", "DownSamplingFactor") ?? 2;
                int timeoutSec = ReadInt(psSettings, "Timeout", "TimeoutSeconds", "SolveTimeout", "SolveTimeoutSeconds") ?? 60;

                double focalLengthMm = GuideFocalLengthMm;
                double pixelSizeUm = GuidePixelSizeUm;

                var parameter = NinaPlateSolveParameterFactory.Create(
                    searchRadiusDeg: searchRadiusDeg,
                    downsample: downsample,
                    timeoutSec: timeoutSec,
                    focalLengthMm: focalLengthMm,
                    pixelSizeUm: pixelSizeUm
                );

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

        private void UpdateMountConnectionState() {
            Logger.Debug(
                $"[CameraDockable] UpdateMountConnectionState | " +
                $"TRS instance={TelescopeReferenceService?.GetHashCode()} | " +
                $"Mediator={TelescopeReferenceService?.TelescopeMediator?.GetHashCode()} | " +
                $"LocalMediator={TelescopeMediator?.GetHashCode()}"
             );

            var connected = DetectMountConnected();
            IsMountConnected = connected;
            //  DebugDumpMountRaDecFromInfo();

            if (!connected) {
                MountDetailsText = string.Empty;
                return;
            }

            // Best effort: show current coordinates (helps troubleshooting)
            try {
                MountDetailsText = (TelescopeReferenceService != null &&
                                   TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg))
                    ? $"RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}"
                    : "Verbunden ✅ (Koordinaten noch nicht verfügbar…)";
            } catch {

                MountDetailsText = "Verbunden ✅)";

            }
        }

        private void RefreshTelescopeCoordsFromService() {
            // wichtig: Mediator bei jedem Refresh sicher setzen (Mount kann im UI gewechselt werden)
            if (TelescopeReferenceService != null) {
                TelescopeReferenceService.TelescopeMediator = TelescopeMediator;

                if (TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg)) {
                    // direkt in UI übernehmen
                    IsMountConnected = true;
                    MountDetailsText = $"RA {AstroFormat.FormatRaHms(raDeg)} / Dec {AstroFormat.FormatDecDms(decDeg)}";
                    return;
                }
            }

            // Koordinaten nicht lesbar → aber Connection über GetInfo().Connected bestimmen
            var connected = DetectMountConnected();
            IsMountConnected = connected;
            MountDetailsText = connected ? "Verbunden ✅ (Koordinaten noch nicht verfügbar…)" : string.Empty;
        }

        private bool DetectMountConnected() {
            var tm = TelescopeMediator;
            if (tm == null) return false;

            // 1) PRIMARY: NINA liefert bei dir Connected über GetInfo()
            var info = InvokeGetInfo(tm);
            var c = TryGetProp(info, "Connected");
            if (c is bool b) return b;

            // 2) Fallbacks (nur falls GetInfo mal nichts liefert)
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
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                return mi?.Invoke(tm, null);
            } catch {
                return null;
            }
        }

        private bool TryGetMountRaDecDeg(out double raDeg, out double decDeg, out string error) {
            raDeg = 0;
            decDeg = 0;
            error = "";

            if (TelescopeMediator == null) {
                error = "TelescopeMediator is null (not imported).";
                return false;
            }

            var coordsObj =
                TryGetProp(TelescopeMediator, "Coordinates") ??
                TryGetProp(TelescopeMediator, "TelescopeCoordinates") ??
                TryGetProp(TelescopeMediator, "CurrentCoordinates");

            if (coordsObj is Coordinates coords) {
                raDeg = GetRightAscensionHours(coords) * 15.0;
                decDeg = GetDeclinationDegrees(coords);
                return true;
            }

            var ra = TryGetProp(TelescopeMediator, "RightAscension") ?? TryGetProp(TelescopeMediator, "RA");
            var dec = TryGetProp(TelescopeMediator, "Declination") ?? TryGetProp(TelescopeMediator, "Dec");

            var raHours = ra != null ? TryToDouble(ra) : (double?)null;
            var decDegVal = dec != null ? TryToDouble(dec) : (double?)null;

            if (raHours.HasValue && decDegVal.HasValue) {
                raDeg = raHours.Value * 15.0;
                decDeg = decDegVal.Value;
                return true;
            }

            error = "Could not read mount coordinates via TelescopeMediator (Coordinates/RA/Dec not found).";
            return false;
        }

        private static double? TryToDouble(object v) {
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (double.TryParse(v.ToString(), out parsed)) return parsed;
            return null;
        }

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

        private async Task CaptureAndSolveAsync() {
            UpdateMountConnectionState();
            await CaptureAndSolveGuiderAsync(updateUi: true).ConfigureAwait(false);
        }

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

        private static double? ReadDouble(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (double.TryParse(v.ToString(), out var d)) return d;
            }
            return null;
        }

        private static int? ReadInt(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (int.TryParse(v.ToString(), out var i)) return i;
            }
            return null;
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

        private static object? TryGetProp(object obj, string name) {
            var p = obj.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            return p?.GetValue(obj);
        }

        private static double? TryGetDoubleProp(object obj, string name) {
            var p = obj.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (p == null) return null;

            var v = p.GetValue(obj);
            if (v == null) return null;

            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;

            return double.TryParse(v.ToString(), out var parsed) ? parsed : (double?)null;
        }

        //DEBUG Helper:

        [System.Diagnostics.Conditional("DEBUG")]
        private void DebugDumpMountBoolProperties(string reason) {
            try {
                Logger.Debug($"================ Mount BOOL dump ({reason}) ================");

                DumpBoolProps("TelescopeMediator", TelescopeMediator);

                var dev = TryGetProp(TelescopeMediator, "Device") ?? TryGetProp(TelescopeMediator, "Telescope");
                DumpBoolProps("TelescopeMediator.Device", dev);

                var info = InvokeGetInfo(TelescopeMediator);
                DumpBoolProps("TelescopeMediator.GetInfo()", info);

                Logger.Debug("============================================================");
            } catch (Exception ex) {
                Logger.Debug($"[Mount BOOL dump] failed: {ex}");
            }
        }

        private void DumpBoolProps(string label, object? obj) {
            if (obj == null) {
                Logger.Debug($"{label}: <null>");
                return;
            }

            Logger.Debug($"{label}: {obj.GetType().FullName}");

            try {
                var props = obj.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);

                foreach (var p in props) {
                    if (p.PropertyType != typeof(bool)) continue;

                    bool value;
                    try {
                        value = (bool)p.GetValue(obj)!;
                    } catch {
                        Logger.Debug($"  {p.Name} = <exception>");
                        continue;
                    }

                    Logger.Debug($"  {p.Name} = {value}");
                }
            } catch (Exception ex) {
                Logger.Debug($"  <error reading properties>: {ex.Message}");
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void DebugDumpMountRaDecFromInfo() {
            var info = InvokeGetInfo(TelescopeMediator);
            if (info == null) { Logger.Debug("[Mount RA/Dec dump] GetInfo() is null"); return; }

            Logger.Debug($"[Mount RA/Dec dump] GetInfo type: {info.GetType().FullName}");

            DumpAnyProp(info, "RightAscension");
            DumpAnyProp(info, "RA");
            DumpAnyProp(info, "Declination");
            DumpAnyProp(info, "Dec");
            DumpAnyProp(info, "Coordinates");
            DumpAnyProp(info, "TargetCoordinates");
        }

        private void DumpAnyProp(object src, string name) {
            try {
                var p = src.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.IgnoreCase);
                if (p == null) { Logger.Debug($"  {name}: <no prop>"); return; }

                var v = p.GetValue(src);
                Logger.Debug($"  {name}: {(v == null ? "<null>" : v.GetType().FullName + " = " + v)}");
            } catch (Exception ex) {
                Logger.Debug($"  {name}: <exception> {ex.Message}");
            }
        }

    }
}
