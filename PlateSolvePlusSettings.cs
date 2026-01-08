using NINA.Core.Utility;
using System;

namespace NINA.Plugins.PlateSolvePlus {

    public enum OffsetMode {
        Rotation = 0,
        Arcsec = 1
    }

    public class PlateSolvePlusSettings : BaseINPC {

        // =========================
        // Guider Capture
        // =========================
        private double guideExposureSeconds = 2.0;
        public double GuideExposureSeconds {
            get => guideExposureSeconds;
            set {
                var v = Math.Max(0.001, value);
                if (Math.Abs(guideExposureSeconds - v) < 0.000001) return;
                guideExposureSeconds = v;
                RaisePropertyChanged();
            }
        }

        private int guideGain = -1;
        public int GuideGain {
            get => guideGain;
            set {
                if (guideGain == value) return;
                guideGain = value;
                RaisePropertyChanged();
            }
        }

        private int guideBinning = 1;
        public int GuideBinning {
            get => guideBinning;
            set {
                var v = Math.Max(1, value);
                if (guideBinning == v) return;
                guideBinning = v;
                RaisePropertyChanged();
            }
        }

        // =========================
        // Optics / Scale
        // =========================
        private double guideFocalLengthMm = 240.0;
        public double GuideFocalLengthMm {
            get => guideFocalLengthMm;
            set {
                var v = Math.Max(1.0, value);
                if (Math.Abs(guideFocalLengthMm - v) < 0.000001) return;
                guideFocalLengthMm = v;
                RaisePropertyChanged();
            }
        }

        private bool useCameraPixelSize = true;
        public bool UseCameraPixelSize {
            get => useCameraPixelSize;
            set {
                if (useCameraPixelSize == value) return;
                useCameraPixelSize = value;
                RaisePropertyChanged();
            }
        }

        private double guidePixelSizeUm = 3.75;
        public double GuidePixelSizeUm {
            get => guidePixelSizeUm;
            set {
                var v = Math.Max(0.1, value);
                if (Math.Abs(guidePixelSizeUm - v) < 0.000001) return;
                guidePixelSizeUm = v;
                RaisePropertyChanged();
            }
        }

        // =========================
        // Solver
        // =========================
        private double solverSearchRadiusDeg = 5.0;
        public double SolverSearchRadiusDeg {
            get => solverSearchRadiusDeg;
            set {
                var v = Math.Max(0.1, value);
                if (Math.Abs(solverSearchRadiusDeg - v) < 0.000001) return;
                solverSearchRadiusDeg = v;
                RaisePropertyChanged();
            }
        }

        private int solverDownsample = 2;
        public int SolverDownsample {
            get => solverDownsample;
            set {
                var v = Math.Max(0, value);
                if (solverDownsample == v) return;
                solverDownsample = v;
                RaisePropertyChanged();
            }
        }

        private int solverTimeoutSec = 60;
        public int SolverTimeoutSec {
            get => solverTimeoutSec;
            set {
                var v = Math.Max(1, value);
                if (solverTimeoutSec == v) return;
                solverTimeoutSec = v;
                RaisePropertyChanged();
            }
        }

        // =========================
        // Centering (like NINA)
        // =========================
        private double centeringThresholdArcmin = 1.0;
        /// <summary>
        /// Centering tolerance in arcminutes (same unit NINA uses for centering threshold).
        /// </summary>
        public double CenteringThresholdArcmin {
            get => centeringThresholdArcmin;
            set {
                var v = Math.Max(0.01, value);
                if (Math.Abs(centeringThresholdArcmin - v) < 0.000001) return;
                centeringThresholdArcmin = v;
                RaisePropertyChanged();
            }
        }

        private int centeringMaxAttempts = 5;
        /// <summary>
        /// Maximum number of sync/slew iterations during Capture+Slew/Sync.
        /// </summary>
        public int CenteringMaxAttempts {
            get => centeringMaxAttempts;
            set {
                var v = Math.Max(1, value);
                if (centeringMaxAttempts == v) return;
                centeringMaxAttempts = v;
                RaisePropertyChanged();
            }
        }

        // =========================
        // Offset
        // =========================
        private bool offsetEnabled = true;
        public bool OffsetEnabled {
            get => offsetEnabled;
            set {
                if (offsetEnabled == value) return;
                offsetEnabled = value;
                RaisePropertyChanged();
            }
        }

        private OffsetMode offsetMode = OffsetMode.Rotation;
        public OffsetMode OffsetMode {
            get => offsetMode;
            set {
                if (offsetMode == value) return;
                offsetMode = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(OffsetModeInt)); // compat for int-based bindings/code
            }
        }

        // Compatibility: some code/UI may use int (0/1) instead of enum
        public int OffsetModeInt {
            get => (int)OffsetMode;
            set => OffsetMode = (OffsetMode)Math.Max(0, Math.Min(1, value));
        }

        private double offsetRaArcsec = 0.0;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            set { if (Math.Abs(offsetRaArcsec - value) < 0.000001) return; offsetRaArcsec = value; RaisePropertyChanged(); }
        }

        private double offsetDecArcsec = 0.0;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            set { if (Math.Abs(offsetDecArcsec - value) < 0.000001) return; offsetDecArcsec = value; RaisePropertyChanged(); }
        }

        private double rotationQw = 1.0;
        public double RotationQw {
            get => rotationQw;
            set { if (Math.Abs(rotationQw - value) < 0.000001) return; rotationQw = value; RaisePropertyChanged(); }
        }

        private double rotationQx = 0.0;
        public double RotationQx {
            get => rotationQx;
            set { if (Math.Abs(rotationQx - value) < 0.000001) return; rotationQx = value; RaisePropertyChanged(); }
        }

        private double rotationQy = 0.0;
        public double RotationQy {
            get => rotationQy;
            set { if (Math.Abs(rotationQy - value) < 0.000001) return; rotationQy = value; RaisePropertyChanged(); }
        }

        private double rotationQz = 0.0;
        public double RotationQz {
            get => rotationQz;
            set { if (Math.Abs(rotationQz - value) < 0.000001) return; rotationQz = value; RaisePropertyChanged(); }
        }

        private DateTime? offsetLastCalibratedUtc;
        public DateTime? OffsetLastCalibratedUtc {
            get => offsetLastCalibratedUtc;
            set {
                if (offsetLastCalibratedUtc == value) return;
                offsetLastCalibratedUtc = value;
                RaisePropertyChanged();
            }
        }

        public void ResetOffset() {
            OffsetRaArcsec = 0;
            OffsetDecArcsec = 0;

            RotationQw = 1;
            RotationQx = 0;
            RotationQy = 0;
            RotationQz = 0;

            OffsetLastCalibratedUtc = null;
        }
    }
}
