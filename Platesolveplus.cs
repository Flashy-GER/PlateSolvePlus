using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PlateSolvePlus.Services;
using NINA.Plugins.PlateSolvePlus.Utils;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus {

    // Cross-container live update (Options UI instance -> Dockable instance)
    public static class PlateSolvePlusSettingsBus {
        public static event EventHandler<PlateSolvePlusSettingChangedEventArgs>? SettingChanged;

        public static void Publish(string key, object? value) {
            SettingChanged?.Invoke(null, new PlateSolvePlusSettingChangedEventArgs(key, value));
        }
    }

    public sealed class PlateSolvePlusSettingChangedEventArgs : EventArgs {
        public string Key { get; }
        public object? Value { get; }

        public PlateSolvePlusSettingChangedEventArgs(string key, object? value) {
            Key = key ?? string.Empty;
            Value = value;
        }
    }

    // Local minimal ICommand to avoid dependency issues
    internal sealed class SimpleCommand : ICommand {
        private readonly Action execute;
        private readonly Func<bool>? canExecute;

        public SimpleCommand(Action execute, Func<bool>? canExecute = null) {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => execute();

        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    [Export(typeof(IPluginManifest))]
    [Export(typeof(Platesolveplus))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class Platesolveplus : PluginBase, INotifyPropertyChanged {

        private readonly IProfileService profileService;
        private readonly IPluginOptionsAccessor options;

        public PlateSolvePlusSettings Settings { get; }



        // =========================
        // Offset display helpers (for Options.xaml / API)
        // =========================
        private readonly OffsetService offsetService = new();

        public bool HasOffsetSet => Settings.HasOffsetSet;

        public string OffsetStatusText => HasOffsetSet ? "Offset: kalibriert ✅" : "Offset: nicht gesetzt ⚠️";

        public string OffsetQuaternionText => offsetService.GetQuaternionText(Settings);
        public string OffsetRotationDegText => offsetService.GetRotationAngleText(Settings);

        public string OffsetRaArcsecText => $"{Settings.OffsetRaArcsec:0.0} ″";
        public string OffsetDecArcsecText => $"{Settings.OffsetDecArcsec:0.0} ″";

        public string OffsetLastCalibratedText =>
            Settings.OffsetLastCalibratedUtc.HasValue
                ? Settings.OffsetLastCalibratedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss")
                : "-";

        // For Options.xaml "Delete Rotation Offset"
        public ICommand ResetRotationOffsetCommand { get; }
        public ICommand ResetOffsetCommand { get; }


        private bool isLoadingSettings;
        // Prevent endless ping-pong when we apply values coming from a different MEF container
        // (e.g. Dockable calibrated offset -> Options UI instance).
        private bool suppressBusPublish;

        [ImportingConstructor]
        public Platesolveplus(IProfileService profileService) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));

            options = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));

            Settings = new PlateSolvePlusSettings();

            ResetRotationOffsetCommand = new SimpleCommand(ResetRotationOffset);
            ResetOffsetCommand = new SimpleCommand(ResetOffset);


            LoadAllIntoSettings(Settings);

            Settings.PropertyChanged += Settings_PropertyChanged;
            profileService.ProfileChanged += ProfileService_ProfileChanged;

            // Receive updates originating from other MEF containers (e.g. Dockables) so the
            // Options UI shows fresh values immediately without a N.I.N.A. restart.
            PlateSolvePlusSettingsBus.SettingChanged += SettingsBus_SettingChanged;
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            LoadAllIntoSettings(Settings);
            RaisePropertyChanged(string.Empty);
        }

        private void SettingsBus_SettingChanged(object? sender, PlateSolvePlusSettingChangedEventArgs e) {
            if (e == null) return;

            // If the value already matches, do nothing (prevents churn + loops)
            var current = GetCurrentValue(e.Key);
            if (Equals(current, e.Value)) return;

            try {
                suppressBusPublish = true;
                ApplyFromBus(e.Key, e.Value);
            } finally {
                suppressBusPublish = false;
            }
        }

        private void ApplyFromBus(string key, object? value) {
            // Apply the incoming value to our Settings instance.
            // This will fire Settings_PropertyChanged -> PersistSingle -> UI refresh.
            switch (key) {
                case nameof(PlateSolvePlusSettings.GuideExposureSeconds):
                    if (value is double d1) Settings.GuideExposureSeconds = d1;
                    break;
                case nameof(PlateSolvePlusSettings.GuideGain):
                    if (value is int i1) Settings.GuideGain = i1;
                    break;
                case nameof(PlateSolvePlusSettings.GuideBinning):
                    if (value is int i2) Settings.GuideBinning = i2;
                    break;

                case nameof(PlateSolvePlusSettings.GuideFocalLengthMm):
                    if (value is double d2) Settings.GuideFocalLengthMm = d2;
                    break;
                case nameof(PlateSolvePlusSettings.UseCameraPixelSize):
                    if (value is bool b1) Settings.UseCameraPixelSize = b1;
                    break;
                case nameof(PlateSolvePlusSettings.GuidePixelSizeUm):
                    if (value is double d3) Settings.GuidePixelSizeUm = d3;
                    break;

                case nameof(PlateSolvePlusSettings.SolverSearchRadiusDeg):
                    if (value is double d4) Settings.SolverSearchRadiusDeg = d4;
                    break;
                case nameof(PlateSolvePlusSettings.SolverDownsample):
                    if (value is int i3) Settings.SolverDownsample = i3;
                    break;
                case nameof(PlateSolvePlusSettings.SolverTimeoutSec):
                    if (value is int i4) Settings.SolverTimeoutSec = i4;
                    break;

                case nameof(PlateSolvePlusSettings.CenteringThresholdArcmin):
                    if (value is double d5) Settings.CenteringThresholdArcmin = d5;
                    break;
                case nameof(PlateSolvePlusSettings.CenteringMaxAttempts):
                    if (value is int i5) Settings.CenteringMaxAttempts = i5;
                    break;

                case nameof(PlateSolvePlusSettings.OffsetEnabled):
                    if (value is bool bo) Settings.OffsetEnabled = bo;
                    break;
                case nameof(PlateSolvePlusSettings.OffsetMode):
                    if (value is OffsetMode om) Settings.OffsetMode = om;
                    else if (value is int omInt) Settings.OffsetModeInt = omInt;
                    break;
                case nameof(PlateSolvePlusSettings.OffsetModeInt):
                    if (value is int omi) Settings.OffsetModeInt = omi;
                    break;
                case nameof(PlateSolvePlusSettings.OffsetRaArcsec):
                    if (value is double dra) Settings.OffsetRaArcsec = dra;
                    break;
                case nameof(PlateSolvePlusSettings.OffsetDecArcsec):
                    if (value is double ddec) Settings.OffsetDecArcsec = ddec;
                    break;
                case nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc):
                    if (value is DateTime dt) Settings.OffsetLastCalibratedUtc = dt;
                    else Settings.OffsetLastCalibratedUtc = null;
                    break;

                case nameof(PlateSolvePlusSettings.RotationQw):
                    if (value is double qw) Settings.RotationQw = qw;
                    break;
                case nameof(PlateSolvePlusSettings.RotationQx):
                    if (value is double qx) Settings.RotationQx = qx;
                    break;
                case nameof(PlateSolvePlusSettings.RotationQy):
                    if (value is double qy) Settings.RotationQy = qy;
                    break;
                case nameof(PlateSolvePlusSettings.RotationQz):
                    if (value is double qz) Settings.RotationQz = qz;
                    break;

                case nameof(PlateSolvePlusSettings.ApiEnabled):
                    if (value is bool ab) Settings.ApiEnabled = ab;
                    break;
                case nameof(PlateSolvePlusSettings.ApiPort):
                    if (value is int ap) Settings.ApiPort = ap;
                    break;
                case nameof(PlateSolvePlusSettings.ApiRequireToken):
                    if (value is bool art) Settings.ApiRequireToken = art;
                    break;
                case nameof(PlateSolvePlusSettings.ApiToken):
                    Settings.ApiToken = value as string;
                    break;
            }
        }

        private void ResetRotationOffset() {
            Settings.RotationQw = 1.0;
            Settings.RotationQx = 0.0;
            Settings.RotationQy = 0.0;
            Settings.RotationQz = 0.0;

            Settings.OffsetLastCalibratedUtc = null;
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
            if (isLoadingSettings) return;

            var name = e.PropertyName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return;

            PersistSingle(name);
            if (!suppressBusPublish) {
                PlateSolvePlusSettingsBus.Publish(name, GetCurrentValue(name));
            }

            RaisePropertyChanged(name);


            // Keep derived offset display properties in sync
            if (name == nameof(PlateSolvePlusSettings.OffsetRaArcsec) ||
                name == nameof(PlateSolvePlusSettings.OffsetDecArcsec) ||
                name == nameof(PlateSolvePlusSettings.RotationQw) ||
                name == nameof(PlateSolvePlusSettings.RotationQx) ||
                name == nameof(PlateSolvePlusSettings.RotationQy) ||
                name == nameof(PlateSolvePlusSettings.RotationQz) ||
                name == nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc) ||
                name == nameof(PlateSolvePlusSettings.OffsetMode) ||
                name == nameof(PlateSolvePlusSettings.OffsetEnabled)) {

                RaisePropertyChanged(nameof(HasOffsetSet));
                RaisePropertyChanged(nameof(OffsetStatusText));
                RaisePropertyChanged(nameof(OffsetQuaternionText));
                RaisePropertyChanged(nameof(OffsetRotationDegText));
                RaisePropertyChanged(nameof(OffsetRaArcsecText));
                RaisePropertyChanged(nameof(OffsetDecArcsecText));
                RaisePropertyChanged(nameof(OffsetLastCalibratedText));
            }
            if (name == nameof(PlateSolvePlusSettings.OffsetMode) || name == nameof(PlateSolvePlusSettings.OffsetModeInt)) {
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
            }

        }

        private void ResetOffset() {
            Settings.ResetOffset();
        }
        private void LoadAllIntoSettings(PlateSolvePlusSettings s) {
            isLoadingSettings = true;
            try {
                // Capture
                s.GuideExposureSeconds = options.GetValueDouble(nameof(PlateSolvePlusSettings.GuideExposureSeconds), 2.0);
                s.GuideGain = options.GetValueInt32(nameof(PlateSolvePlusSettings.GuideGain), -1);
                s.GuideBinning = Math.Max(1, options.GetValueInt32(nameof(PlateSolvePlusSettings.GuideBinning), 1));

                // Optics
                s.GuideFocalLengthMm = Math.Max(1.0, options.GetValueDouble(nameof(PlateSolvePlusSettings.GuideFocalLengthMm), 240.0));
                s.UseCameraPixelSize = options.GetValueBoolean(nameof(PlateSolvePlusSettings.UseCameraPixelSize), true);
                s.GuidePixelSizeUm = Math.Max(0.1, options.GetValueDouble(nameof(PlateSolvePlusSettings.GuidePixelSizeUm), 3.75));

                // Solver
                s.SolverSearchRadiusDeg = Math.Max(0.1, options.GetValueDouble(nameof(PlateSolvePlusSettings.SolverSearchRadiusDeg), 5.0));
                s.SolverDownsample = Math.Max(0, options.GetValueInt32(nameof(PlateSolvePlusSettings.SolverDownsample), 2));
                s.SolverTimeoutSec = Math.Max(1, options.GetValueInt32(nameof(PlateSolvePlusSettings.SolverTimeoutSec), 60));

                // Centering (arcmin like NINA)
                s.CenteringThresholdArcmin = Math.Max(0.01, options.GetValueDouble(nameof(PlateSolvePlusSettings.CenteringThresholdArcmin), 1.0));
                s.CenteringMaxAttempts = Math.Max(1, options.GetValueInt32(nameof(PlateSolvePlusSettings.CenteringMaxAttempts), 5));

                // Local API
                s.ApiEnabled = options.GetValueBoolean(nameof(PlateSolvePlusSettings.ApiEnabled), false);
                s.ApiPort = options.GetValueInt32(nameof(PlateSolvePlusSettings.ApiPort), 1899);
                s.ApiRequireToken = options.GetValueBoolean(nameof(PlateSolvePlusSettings.ApiRequireToken), false);
                s.ApiToken = options.GetValueString(nameof(PlateSolvePlusSettings.ApiToken), "");
                if (string.IsNullOrWhiteSpace(s.ApiToken)) s.ApiToken = null;


                // Offset quaternion
                s.RotationQw = options.GetValueDouble(nameof(PlateSolvePlusSettings.RotationQw), 1.0);
                s.RotationQx = options.GetValueDouble(nameof(PlateSolvePlusSettings.RotationQx), 0.0);
                s.RotationQy = options.GetValueDouble(nameof(PlateSolvePlusSettings.RotationQy), 0.0);
                s.RotationQz = options.GetValueDouble(nameof(PlateSolvePlusSettings.RotationQz), 0.0);

                // Offset arcsec + enabled
                s.OffsetEnabled = options.GetValueBoolean(nameof(PlateSolvePlusSettings.OffsetEnabled), true);
                s.OffsetRaArcsec = options.GetValueDouble(nameof(PlateSolvePlusSettings.OffsetRaArcsec), 0.0);
                s.OffsetDecArcsec = options.GetValueDouble(nameof(PlateSolvePlusSettings.OffsetDecArcsec), 0.0);

                // OffsetMode stored as int 0/1
                var modeInt = options.GetValueInt32("OffsetMode", 0);
                s.OffsetMode = (OffsetMode)Math.Max(0, Math.Min(1, modeInt));

                // Timestamp (new + legacy fallback)
                var dtStr = options.GetValueString(nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc), "");
                if (string.IsNullOrWhiteSpace(dtStr)) {
                    dtStr = options.GetValueString("LastOffsetCalibrationUtc", "");
                }

                s.OffsetLastCalibratedUtc =
                    DateTime.TryParse(dtStr, null, DateTimeStyles.RoundtripKind, out var dt)
                        ? dt
                        : null;

            } finally {
                isLoadingSettings = false;
            }
        }


        public bool IsRotationMode {
            get => Settings.OffsetMode == OffsetMode.Rotation;
            set {
                if (!value) return;
                if (Settings.OffsetMode == OffsetMode.Rotation) return;
                Settings.OffsetMode = OffsetMode.Rotation;
                RaisePropertyChanged(nameof(IsRotationMode));
                RaisePropertyChanged(nameof(IsArcsecMode));
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
            }
        }


        private void PersistSingle(string propertyName) {
            switch (propertyName) {

                // Capture
                case nameof(PlateSolvePlusSettings.GuideExposureSeconds):
                    options.SetValueDouble(propertyName, Settings.GuideExposureSeconds); break;
                case nameof(PlateSolvePlusSettings.GuideGain):
                    options.SetValueInt32(propertyName, Settings.GuideGain); break;
                case nameof(PlateSolvePlusSettings.GuideBinning):
                    options.SetValueInt32(propertyName, Settings.GuideBinning); break;

                // Optics
                case nameof(PlateSolvePlusSettings.GuideFocalLengthMm):
                    options.SetValueDouble(propertyName, Settings.GuideFocalLengthMm); break;
                case nameof(PlateSolvePlusSettings.UseCameraPixelSize):
                    options.SetValueBoolean(propertyName, Settings.UseCameraPixelSize); break;
                case nameof(PlateSolvePlusSettings.GuidePixelSizeUm):
                    options.SetValueDouble(propertyName, Settings.GuidePixelSizeUm); break;

                // Solver
                case nameof(PlateSolvePlusSettings.SolverSearchRadiusDeg):
                    options.SetValueDouble(propertyName, Settings.SolverSearchRadiusDeg); break;
                case nameof(PlateSolvePlusSettings.SolverDownsample):
                    options.SetValueInt32(propertyName, Settings.SolverDownsample); break;
                case nameof(PlateSolvePlusSettings.SolverTimeoutSec):
                    options.SetValueInt32(propertyName, Settings.SolverTimeoutSec); break;

                // Centering
                case nameof(PlateSolvePlusSettings.CenteringThresholdArcmin):
                    options.SetValueDouble(propertyName, Settings.CenteringThresholdArcmin); break;
                case nameof(PlateSolvePlusSettings.CenteringMaxAttempts):
                    options.SetValueInt32(propertyName, Settings.CenteringMaxAttempts); break;

                // Web API
                case nameof(PlateSolvePlusSettings.ApiEnabled):
                    options.SetValueBoolean(propertyName, Settings.ApiEnabled);
                    break;
                case nameof(PlateSolvePlusSettings.ApiPort):
                    options.SetValueInt32(propertyName, Settings.ApiPort);
                    break;
                case nameof(PlateSolvePlusSettings.ApiRequireToken):
                    options.SetValueBoolean(propertyName, Settings.ApiRequireToken);
                    break;
                case nameof(PlateSolvePlusSettings.ApiToken):
                    options.SetValueString(propertyName, Settings.ApiToken ?? "");
                    break;

                // Offset enabled + arcsec
                case nameof(PlateSolvePlusSettings.OffsetEnabled):
                    options.SetValueBoolean(propertyName, Settings.OffsetEnabled); break;
                case nameof(PlateSolvePlusSettings.OffsetRaArcsec):
                    options.SetValueDouble(propertyName, Settings.OffsetRaArcsec); break;
                case nameof(PlateSolvePlusSettings.OffsetDecArcsec):
                    options.SetValueDouble(propertyName, Settings.OffsetDecArcsec); break;

                // Quaternion
                case nameof(PlateSolvePlusSettings.RotationQw):
                    options.SetValueDouble(propertyName, Settings.RotationQw); break;
                case nameof(PlateSolvePlusSettings.RotationQx):
                    options.SetValueDouble(propertyName, Settings.RotationQx); break;
                case nameof(PlateSolvePlusSettings.RotationQy):
                    options.SetValueDouble(propertyName, Settings.RotationQy); break;
                case nameof(PlateSolvePlusSettings.RotationQz):
                    options.SetValueDouble(propertyName, Settings.RotationQz); break;

                // Mode
                case nameof(PlateSolvePlusSettings.OffsetMode):
                case nameof(PlateSolvePlusSettings.OffsetModeInt):
                    options.SetValueInt32("OffsetMode", (int)Settings.OffsetMode);
                    break;

                // Timestamp (dual write)
                case nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc):
                    var sdt = Settings.OffsetLastCalibratedUtc.HasValue
                        ? Settings.OffsetLastCalibratedUtc.Value.ToString("o")
                        : "";

                    options.SetValueString(nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc), sdt);
                    options.SetValueString("LastOffsetCalibrationUtc", sdt);
                    break;
            }
        }

        private object? GetCurrentValue(string propertyName) {
            return propertyName switch {

                nameof(PlateSolvePlusSettings.GuideExposureSeconds) => Settings.GuideExposureSeconds,
                nameof(PlateSolvePlusSettings.GuideGain) => Settings.GuideGain,
                nameof(PlateSolvePlusSettings.GuideBinning) => Settings.GuideBinning,

                nameof(PlateSolvePlusSettings.GuideFocalLengthMm) => Settings.GuideFocalLengthMm,
                nameof(PlateSolvePlusSettings.UseCameraPixelSize) => Settings.UseCameraPixelSize,
                nameof(PlateSolvePlusSettings.GuidePixelSizeUm) => Settings.GuidePixelSizeUm,

                nameof(PlateSolvePlusSettings.SolverSearchRadiusDeg) => Settings.SolverSearchRadiusDeg,
                nameof(PlateSolvePlusSettings.SolverDownsample) => Settings.SolverDownsample,
                nameof(PlateSolvePlusSettings.SolverTimeoutSec) => Settings.SolverTimeoutSec,

                nameof(PlateSolvePlusSettings.CenteringThresholdArcmin) => Settings.CenteringThresholdArcmin,
                nameof(PlateSolvePlusSettings.CenteringMaxAttempts) => Settings.CenteringMaxAttempts,

                nameof(PlateSolvePlusSettings.OffsetEnabled) => Settings.OffsetEnabled,
                nameof(PlateSolvePlusSettings.OffsetRaArcsec) => Settings.OffsetRaArcsec,
                nameof(PlateSolvePlusSettings.OffsetDecArcsec) => Settings.OffsetDecArcsec,

                nameof(PlateSolvePlusSettings.RotationQw) => Settings.RotationQw,
                nameof(PlateSolvePlusSettings.RotationQx) => Settings.RotationQx,
                nameof(PlateSolvePlusSettings.RotationQy) => Settings.RotationQy,
                nameof(PlateSolvePlusSettings.RotationQz) => Settings.RotationQz,

                nameof(PlateSolvePlusSettings.OffsetLastCalibratedUtc) => Settings.OffsetLastCalibratedUtc,

                nameof(PlateSolvePlusSettings.OffsetMode) => Settings.OffsetMode,
                nameof(PlateSolvePlusSettings.OffsetModeInt) => Settings.OffsetModeInt,

                nameof(PlateSolvePlusSettings.ApiEnabled) => Settings.ApiEnabled,
                nameof(PlateSolvePlusSettings.ApiPort) => Settings.ApiPort,
                nameof(PlateSolvePlusSettings.ApiRequireToken) => Settings.ApiRequireToken,
                nameof(PlateSolvePlusSettings.ApiToken) => Settings.ApiToken,

                _ => null
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        }
    }
}
