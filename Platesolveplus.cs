using NINA.Plugin;
using NINA.Plugin.Interfaces;
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

        // For Options.xaml "Delete Rotation Offset"
        public ICommand ResetRotationOffsetCommand { get; }

        private bool isLoadingSettings;

        [ImportingConstructor]
        public Platesolveplus(IProfileService profileService) {
            this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));

            options = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));

            Settings = new PlateSolvePlusSettings();

            ResetRotationOffsetCommand = new SimpleCommand(ResetRotationOffset);

            LoadAllIntoSettings(Settings);

            Settings.PropertyChanged += Settings_PropertyChanged;
            profileService.ProfileChanged += ProfileService_ProfileChanged;
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            LoadAllIntoSettings(Settings);
            RaisePropertyChanged(string.Empty);
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
            PlateSolvePlusSettingsBus.Publish(name, GetCurrentValue(name));

            RaisePropertyChanged(name);
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

                _ => null
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
