using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State {
    public sealed class SecondaryAutofocusRunState : ObservableObject {
        private SecondaryAfPhase _phase = SecondaryAfPhase.Idle;
        public SecondaryAfPhase Phase { get => _phase; set => SetProperty(ref _phase, value); }

        private string _status = "";
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        private double _progress;
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); } // 0..1

        private int _currentPosition;
        public int CurrentPosition { get => _currentPosition; set => SetProperty(ref _currentPosition, value); }

        private double _currentHfr;
        public double CurrentHfr { get => _currentHfr; set => SetProperty(ref _currentHfr, value); }

        private int _currentStars;
        public int CurrentStars { get => _currentStars; set => SetProperty(ref _currentStars, value); }

        public ObservableCollection<FocusSample> Samples { get; } = new();

        private int _bestPosition;
        public int BestPosition { get => _bestPosition; set => SetProperty(ref _bestPosition, value); }

        private double _bestHfr = double.NaN;
        public double BestHfr { get => _bestHfr; set => SetProperty(ref _bestHfr, value); }

        private string? _lastError;
        public string? LastError { get => _lastError; set => SetProperty(ref _lastError, value); }
        public string StatusText { get; internal set; } = string.Empty;
    }

    public enum SecondaryAfPhase {
        Idle,
        Preparing,
        Moving,
        Settling,
        Capturing,
        Measuring,
        Fitting,
        MovingToBest,
        Completed,
        Failed,
        Cancelled
    }
}
