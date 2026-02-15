﻿using NINA.Core.Utility;
using NINA.Plugins.PlateSolvePlus.Services;
using System;
using System.ComponentModel;

namespace NINA.Plugins.PlateSolvePlus {

    public enum OffsetMode {
        Rotation = 0,
        Arcsec = 1
    }

    public enum HfrMetric {
        Median = 0,
        Mean = 1,
        BestMedian = 2
    }
    public enum BacklashMode {
        None = 0,
        OvershootReturn = 1,
        OneWayApproach = 2
    }

    public class PlateSolvePlusSettings : BaseINPC {

        public PlateSolvePlusSettings() {
            // IMPORTANT: bubble nested changes up to root so NINA persists them
            HookSecondaryAutofocus(SecondaryAutofocus);
            // Ensure nested runtime object mirrors persisted Af* on startup
            SyncAfToSecondary();
        }

        private void HookSecondaryAutofocus(SecondaryAutofocusSettings? s) {
            if (s == null) return;

            // avoid duplicate subscriptions
            s.PropertyChanged -= SecondaryAutofocusOnPropertyChanged;
            s.PropertyChanged += SecondaryAutofocusOnPropertyChanged;
        }

        public void SyncAfToSecondary() {
            SecondaryAutofocus.ExposureSeconds = AfExposureSeconds;
            SecondaryAutofocus.Gain = AfGain;
            SecondaryAutofocus.BinX = AfBinX;
            SecondaryAutofocus.BinY = AfBinY;
            SecondaryAutofocus.StepSize = AfStepSize;
            SecondaryAutofocus.StepsOut = AfStepsOut;
            SecondaryAutofocus.StepsIn = AfStepsIn;
            SecondaryAutofocus.SettleTimeMs = AfSettleTimeMs;
            SecondaryAutofocus.BacklashSteps = AfBacklashSteps;
            SecondaryAutofocus.BacklashMode = AfBacklashMode;
            SecondaryAutofocus.TimeoutSeconds = AfTimeoutSeconds;
        }

        // Keep persisted Af* backing fields in sync when SecondaryAutofocus.* changes
        private void SecondaryAutofocusOnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            // Mark root as dirty so options persistence triggers
            RaisePropertyChanged(nameof(SecondaryAutofocus));

            // Mirror nested settings back into persisted Af* backing fields
            // (update backing fields directly to avoid recursion)
            switch (e.PropertyName) {
                case nameof(SecondaryAutofocusSettings.ExposureSeconds):
                    afExposureSeconds = SecondaryAutofocus.ExposureSeconds;
                    RaisePropertyChanged(nameof(AfExposureSeconds));
                    break;

                case nameof(SecondaryAutofocusSettings.Gain):
                    afGain = SecondaryAutofocus.Gain;
                    RaisePropertyChanged(nameof(AfGain));
                    break;

                case nameof(SecondaryAutofocusSettings.BinX):
                    afBinX = SecondaryAutofocus.BinX;
                    RaisePropertyChanged(nameof(AfBinX));
                    break;

                case nameof(SecondaryAutofocusSettings.BinY):
                    afBinY = SecondaryAutofocus.BinY;
                    RaisePropertyChanged(nameof(AfBinY));
                    break;

                case nameof(SecondaryAutofocusSettings.StepSize):
                    afStepSize = SecondaryAutofocus.StepSize;
                    RaisePropertyChanged(nameof(AfStepSize));
                    break;

                case nameof(SecondaryAutofocusSettings.StepsOut):
                    afStepsOut = SecondaryAutofocus.StepsOut;
                    RaisePropertyChanged(nameof(AfStepsOut));
                    break;

                case nameof(SecondaryAutofocusSettings.StepsIn):
                    afStepsIn = SecondaryAutofocus.StepsIn;
                    RaisePropertyChanged(nameof(AfStepsIn));
                    break;

                case nameof(SecondaryAutofocusSettings.SettleTimeMs):
                    afSettleTimeMs = SecondaryAutofocus.SettleTimeMs;
                    RaisePropertyChanged(nameof(AfSettleTimeMs));
                    break;

                case nameof(SecondaryAutofocusSettings.BacklashSteps):
                    afBacklashSteps = SecondaryAutofocus.BacklashSteps;
                    RaisePropertyChanged(nameof(AfBacklashSteps));
                    break;

                case nameof(SecondaryAutofocusSettings.BacklashMode):
                    afBacklashMode = SecondaryAutofocus.BacklashMode;
                    RaisePropertyChanged(nameof(AfBacklashMode));
                    break;

                case nameof(SecondaryAutofocusSettings.TimeoutSeconds):
                    afTimeoutSeconds = SecondaryAutofocus.TimeoutSeconds;
                    RaisePropertyChanged(nameof(AfTimeoutSeconds));
                    break;
            }
        }

