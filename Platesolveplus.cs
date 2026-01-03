using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus {
    [Export(typeof(IPluginManifest))]
    public class Platesolveplus : PluginBase, INotifyPropertyChanged {
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public Platesolveplus(IProfileService profileService) {
            this.profileService = profileService;

            // Profile-specific settings store
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));

            // React on profile change so Options UI updates
            profileService.ProfileChanged += ProfileService_ProfileChanged;
        }

        public override Task Teardown() {
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
            return base.Teardown();
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            // Raise all properties so the Options UI refreshes when profile switches
            RaisePropertyChanged(string.Empty);
        }

        // =========================
        // PlateSolvePlus settings
        // =========================

        // --- Guider Capture ---
        public double GuideExposureSeconds {
            get => Get(nameof(GuideExposureSeconds), 2.0);
            set => Set(nameof(GuideExposureSeconds), value);
        }

        public int GuideGain {
            get => Get(nameof(GuideGain), -1); // -1 = ignore/auto
            set => Set(nameof(GuideGain), value);
        }

        public int GuideBinning {
            get => Get(nameof(GuideBinning), 1);
            set => Set(nameof(GuideBinning), Math.Max(1, value));
        }

        // --- Optics / Scale ---
        public double GuideFocalLengthMm {
            get => Get(nameof(GuideFocalLengthMm), 240.0);
            set => Set(nameof(GuideFocalLengthMm), Math.Max(1.0, value));
        }

        public bool UseCameraPixelSize {
            get => Get(nameof(UseCameraPixelSize), true);
            set => Set(nameof(UseCameraPixelSize), value);
        }

        public double GuidePixelSizeUm {
            get => Get(nameof(GuidePixelSizeUm), 3.75);
            set => Set(nameof(GuidePixelSizeUm), Math.Max(0.1, value));
        }

        // --- Solver ---
        public double SolverSearchRadiusDeg {
            get => Get(nameof(SolverSearchRadiusDeg), 5.0);
            set => Set(nameof(SolverSearchRadiusDeg), Math.Max(0.1, value));
        }

        public int SolverTimeoutSec {
            get => Get(nameof(SolverTimeoutSec), 60);
            set => Set(nameof(SolverTimeoutSec), Math.Max(5, value));
        }

        public int SolverDownsample {
            get => Get(nameof(SolverDownsample), 2);
            set => Set(nameof(SolverDownsample), Math.Max(1, value));
        }

        // --- Offset ---
        public bool OffsetEnabled {
            get => Get(nameof(OffsetEnabled), false);
            set => Set(nameof(OffsetEnabled), value);
        }

        public double OffsetRaArcsec {
            get => Get(nameof(OffsetRaArcsec), 0.0);
            set => Set(nameof(OffsetRaArcsec), value);
        }

        public double OffsetDecArcsec {
            get => Get(nameof(OffsetDecArcsec), 0.0);
            set => Set(nameof(OffsetDecArcsec), value);
        }

        public DateTime OffsetLastCalibratedUtc {
            get => Get(nameof(OffsetLastCalibratedUtc), DateTime.MinValue);
            set => Set(nameof(OffsetLastCalibratedUtc), value);
        }

        // =========================
        // Helpers (typed get/set)
        // =========================

        private T Get<T>(string key, T defaultValue) {
            // PluginOptionsAccessor supports typed values, but not always generically.
            // We'll route by type to keep it simple & safe.
            if (typeof(T) == typeof(string))
                return (T)(object)pluginSettings.GetValueString(key, defaultValue as string);

            if (typeof(T) == typeof(int))
                return (T)(object)pluginSettings.GetValueInt32(key, Convert.ToInt32(defaultValue));

            if (typeof(T) == typeof(double))
                return (T)(object)pluginSettings.GetValueDouble(key, Convert.ToDouble(defaultValue));

            if (typeof(T) == typeof(bool))
                return (T)(object)pluginSettings.GetValueBoolean(key, Convert.ToBoolean(defaultValue));

            if (typeof(T) == typeof(DateTime)) {
                var s = pluginSettings.GetValueString(key, ((DateTime)(object)defaultValue).ToString("o"));
                return (T)(object)(DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : (DateTime)(object)defaultValue);
            }

            return defaultValue;
        }

        private void Set<T>(string key, T value, [CallerMemberName] string propertyName = null) {
            if (typeof(T) == typeof(string))
                pluginSettings.SetValueString(key, value as string);
            else if (typeof(T) == typeof(int))
                pluginSettings.SetValueInt32(key, Convert.ToInt32(value));
            else if (typeof(T) == typeof(double))
                pluginSettings.SetValueDouble(key, Convert.ToDouble(value));
            else if (typeof(T) == typeof(bool))
                pluginSettings.SetValueBoolean(key, Convert.ToBoolean(value));
            else if (typeof(T) == typeof(DateTime))
                pluginSettings.SetValueString(key, ((DateTime)(object)value).ToString("o"));
            else
                pluginSettings.SetValueString(key, value?.ToString() ?? string.Empty);

            RaisePropertyChanged(propertyName);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
