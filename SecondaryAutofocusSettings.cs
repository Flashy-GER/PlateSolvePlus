using NINA.Core.Utility;
using System;

namespace NINA.Plugins.PlateSolvePlus {

    public class SecondaryAutofocusSettings : BaseINPC {
        private HfrMetric hfrMetric = HfrMetric.Median;
        public HfrMetric HfrMetric {
            get => hfrMetric;
            set { hfrMetric = value; RaisePropertyChanged(); }
        }

        private double exposureSeconds = 2.0;
        public double ExposureSeconds {
            get => exposureSeconds;
            set {
                if (Math.Abs(exposureSeconds - value) < 1e-6) return;
                exposureSeconds = value; RaisePropertyChanged();
            }
        }

        private int gain = 0;
        public int Gain {
            get => gain;
            set { if (gain == value) return; gain = value; RaisePropertyChanged(); }
        }

        private int binX = 1;
        public int BinX {
            get => binX;
            set { if (binX == value) return; binX = value; RaisePropertyChanged(); }
        }

        private int binY = 1;
        public int BinY {
            get => binY;
            set { if (binY == value) return; binY = value; RaisePropertyChanged(); }
        }

        private int stepSize = 40;
        public int StepSize {
            get => stepSize;
            set { if (stepSize == value) return; stepSize = value; RaisePropertyChanged(); }
        }

        private int stepsOut = 4;
        public int StepsOut {
            get => stepsOut;
            set { if (stepsOut == value) return; stepsOut = value; RaisePropertyChanged(); }
        }

        private int stepsIn = 4;
        public int StepsIn {
            get => stepsIn;
            set { if (stepsIn == value) return; stepsIn = value; RaisePropertyChanged(); }
        }

        private int settleTimeMs = 400;
        public int SettleTimeMs {
            get => settleTimeMs;
            set { if (settleTimeMs == value) return; settleTimeMs = value; RaisePropertyChanged(); }
        }

        private int backlashSteps = 0;
        public int BacklashSteps {
            get => backlashSteps;
            set { if (backlashSteps == value) return; backlashSteps = value; RaisePropertyChanged(); }
        }

        private BacklashMode backlashMode = BacklashMode.OvershootReturn;
        public BacklashMode BacklashMode {
            get => backlashMode;
            set { if (backlashMode == value) return; backlashMode = value; RaisePropertyChanged(); }
        }

        private int minStars = 10;
        public int MinStars {
            get => minStars;
            set { if (minStars == value) return; minStars = value; RaisePropertyChanged(); }
        }

        private int maxStars = 250;
        public int MaxStars {
            get => maxStars;
            set { if (maxStars == value) return; maxStars = value; RaisePropertyChanged(); }
        }

        private int timeoutSeconds = 180;
        public int TimeoutSeconds {
            get => timeoutSeconds;
            set { if (timeoutSeconds == value) return; timeoutSeconds = value; RaisePropertyChanged(); }
        }

        private int minFocuserPosition = 0;
        public int MinFocuserPosition {
            get => minFocuserPosition;
            set { if (minFocuserPosition == value) return; minFocuserPosition = value; RaisePropertyChanged(); }
        }

        private int maxFocuserPosition = 0;
        public int MaxFocuserPosition {
            get => maxFocuserPosition;
            set { if (maxFocuserPosition == value) return; maxFocuserPosition = value; RaisePropertyChanged(); }
        }

        public void ApplyFrom(SecondaryAutofocusSettings other) {
            if (other == null) return;

            ExposureSeconds = other.ExposureSeconds;
            Gain = other.Gain;
            BinX = other.BinX;
            BinY = other.BinY;

            StepSize = other.StepSize;
            StepsOut = other.StepsOut;
            StepsIn = other.StepsIn;

            SettleTimeMs = other.SettleTimeMs;

            MinStars = other.MinStars;
            MaxStars = other.MaxStars;
            TimeoutSeconds = other.TimeoutSeconds;

            BacklashSteps = other.BacklashSteps;
            BacklashMode = other.BacklashMode;

            MinFocuserPosition = other.MinFocuserPosition;
            MaxFocuserPosition = other.MaxFocuserPosition;
        }
    }

}