        private SecondaryAutofocusSettings secondaryAutofocus = new SecondaryAutofocusSettings();
        public SecondaryAutofocusSettings SecondaryAutofocus {
            get => secondaryAutofocus;
            set {
                if (ReferenceEquals(secondaryAutofocus, value)) return;

                if (secondaryAutofocus != null)
                    secondaryAutofocus.PropertyChanged -= SecondaryAutofocusOnPropertyChanged;

                secondaryAutofocus = value ?? new SecondaryAutofocusSettings();
                HookSecondaryAutofocus(secondaryAutofocus);
                RaisePropertyChanged();
            }
        }

        // -------------------------
        // AF persisted fields
        // -------------------------
        private double afExposureSeconds = 5.0;
        public double AfExposureSeconds {
            get => afExposureSeconds;
            set {
                if (Math.Abs(afExposureSeconds - value) < 1e-6) return;
                afExposureSeconds = value;
                SecondaryAutofocus.ExposureSeconds = value;   // keep runtime object in sync
                RaisePropertyChanged();
            }
        }

        private int afGain = 0;
        public int AfGain {
            get => afGain;
            set {
                if (afGain == value) return;
                afGain = value;
                SecondaryAutofocus.Gain = value;
                RaisePropertyChanged();
            }
        }

        private int afBinX = 1;
        public int AfBinX {
            get => afBinX;
            set {
                if (afBinX == value) return;
                afBinX = value;
                SecondaryAutofocus.BinX = value;
                RaisePropertyChanged();
            }
        }

        private int afBinY = 1;
        public int AfBinY {
            get => afBinY;
            set {
                if (afBinY == value) return;
                afBinY = value;
                SecondaryAutofocus.BinY = value;
                RaisePropertyChanged();
            }
        }

        private int afStepSize = 40;
        public int AfStepSize {
            get => afStepSize;
            set {
                if (afStepSize == value) return;
                afStepSize = value;
                SecondaryAutofocus.StepSize = value;
                RaisePropertyChanged();
            }
        }

        private int afStepsOut = 4;
        public int AfStepsOut {
            get => afStepsOut;
            set {
                if (afStepsOut == value) return;
                afStepsOut = value;
                SecondaryAutofocus.StepsOut = value;
                RaisePropertyChanged();
            }
        }

        private int afStepsIn = 4;
        public int AfStepsIn {
            get => afStepsIn;
            set {
                if (afStepsIn == value) return;
                afStepsIn = value;
                SecondaryAutofocus.StepsIn = value;
                RaisePropertyChanged();
            }
        }

        private int afSettleTimeMs = 400;
        public int AfSettleTimeMs {
            get => afSettleTimeMs;
            set {
                if (afSettleTimeMs == value) return;
                afSettleTimeMs = value;
                SecondaryAutofocus.SettleTimeMs = value;
                RaisePropertyChanged();
            }
        }

        private int afBacklashSteps = 0;
        public int AfBacklashSteps {
            get => afBacklashSteps;
            set {
                if (afBacklashSteps == value) return;
                afBacklashSteps = value;
                SecondaryAutofocus.BacklashSteps = value;
                RaisePropertyChanged();
            }
        }

        private BacklashMode afBacklashMode = BacklashMode.OvershootReturn;
        public BacklashMode AfBacklashMode {
            get => afBacklashMode;
            set {
                if (afBacklashMode == value) return;
                afBacklashMode = value;
                SecondaryAutofocus.BacklashMode = value;
                RaisePropertyChanged();
            }
        }

        private int afTimeoutSeconds = 180;
        public int AfTimeoutSeconds {
            get => afTimeoutSeconds;
            set {
                if (afTimeoutSeconds == value) return;
                afTimeoutSeconds = value;
                SecondaryAutofocus.TimeoutSeconds = value;
                RaisePropertyChanged();
            }
        }


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
        private readonly OffsetService offsetService = new();

