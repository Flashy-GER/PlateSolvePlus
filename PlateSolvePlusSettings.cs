using NINA.Core.Utility;
using System;

namespace NINA.Plugins.PlateSolvePlus {
    public enum OffsetMode {
        Rotation = 0,
        Arcsec = 1
    }

    public class PlateSolvePlusSettings : BaseINPC {
        private bool offsetEnabled = true;
        public bool OffsetEnabled {
            get => offsetEnabled;
            set { offsetEnabled = value; RaisePropertyChanged(); }
        }

        private OffsetMode offsetMode = OffsetMode.Rotation;
        public OffsetMode OffsetMode {
            get => offsetMode;
            set { offsetMode = value; RaisePropertyChanged(); }
        }

        // Legacy Arcsec Offset (still supported as fallback / debugging)
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

        // Rotation offset stored as quaternion (w, x, y, z)
        // Default identity rotation
        private double rotationQw = 1.0;
        public double RotationQw {
            get => rotationQw;
            set { rotationQw = value; RaisePropertyChanged(); }
        }

        private double rotationQx = 0.0;
        public double RotationQx {
            get => rotationQx;
            set { rotationQx = value; RaisePropertyChanged(); }
        }

        private double rotationQy = 0.0;
        public double RotationQy {
            get => rotationQy;
            set { rotationQy = value; RaisePropertyChanged(); }
        }

        private double rotationQz = 0.0;
        public double RotationQz {
            get => rotationQz;
            set { rotationQz = value; RaisePropertyChanged(); }
        }

        private DateTime? lastOffsetCalibrationUtc;
        public DateTime? LastOffsetCalibrationUtc {
            get => lastOffsetCalibrationUtc;
            set { lastOffsetCalibrationUtc = value; RaisePropertyChanged(); }
        }

        public void ResetOffset() {
            OffsetRaArcsec = 0;
            OffsetDecArcsec = 0;

            // identity rotation
            RotationQw = 1;
            RotationQx = 0;
            RotationQy = 0;
            RotationQz = 0;

            LastOffsetCalibrationUtc = null;
        }
    }
}
