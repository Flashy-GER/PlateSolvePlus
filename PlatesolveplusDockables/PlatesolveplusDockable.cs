using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugins.PlateSolvePlus.PlateSolving;
using NINA.Plugins.PlateSolvePlus.SecondaryCamera;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables {
    [Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public class PlateSolvePlusDockable : DockableVM, NINA.Equipment.Interfaces.ViewModel.IDockableVM {
        private readonly IProfileService profileService;

        [Import(AllowDefault = true)]
        public Platesolveplus? PluginSettings { get; set; }

        [Import(AllowDefault = true)]
        public IPlateSolverFactory? PlateSolverFactory { get; set; }

        [Import(AllowDefault = true)]
        public IImageDataFactory? ImageDataFactory { get; set; }

        // MAIN reference source: NINA telescope mediator
        private ITelescopeMediator? telescopeMediator;

        [Import(AllowDefault = true)]
        public ITelescopeMediator? TelescopeMediator {
            get => telescopeMediator;
            set {
                if (ReferenceEquals(telescopeMediator, value)) return;

                if (telescopeMediator != null) {
                    try { telescopeMediator.Slewed -= TelescopeMediator_Slewed; } catch { }
                }

                telescopeMediator = value;

                if (telescopeMediator != null) {
                    try { telescopeMediator.Slewed += TelescopeMediator_Slewed; } catch { }
                }

                RefreshTelescopeCoords();
            }
        }

        private async Task TelescopeMediator_Slewed(object sender, MountSlewedEventArgs e) {
            try {
                RefreshTelescopeCoords();
                await Task.CompletedTask;
            } catch { }
        }

        // Secondary camera
        private ISecondaryCamera? secondaryCamera;
        private const string ProgId = "ASCOM.Simulator.Camera";

        // Commands
        public ICommand ConnectSecondaryCommand { get; }
        public ICommand DisconnectSecondaryCommand { get; }
        public ICommand CaptureAndSolveCommand { get; }
        public ICommand RefreshTelescopeCoordsCommand { get; }

        // UI state
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

        private bool isSecondaryConnected;
        public bool IsSecondaryConnected {
            get => isSecondaryConnected;
            set { isSecondaryConnected = value; RaisePropertyChanged(nameof(IsSecondaryConnected)); }
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

                // In live-mount mode we can treat reference as ready if telescope mediator is available
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

                // In main-scope mode we require a fresh refresh/slew AFTER main solve+sync
                MainReferenceReady = false;
                UpdateMainReferenceStatusText();

                RaisePropertyChanged(nameof(UseMainScopeSolveAsMainRef));
                RaisePropertyChanged(nameof(UseTelescopeCoordsAsMainRef));
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

        // Offset handling (Rotation default)
        public PlateSolvePlusSettings Settings { get; } = new PlateSolvePlusSettings();

        // Mode helper properties for XAML RadioButtons
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

        public ICommand CalibrateOffsetCommand { get; }
        public ICommand ResetOffsetCommand { get; }

        private string statusLine = "Waiting for solve…";
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
            Settings.LastOffsetCalibrationUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

        // Rotation display text
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

        [ImportingConstructor]
        public PlateSolvePlusDockable(IProfileService profileService)
            : base(profileService) {
            this.profileService = profileService;

            Title = "PlateSolvePlus";
            IsVisible = true;

            // Rotation as standard
            Settings.OffsetMode = OffsetMode.Rotation;

            ConnectSecondaryCommand = new SimpleAsyncCommand(ConnectSecondaryAsync);
            DisconnectSecondaryCommand = new SimpleAsyncCommand(DisconnectSecondaryAsync);
            CaptureAndSolveCommand = new SimpleAsyncCommand(CaptureAndSolveAsync);
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

            Settings.PropertyChanged += (_, __) => {
                RaisePropertyChanged(nameof(LastCalibrationText));
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
                UpdateRotationInfoText();
                UpdateCorrectedText();
            };

            // default selection
            UseTelescopeCoordsAsMainRef = true;

            StatusText = "Ready";
            DetailsText = "Connect secondary camera to start.";
            StatusLine = "Offset: waiting for guider solve…";

            UpdateRotationInfoText();
            RefreshTelescopeCoords();
        }

        // ============================
        // Secondary camera connect/disconnect
        // ============================
        private async Task ConnectSecondaryAsync() {
            try {
                StatusText = "Connecting secondary camera...";
                DetailsText = $"ProgID: {ProgId}";

                secondaryCamera?.Dispose();
                secondaryCamera = new AscomComSecondaryCamera(ProgId);

                await secondaryCamera.ConnectAsync(CancellationToken.None);
                IsSecondaryConnected = secondaryCamera.IsConnected;

                StatusText = IsSecondaryConnected
                    ? "Secondary camera connected ✅"
                    : "Secondary camera not connected ❌";
            } catch (Exception ex) {
                StatusText = "Connect failed ❌";
                DetailsText = ex.ToString();
                IsSecondaryConnected = false;
            }
        }

        private async Task DisconnectSecondaryAsync() {
            try {
                StatusText = "Disconnecting secondary camera...";
                if (secondaryCamera != null)
                    await secondaryCamera.DisconnectAsync(CancellationToken.None);

                secondaryCamera?.Dispose();
                secondaryCamera = null;

                IsSecondaryConnected = false;
                StatusText = "Disconnected ✅";
            } catch (Exception ex) {
                StatusText = "Disconnect failed ❌";
                DetailsText = ex.ToString();
            }
        }

        // ============================
        // Telescope refresh (Slew + manual)
        // ============================
        private void RefreshTelescopeCoords() {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) {
                dispatcher.Invoke(RefreshTelescopeCoords);
                return;
            }

            lastTelescopeRefreshUtc = DateTime.UtcNow;
            RaisePropertyChanged(nameof(LastTelescopeRefreshText));

            if (TelescopeMediator == null) {
                TelescopeCoordsText = "n/a";
                TelescopeCoordsStatusText = "ITelescopeMediator not available.";

                // readiness depends on mode
                MainReferenceReady = UseTelescopeCoordsAsMainRef ? false : false;
                UpdateMainReferenceStatusText();
                return;
            }

            if (!TryGetMainReferenceFromTelescope(out var raDeg, out var decDeg)) {
                TelescopeCoordsText = "n/a";
                TelescopeCoordsStatusText = "Could not read telescope coordinates.";

                MainReferenceReady = false;
                UpdateMainReferenceStatusText();
                return;
            }

            TelescopeCoordsText =
                $"RA: {FormatRaHms(raDeg)}  |  Dec: {FormatDecDms(decDeg)} (deg: {raDeg:0.######}, {decDeg:0.######})";

            TelescopeCoordsStatusText =
                $"Updated via Slew/Refresh  |  UTC: {LastTelescopeRefreshText}";

            // Ready logic:
            // - live mount mode: ready whenever we can read mount coords
            // - main-scope mode: ready only after an explicit Refresh/Slew while main-scope mode is selected
            if (UseTelescopeCoordsAsMainRef) {
                MainReferenceReady = true;
            } else {
                MainReferenceReady = true;
            }

            UpdateMainReferenceStatusText();
        }

        private bool TryGetMainReferenceFromTelescope(out double raDeg, out double decDeg) {
            raDeg = 0;
            decDeg = 0;

            try {
                var coords = TelescopeMediator!.GetCurrentPosition();
                if (coords == null) return false;

                if (TryReadNumber(coords, new[] { "RightAscension", "RA" }, out var raVal) &&
                    TryReadNumber(coords, new[] { "Declination", "Dec" }, out var decVal)) {
                    raDeg = GuessRaToDegrees(raVal);
                    decDeg = decVal;
                    return true;
                }

                return false;
            } catch {
                return false;
            }
        }

        // ============================
        // Capture + Solve
        // ============================
        private async Task CaptureAndSolveAsync() {
            if (secondaryCamera == null || !secondaryCamera.IsConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            try {
                var imgFactory = ImageDataFactory ?? TryResolve<IImageDataFactory>();
                var psFactory = PlateSolverFactory ?? TryResolve<IPlateSolverFactory>();

                if (imgFactory == null || psFactory == null) {
                    StatusText = "Missing NINA factories ❌";
                    DetailsText = $"ImageDataFactory={imgFactory != null}, PlateSolverFactory={psFactory != null}";
                    return;
                }

                StatusText = "Capturing frame…";
                DetailsText = "";

                var frame = await secondaryCamera.CaptureAsync(
                    exposureSeconds: 2.0,
                    binX: 1,
                    binY: 1,
                    gain: null,
                    ct: CancellationToken.None);

                StatusText = "Building IImageData…";

                ushort[] packed = ConvertToUShortRowMajor(frame.Pixels, frame.Width, frame.Height);
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

                double focalLengthMm = PluginSettings?.GuideFocalLengthMm ?? 240.0;
                double pixelSizeUm = PluginSettings?.GuidePixelSizeUm ?? 3.75;

                var parameter = NinaPlateSolveParameterFactory.Create(
                    searchRadiusDeg: searchRadiusDeg,
                    downsample: downsample,
                    timeoutSec: timeoutSec,
                    focalLengthMm: focalLengthMm,
                    pixelSizeUm: pixelSizeUm
                );

                var solver = psFactory.GetPlateSolver(psSettings);

                StatusText = "Solving…";
                var result = await solver.SolveAsync(imageData, parameter, null, CancellationToken.None);

                StatusText = "Solve finished ✅";
                DetailsText =
                    $"Frame: {frame.Width}x{frame.Height}, bitDepth={frame.BitDepth}\n" +
                    $"SearchRadius={searchRadiusDeg}°, Downsample={downsample}, Timeout={timeoutSec}s\n" +
                    $"FocalLength={focalLengthMm}mm, PixelSize={pixelSizeUm}µm\n\n" +
                    DumpObject(result);

                if (TryExtractRaDecDeg(result, out var raDeg, out var decDeg)) {
                    OnGuiderSolveSuccess(raDeg, decDeg);
                } else {
                    StatusLine = "Solve finished, but RA/Dec could not be extracted for offset.";
                }

                RefreshTelescopeCoords();
            } catch (Exception ex) {
                StatusText = "Capture / Solve failed ❌";
                DetailsText = ex.ToString();
                StatusLine = "Offset: solve failed (no update).";
            }
        }

        // ============================
        // Offset logic (Rotation default)
        // ============================
        public void OnGuiderSolveSuccess(double raDeg, double decDeg) {
            lastGuiderSolveDeg = (raDeg, decDeg);

            LastGuiderSolveText =
                $"RA: {FormatRaHms(raDeg)}  |  Dec: {FormatDecDms(decDeg)}  (deg: {raDeg:0.######}, {decDeg:0.######})";

            StatusLine = Settings.OffsetEnabled
                ? $"Guider solve received. Offset enabled. Mode={Settings.OffsetMode}"
                : "Guider solve received. Offset is disabled.";

            UpdateCorrectedText();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool CanCalibrate() {
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

            if (!TryGetMainReferenceFromTelescope(out var mainRa, out var mainDec)) {
                StatusLine = "Could not read telescope coordinates (main reference).";
                return;
            }

            var (guideRa, guideDec) = lastGuiderSolveDeg.Value;

            // Rotation (STANDARD)
            var (qw, qx, qy, qz) = OffsetMath.ComputeRotationQuaternion(
                mainRa, mainDec,
                guideRa, guideDec);

            Settings.RotationQw = qw;
            Settings.RotationQx = qx;
            Settings.RotationQy = qy;
            Settings.RotationQz = qz;

            // Legacy arcsec for comparison/debug
            var (dRaArcsec, dDecArcsec) = OffsetMath.ComputeOffsetArcsec(mainRa, mainDec, guideRa, guideDec);
            Settings.OffsetRaArcsec = dRaArcsec;
            Settings.OffsetDecArcsec = dDecArcsec;

            Settings.LastOffsetCalibrationUtc = DateTime.UtcNow;

            StatusLine =
                $"Offset calibrated (Rotation). Angle={ComputeRotationAngleDeg(qw):0.####}°  " +
                $"Legacy ΔRA={dRaArcsec:0.###}\", ΔDec={dDecArcsec:0.###}\"";

            RaisePropertyChanged(nameof(LastCalibrationText));
            UpdateRotationInfoText();
            UpdateCorrectedText();
        }

        private void UpdateCorrectedText() {
            if (!lastGuiderSolveDeg.HasValue) {
                CorrectedSolveText = "-";
                return;
            }

            if (!Settings.OffsetEnabled) {
                CorrectedSolveText = "Offset disabled → using guider solve as-is.";
                return;
            }

            var (ra, dec) = lastGuiderSolveDeg.Value;

            if (Settings.OffsetMode == OffsetMode.Rotation) {
                var (raC, decC) = OffsetMath.ApplyRotationQuaternion(
                    ra, dec,
                    Settings.RotationQw, Settings.RotationQx, Settings.RotationQy, Settings.RotationQz);

                CorrectedSolveText =
                    $"[Rotation] RA: {FormatRaHms(raC)}  |  Dec: {FormatDecDms(decC)}  (deg: {raC:0.######}, {decC:0.######})";
            } else {
                var (raC, decC) = OffsetMath.ApplyOffsetArcsec(
                    ra, dec,
                    Settings.OffsetRaArcsec, Settings.OffsetDecArcsec);

                CorrectedSolveText =
                    $"[Arcsec] RA: {FormatRaHms(raC)}  |  Dec: {FormatDecDms(decC)}  (deg: {raC:0.######}, {decC:0.######})";
            }
        }

        private void UpdateRotationInfoText() {
            RotationQuaternionText =
                $"({Settings.RotationQw:0.######}, {Settings.RotationQx:0.######}, {Settings.RotationQy:0.######}, {Settings.RotationQz:0.######})";

            RotationAngleDegText =
                $"{ComputeRotationAngleDeg(Settings.RotationQw):0.####}";
        }

        private static double ComputeRotationAngleDeg(double qw) {
            double c = qw;
            if (c < -1) c = -1;
            if (c > 1) c = 1;
            double angleRad = 2.0 * Math.Acos(c);
            return angleRad * (180.0 / Math.PI);
        }

        // ============================
        // Helper methods
        // ============================
        private static T? TryResolve<T>() where T : class {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types) {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(T).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                    try {
                        if (Activator.CreateInstance(t) is T ok)
                            return ok;
                    } catch { }
                }
            }
            return null;
        }

        private static ushort[] ConvertToUShortRowMajor(int[,] pixels, int width, int height) {
            var arr = new ushort[width * height];
            int idx = 0;

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) {
                    int v = pixels[y, x];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    arr[idx++] = (ushort)v;
                }

            return arr;
        }

        private static bool TryReadNumber(object src, string[] names, out double value) {
            value = 0;
            foreach (var n in names) {
                var p = src.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(src);
                if (v == null) continue;

                if (v is double d) { value = d; return true; }
                if (v is float f) { value = f; return true; }
                if (v is int i) { value = i; return true; }
                if (double.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
            }
            return false;
        }

        private static double? ReadDouble(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (double.TryParse(v.ToString(), out var d)) return d;
            }
            return null;
        }

        private static int? ReadInt(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (int.TryParse(v.ToString(), out var i)) return i;
            }
            return null;
        }

        private static bool TryExtractRaDecDeg(object result, out double raDeg, out double decDeg) {
            raDeg = 0;
            decDeg = 0;
            if (result == null) return false;

            if (TryReadNumber(result, new[] { "RightAscension", "RA" }, out var raVal) &&
                TryReadNumber(result, new[] { "Declination", "Dec" }, out var decVal)) {
                raDeg = GuessRaToDegrees(raVal);
                decDeg = decVal;
                return true;
            }

            var coordsProp = result.GetType().GetProperty("Coordinates", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var coords = coordsProp?.GetValue(result);
            if (coords != null) {
                if (TryReadNumber(coords, new[] { "RightAscension", "RA" }, out raVal) &&
                    TryReadNumber(coords, new[] { "Declination", "Dec" }, out decVal)) {
                    raDeg = GuessRaToDegrees(raVal);
                    decDeg = decVal;
                    return true;
                }
            }

            return false;
        }

        private static string DumpObject(object obj) {
            if (obj == null) return "<null>";
            var props = obj.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead)
                .OrderBy(p => p.Name);

            return string.Join("\n", props.Select(p => {
                object? v = null;
                try { v = p.GetValue(obj); } catch { }
                return $"{p.Name} = {v}";
            }));
        }

        private static double GuessRaToDegrees(double ra) => (ra >= 0 && ra <= 24.0) ? ra * 15.0 : ra;

        private static string FormatRaHms(double raDeg) {
            var raHours = raDeg / 15.0;
            if (raHours < 0) raHours += 24.0;
            raHours %= 24.0;

            var h = (int)Math.Floor(raHours);
            var mFloat = (raHours - h) * 60.0;
            var m = (int)Math.Floor(mFloat);
            var s = (mFloat - m) * 60.0;

            return $"{h:00}:{m:00}:{s:00.##}";
        }

        private static string FormatDecDms(double decDeg) {
            var sign = decDeg < 0 ? "-" : "+";
            var a = Math.Abs(decDeg);
            var d = (int)Math.Floor(a);
            var mFloat = (a - d) * 60.0;
            var m = (int)Math.Floor(mFloat);
            var s = (mFloat - m) * 60.0;

            return $"{sign}{d:00}° {m:00}' {s:00.##}\"";
        }
    }

    public sealed class SimpleAsyncCommand : ICommand {
        private readonly Func<Task> execute;
        private bool isExecuting;

        public SimpleAsyncCommand(Func<Task> execute) => this.execute = execute;

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