        public string OffsetQuaternionText => offsetService.GetQuaternionText(this);
        public string OffsetRotationDegText => offsetService.GetRotationAngleText(this);

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
                RaisePropertyChanged(nameof(OffsetModeInt));
            }
        }

        public int OffsetModeInt {
            get => (int)OffsetMode;
            set => OffsetMode = (OffsetMode)Math.Max(0, Math.Min(1, value));
        }

        private double offsetRaArcsec = 0.0;
        public double OffsetRaArcsec {
            get => offsetRaArcsec;
            set {
                if (Math.Abs(offsetRaArcsec - value) < 0.000001) return;
                offsetRaArcsec = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
            }
        }

        private double offsetDecArcsec = 0.0;
        public double OffsetDecArcsec {
            get => offsetDecArcsec;
            set {
                if (Math.Abs(offsetDecArcsec - value) < 0.000001) return;
                offsetDecArcsec = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
            }
        }

        private double rotationQw = 1.0;
        public double RotationQw {
            get => rotationQw;
            set {
                if (Math.Abs(rotationQw - value) < 0.000001) return;
                rotationQw = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
                OnPropertyChanged(nameof(OffsetQuaternionText));
                OnPropertyChanged(nameof(OffsetRotationDegText));
            }
        }

        private double rotationQx = 0.0;
        public double RotationQx {
            get => rotationQx;
            set {
                if (Math.Abs(rotationQx - value) < 0.000001) return;
                rotationQx = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
                OnPropertyChanged(nameof(OffsetQuaternionText));
                OnPropertyChanged(nameof(OffsetRotationDegText));
            }
        }

        private double rotationQy = 0.0;
        public double RotationQy {
            get => rotationQy;
            set {
                if (Math.Abs(rotationQy - value) < 0.000001) return;
                rotationQy = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
                OnPropertyChanged(nameof(OffsetQuaternionText));
                OnPropertyChanged(nameof(OffsetRotationDegText));
            }
        }

        private double rotationQz = 0.0;
        public double RotationQz {
            get => rotationQz;
            set {
                if (Math.Abs(rotationQz - value) < 0.000001) return;
                rotationQz = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasOffsetSet));
                OnPropertyChanged(nameof(OffsetQuaternionText));
                OnPropertyChanged(nameof(OffsetRotationDegText));
            }
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

        // =========================
        // Local WEB API settings
        // =========================
        private bool apiEnabled = true;
        public bool ApiEnabled {
            get => apiEnabled;
            set { if (apiEnabled == value) return; apiEnabled = value; RaisePropertyChanged(nameof(ApiEnabled)); }
        }

        private int apiPort = 1899;
        public int ApiPort {
            get => apiPort;
            set {
                var v = value < 1024 ? 1899 : value;
                if (apiPort == v) return;
                apiPort = v;
                OnPropertyChanged(nameof(ApiPort));
            }
        }

        private bool apiRequireToken = false;
        public bool ApiRequireToken {
            get => apiRequireToken;
            set { if (apiRequireToken == value) return; apiRequireToken = value; RaisePropertyChanged(nameof(ApiRequireToken)); }
        }

        private string? apiToken;
        public string? ApiToken {
            get => apiToken;
            set { if (string.Equals(apiToken, value, StringComparison.Ordinal)) return; apiToken = value; RaisePropertyChanged(nameof(ApiToken)); }
        }

        // =========================
        // Autofocus settings
        // =========================
        private bool afBlock = false;
        public bool AFBlock {
            get => afBlock;
            set {
                if (afBlock == value) return;
                afBlock = value;
                RaisePropertyChanged();
            }
        }

        // =========================
        // Derived / UI helpers
        // =========================
        /// <summary>
        /// True when an offset is effectively set (rotation quaternion not identity OR arcsec deltas not zero).
        /// Note: OffsetEnabled/OffsetMode are treated as legacy UI toggles and are intentionally ignored here.
        /// </summary>
        public bool HasOffsetSet {
            get {
                var isIdentity = Math.Abs(RotationQw - 1.0) < 1e-6 &&
                                 Math.Abs(RotationQx) < 1e-6 &&
                                 Math.Abs(RotationQy) < 1e-6 &&
                                 Math.Abs(RotationQz) < 1e-6;

                if (!isIdentity) return true;

                return Math.Abs(OffsetRaArcsec) > 1e-6 || Math.Abs(OffsetDecArcsec) > 1e-6;
            }
        }

        public bool PreviewDebayerEnabled { get; internal set; }
        public bool PreviewAutoStretchEnabled { get; internal set; }
        public bool PreviewUnlinkedStretchEnabled { get; internal set; }
    }
}
