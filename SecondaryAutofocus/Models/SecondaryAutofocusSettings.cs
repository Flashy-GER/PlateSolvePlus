using NINA.Core.Utility;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models {
    public enum HfrMetric {
        Median = 0,
        Mean = 1,
        BestNMedian = 2
    }
    public enum BacklashMode {
        None = 0,
        OvershootReturn = 1,
        OneWayApproach = 2
    }
    public class SecondaryAutofocusSettings : BaseINPC {

        private HfrMetric hfrMetric = HfrMetric.Median;
        public HfrMetric HfrMetric {
            get => hfrMetric;
            set { hfrMetric = value; RaisePropertyChanged(); }
        }

        private double exposureSeconds = 2.0;
        public double ExposureSeconds {
            get => exposureSeconds;
            set { exposureSeconds = value; RaisePropertyChanged(); }
        }

        private int gain = 0;
        public int Gain {
            get => gain;
            set { gain = value; RaisePropertyChanged(); }
        }

        private int binX = 1;
        public int BinX {
            get => binX;
            set { binX = value; RaisePropertyChanged(); }
        }

        private int binY = 1;
        public int BinY {
            get => binY;
            set { binY = value; RaisePropertyChanged(); }
        }

        private int stepSize = 40;
        public int StepSize {
            get => stepSize;
            set { stepSize = value; RaisePropertyChanged(); }
        }

        private int stepsOut = 4;
        public int StepsOut {
            get => stepsOut;
            set { stepsOut = value; RaisePropertyChanged(); }
        }

        private int stepsIn = 4;
        public int StepsIn {
            get => stepsIn;
            set { stepsIn = value; RaisePropertyChanged(); }
        }

        private int minFocuserPosition = 1;
        public int MinFocuserPosition {
            get => minFocuserPosition;
            set { minFocuserPosition = value; RaisePropertyChanged(); }
        }
        private int maxFocuserPosition = 120000;
        public int MaxFocuserPosition {
            get => maxFocuserPosition;
            set { maxFocuserPosition = value; RaisePropertyChanged(); }
        }


        private int settleTimeMs = 400;
        public int SettleTimeMs {
            get => settleTimeMs;
            set { settleTimeMs = value; RaisePropertyChanged(); }
        }

        private int minStars = 10;
        public int MinStars {
            get => minStars;
            set { minStars = value; RaisePropertyChanged(); }
        }

        private int maxStars = 250;
        public int MaxStars {
            get => maxStars;
            set { maxStars = value; RaisePropertyChanged(); }
        }

        private int timeoutSeconds = 180;
        public int TimeoutSeconds {
            get => timeoutSeconds;
            set { timeoutSeconds = value; RaisePropertyChanged(); }
        }

        private int backlashSteps = 0;
        public int BacklashSteps {
            get => backlashSteps;
            set { backlashSteps = value; RaisePropertyChanged(); }
        }

        private BacklashMode backlashMode = BacklashMode.None;
        public BacklashMode BacklashMode {
            get => backlashMode;
            set { backlashMode = value; RaisePropertyChanged(); }
        }
    }

    public partial class PlateSolvePlusSettings : BaseINPC {
        private SecondaryAutofocusSettings secondaryAutofocus = new SecondaryAutofocusSettings();

        public SecondaryAutofocusSettings SecondaryAutofocus {
            get => secondaryAutofocus;
            set {
                secondaryAutofocus = value;
                RaisePropertyChanged();
            }
        }
    }
}


