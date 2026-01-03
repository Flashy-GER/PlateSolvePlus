using NINA.Core.Model;
using NINA.Core.Utility;
using System;
using System.ComponentModel;

namespace NINA.Plugins.PlateSolvePlus {
    // Wenn du schon ein Plugin-Settings-Model hast: einfach die Properties reinziehen.
    public class PlateSolvePlusSettings : BaseINPC {
        private bool offsetEnabled;
        public bool OffsetEnabled {
            get => offsetEnabled;
            set { offsetEnabled = value; RaisePropertyChanged(); }
        }

        private double offsetRaArcsec;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            set { offsetRaArcsec = value; RaisePropertyChanged(); }
        }

        private double offsetDecArcsec;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            set { offsetDecArcsec = value; RaisePropertyChanged(); }
        }

        private DateTime? lastOffsetCalibrationUtc;
        public DateTime? LastOffsetCalibrationUtc {
            get => lastOffsetCalibrationUtc;
            set { lastOffsetCalibrationUtc = value; RaisePropertyChanged(); }
        }

        public void ResetOffset() {
            OffsetRaArcsec = 0;
            OffsetDecArcsec = 0;
            LastOffsetCalibrationUtc = null;
        }
    }
}
