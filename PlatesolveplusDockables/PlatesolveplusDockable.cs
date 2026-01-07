using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugins.PlateSolvePlus.Services;
using NINA.Plugins.PlateSolvePlus.Utils;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables {

    [Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public sealed class PlateSolvePlusDockable : DockableVM,
        NINA.Equipment.Interfaces.ViewModel.IDockableVM,
        IDisposable,
        IPartImportsSatisfiedNotification {

        private readonly IProfileService profileService;
        private readonly IServiceFactory serviceFactory;

        [Import(AllowDefault = true)]
        public Platesolveplus? PluginSettings { get; set; }

        [Import]
        private Lazy<IPlateSolvePlusContext> ContextLazy { get; set; } = null!;
        private IPlateSolvePlusContext Context => ContextLazy.Value;

        // Services (via factory)
        private ITelescopeReferenceService TelescopeReferenceService => serviceFactory.GetTelescopeReferenceService();
        private IOffsetService OffsetService => serviceFactory.GetOffsetService();

        // Exposed to NINA MEF; forwarded to telescope reference service
        [Import(AllowDefault = true)]
        public ITelescopeMediator? TelescopeMediator {
            get => TelescopeReferenceService.TelescopeMediator;
            set => TelescopeReferenceService.TelescopeMediator = value;
        }

        public ICommand RefreshTelescopeCoordsCommand { get; }
        public ICommand CalibrateOffsetCommand { get; }
        public ICommand ResetOffsetCommand { get; }

        private string statusText = "Idle";
        public string StatusText {
            get => statusText;
            set { statusText = value; RaisePropertyChanged(nameof(StatusText)); }
        }

        private string detailsText = "";
        public string DetailsText {
            get => detailsText;
            set { detailsText = value; RaisePropertyChanged(nameof(DetailsText)); }
        }

        // Telescope coordinate display
        private string telescopeCoordsText = "n/a";
        public string TelescopeCoordsText {
            get => telescopeCoordsText;
            set { telescopeCoordsText = value; RaisePropertyChanged(nameof(TelescopeCoordsText)); }
        }

        private string telescopeCoordsStatusText = "";
        public string TelescopeCoordsStatusText {
            get => telescopeCoordsStatusText;
            set { telescopeCoordsStatusText = value; RaisePropertyChanged(nameof(TelescopeCoordsStatusText)); }
        }

        private DateTime? lastTelescopeRefreshUtc;
        public string LastTelescopeRefreshText =>
            lastTelescopeRefreshUtc.HasValue
                ? lastTelescopeRefreshUtc.Value.ToString("yyyy-MM-dd HH:mm:ss")
                : "-";

        // Main reference readiness
        private bool mainReferenceReady;
        public bool MainReferenceReady {
            get => mainReferenceReady;
            private set {
                if (mainReferenceReady == value) return;
                mainReferenceReady = value;
                RaisePropertyChanged(nameof(MainReferenceReady));
                RaisePropertyChanged(nameof(MainReferenceReadyText));
                RaisePropertyChanged(nameof(MainReferenceStatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string MainReferenceReadyText => MainReferenceReady ? "READY ✅" : "NOT READY ⚠️";

        private string mainReferenceStatusText = "Select a reference mode.";
        public string MainReferenceStatusText {
            get => mainReferenceStatusText;
            private set {
                if (mainReferenceStatusText == value) return;
                mainReferenceStatusText = value;
                RaisePropertyChanged(nameof(MainReferenceStatusText));
            }
        }

        // Main reference mode selection
        private bool useTelescopeCoordsAsMainRef = true;
        public bool UseTelescopeCoordsAsMainRef {
            get => useTelescopeCoordsAsMainRef;
            set {
                if (useTelescopeCoordsAsMainRef == value) return;
                useTelescopeCoordsAsMainRef = value;
                if (value) useMainScopeSolveAsMainRef = false;

                MainReferenceReady = TelescopeMediator != null;
                UpdateMainReferenceStatusText();

                RaisePropertyChanged(nameof(UseTelescopeCoordsAsMainRef));
                RaisePropertyChanged(nameof(UseMainScopeSolveAsMainRef));
                RaisePropertyChanged(nameof(MainReferenceHintText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool useMainScopeSolveAsMainRef;
        public bool UseMainScopeSolveAsMainRef {
            get => useMainScopeSolveAsMainRef;
            set {
                if (useMainScopeSolveAsMainRef == value) return;
                useMainScopeSolveAsMainRef = value;
                if (value) useTelescopeCoordsAsMainRef = false;

                // In main-scope mode readiness is handled by TelescopeReferenceService updates
                UpdateMainReferenceStatusText();

                RaisePropertyChanged(nameof(UseTelescopeCoordsAsMainRef));
                RaisePropertyChanged(nameof(UseMainScopeSolveAsMainRef));
                RaisePropertyChanged(nameof(MainReferenceHintText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string MainReferenceHintText =>
            UseTelescopeCoordsAsMainRef
                ? "Uses current mount coordinates as main reference (live)."
                : "Requires: run a MAIN plate solve with Sync, then Refresh (or Slew) to load the solved mount position.";

        private void UpdateMainReferenceStatusText() {
            if (UseTelescopeCoordsAsMainRef) {
                MainReferenceStatusText =
                    TelescopeMediator == null
                        ? "Live mount mode selected, but ITelescopeMediator is not available."
                        : "Live mount mode: any Refresh/Slew updates the main reference.";
            } else {
                MainReferenceStatusText =
                    MainReferenceReady
                        ? "Main-scope mode: reference captured from mount coordinates AFTER main-scope solve+sync."
                        : "Main-scope mode: NOT READY. Run main plate solve WITH Sync, then click Refresh (or Slew).";
            }
        }

        // Offset settings (persistent; shared with Options)
        private PlateSolvePlusSettings FallbackSettings { get; } = new PlateSolvePlusSettings();

        public PlateSolvePlusSettings Settings => PluginSettings?.Settings ?? FallbackSettings;

        private bool settingsSubscribed;
public bool IsRotationMode {
            get => Settings.OffsetMode == OffsetMode.Rotation;
            set {
                if (!value) return;
                if (Settings.OffsetMode == OffsetMode.Rotation) return;
                Settings.OffsetMode = OffsetMode.Rotation;
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
                UpdateCorrectedText();
            }
        }

        public bool IsArcsecMode {
            get => Settings.OffsetMode == OffsetMode.Arcsec;
            set {
                if (!value) return;
                if (Settings.OffsetMode == OffsetMode.Arcsec) return;
                Settings.OffsetMode = OffsetMode.Arcsec;
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
                UpdateCorrectedText();
            }
        }

        private string statusLine = "Offset: waiting for guider solve…";
        public string StatusLine {
            get => statusLine;
            set { statusLine = value; RaisePropertyChanged(nameof(StatusLine)); }
        }

        private (double raDeg, double decDeg)? lastGuiderSolveDeg;

        private string lastGuiderSolveText = "-";
        public string LastGuiderSolveText {
            get => lastGuiderSolveText;
            set { lastGuiderSolveText = value; RaisePropertyChanged(nameof(LastGuiderSolveText)); }
        }

        private string correctedSolveText = "-";
        public string CorrectedSolveText {
            get => correctedSolveText;
            set { correctedSolveText = value; RaisePropertyChanged(nameof(CorrectedSolveText)); }
        }

        public string LastCalibrationText =>
           Settings.OffsetLastCalibratedUtc.HasValue
            ? Settings.OffsetLastCalibratedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss")
            : "-";

        private string rotationAngleDegText = "-";
        public string RotationAngleDegText {
            get => rotationAngleDegText;
            set { rotationAngleDegText = value; RaisePropertyChanged(nameof(RotationAngleDegText)); }
        }

        private string rotationQuaternionText = "(1,0,0,0)";
        public string RotationQuaternionText {
            get => rotationQuaternionText;
            set { rotationQuaternionText = value; RaisePropertyChanged(nameof(RotationQuaternionText)); }
        }

        private bool disposed;
        private bool importsReady;

        [ImportingConstructor]
        public PlateSolvePlusDockable(IProfileService profileService)
            : base(profileService) {

            this.profileService = profileService;
            this.serviceFactory = new ServiceFactory();

            Title = "PlateSolvePlus";
            IsVisible = true;

            RefreshTelescopeCoordsCommand = new RelayCommand(_ => RefreshTelescopeCoords());

            CalibrateOffsetCommand = new RelayCommand(_ => CalibrateOffset(), _ => CanCalibrate());
            ResetOffsetCommand = new RelayCommand(_ => {
                Settings.ResetOffset();
                StatusLine = "Offset reset.";
                RaisePropertyChanged(nameof(LastCalibrationText));
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
                UpdateRotationInfoText();
                UpdateCorrectedText();
            });
            // Defaults
UseTelescopeCoordsAsMainRef = true;

            StatusText = "Ready";
            DetailsText = "Main reference + offset calibration.";
            StatusLine = "Offset: waiting for guider solve…";

            UpdateRotationInfoText();
        }

        
        private void HookPersistentSettings() {
            if (settingsSubscribed) return;
            if (PluginSettings?.Settings == null) return;
            PluginSettings.Settings.PropertyChanged += Settings_PropertyChanged;
            settingsSubscribed = true;
        }

        private void UnhookPersistentSettings() {
            if (!settingsSubscribed) return;
            if (PluginSettings?.Settings != null) {
                PluginSettings.Settings.PropertyChanged -= Settings_PropertyChanged;
            }
            settingsSubscribed = false;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e) {
            RaisePropertyChanged(nameof(LastCalibrationText));
            RaisePropertyChanged(nameof(IsRotationMode));
            RaisePropertyChanged(nameof(IsArcsecMode));
            UpdateRotationInfoText();
            UpdateCorrectedText();
        }

public void OnImportsSatisfied() {
            importsReady = true;
            Logger.Debug(
    $"[CameraDockable] OnImportsSatisfied | " +
    $"TRS instance={TelescopeReferenceService?.GetHashCode()} | " +
    $"Mediator={TelescopeReferenceService?.TelescopeMediator?.GetHashCode()} | " +
    $"Dockable this={this.GetHashCode()}"
);


            HookPersistentSettings();
TelescopeReferenceService.ReferenceUpdated += TelescopeReferenceService_ReferenceUpdated;
            Context.LastGuiderSolveUpdated += Context_LastGuiderSolveUpdated;

            // If we already have a solve snapshot (e.g. camera dockable solved first), consume it
            ConsumeContextSolveSnapshot();

            RefreshTelescopeCoords();
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;

            
            UnhookPersistentSettings();
try { TelescopeReferenceService.ReferenceUpdated -= TelescopeReferenceService_ReferenceUpdated; } catch { }
            try { Context.LastGuiderSolveUpdated -= Context_LastGuiderSolveUpdated; } catch { }

            try { serviceFactory.Dispose(); } catch { }
        }

        private void Context_LastGuiderSolveUpdated(object? sender, EventArgs e) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) {
                dispatcher.Invoke(() => Context_LastGuiderSolveUpdated(sender, e));
                return;
            }

            ConsumeContextSolveSnapshot();
        }

        private void ConsumeContextSolveSnapshot() {
            var snap = Context.LastGuiderSolve;
            if (snap == null) return;

            OnGuiderSolveSuccess(snap.RaDeg, snap.DecDeg);
        }

        private void TelescopeReferenceService_ReferenceUpdated(object? sender, TelescopeReferenceUpdatedEventArgs e) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) {
                dispatcher.Invoke(() => TelescopeReferenceService_ReferenceUpdated(sender, e));
                return;
            }

            lastTelescopeRefreshUtc = DateTime.UtcNow;
            RaisePropertyChanged(nameof(LastTelescopeRefreshText));

            TelescopeCoordsStatusText = $"{e.StatusText}  |  UTC: {LastTelescopeRefreshText}";

            if (!e.Success || !e.RaDeg.HasValue || !e.DecDeg.HasValue) {
                TelescopeCoordsText = "n/a";
                MainReferenceReady = false;
                UpdateMainReferenceStatusText();
                return;
            }

            var raDeg = e.RaDeg.Value;
            var decDeg = e.DecDeg.Value;

            TelescopeCoordsText =
                $"RA: {AstroFormat.FormatRaHms(raDeg)}  |  Dec: {AstroFormat.FormatDecDms(decDeg)} (deg: {raDeg:0.######}, {decDeg:0.######})";

            MainReferenceReady = true;
            UpdateMainReferenceStatusText();
        }

        private void RefreshTelescopeCoords() {
            if (TelescopeReferenceService.TryGetCurrentRaDec(out var raDeg, out var decDeg)) {
                TelescopeReferenceService_ReferenceUpdated(this,
                    new TelescopeReferenceUpdatedEventArgs(true, "Updated via manual Refresh.", raDeg, decDeg));
            } else {
                TelescopeReferenceService_ReferenceUpdated(this,
                    new TelescopeReferenceUpdatedEventArgs(false,
                        TelescopeMediator == null ? "ITelescopeMediator not available." : "Could not read telescope coordinates.",
                        null, null));
            }
        }

        // ============================
        // Offset logic
        // ============================

        public void OnGuiderSolveSuccess(double raDeg, double decDeg) {
            lastGuiderSolveDeg = (raDeg, decDeg);

            LastGuiderSolveText =
                $"RA: {AstroFormat.FormatRaHms(raDeg)}  |  Dec: {AstroFormat.FormatDecDms(decDeg)}  (deg: {raDeg:0.######}, {decDeg:0.######})";

            StatusLine = Settings.OffsetEnabled
                ? $"Guider solve received. Offset enabled. Mode={Settings.OffsetMode}"
                : "Guider solve received. Offset is disabled.";

            UpdateCorrectedText();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool CanCalibrate() {
            if (!importsReady) return false;
            if (!lastGuiderSolveDeg.HasValue) return false;

            if (UseMainScopeSolveAsMainRef)
                return MainReferenceReady && TelescopeMediator != null;

            return TelescopeMediator != null;
        }

        private void CalibrateOffset() {
            if (!lastGuiderSolveDeg.HasValue) {
                StatusLine = "No guider solve available yet.";
                return;
            }

            if (UseMainScopeSolveAsMainRef && !MainReferenceReady) {
                StatusLine = "Main-scope mode selected, but main reference is NOT READY. Run main solve+sync, then Refresh/Slew.";
                return;
            }

            if (TelescopeMediator == null) {
                StatusLine = "ITelescopeMediator not available (main reference missing).";
                return;
            }

            if (!TelescopeReferenceService.TryGetCurrentRaDec(out var mainRa, out var mainDec)) {
                StatusLine = "Could not read telescope coordinates (main reference).";
                return;
            }

            var (guideRa, guideDec) = lastGuiderSolveDeg.Value;

            var res = OffsetService.Calibrate(Settings, mainRa, mainDec, guideRa, guideDec);

            StatusLine =
                $"Offset calibrated (Rotation). Angle={res.RotationAngleDeg:0.####}°  " +
                $"Legacy ΔRA={res.DeltaRaArcsec:0.###}\", ΔDec={res.DeltaDecArcsec:0.###}\"";

            RaisePropertyChanged(nameof(LastCalibrationText));
            UpdateRotationInfoText();
            UpdateCorrectedText();
        }

        private void UpdateCorrectedText() {
            if (!lastGuiderSolveDeg.HasValue) {
                CorrectedSolveText = "-";
                return;
            }

            var (ra, dec) = lastGuiderSolveDeg.Value;

            if (!Settings.OffsetEnabled) {
                CorrectedSolveText = "Offset disabled → using guider solve as-is.";
                return;
            }

            var (raC, decC) = OffsetService.ApplyToGuiderSolve(Settings, ra, dec);
            var modeText = Settings.OffsetMode == OffsetMode.Rotation ? "Rotation" : "Arcsec";

            CorrectedSolveText =
                $"[{modeText}] RA: {AstroFormat.FormatRaHms(raC)}  |  Dec: {AstroFormat.FormatDecDms(decC)}  (deg: {raC:0.######}, {decC:0.######})";
        }

        private void UpdateRotationInfoText() {
            RotationQuaternionText = OffsetService.GetQuaternionText(Settings);
            RotationAngleDegText = OffsetService.GetRotationAngleText(Settings);
        }
    }
}
