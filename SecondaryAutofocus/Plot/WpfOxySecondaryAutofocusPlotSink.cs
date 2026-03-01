using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;
using System;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Plot {
    public sealed class WpfOxySecondaryAutofocusPlotSink : ISecondaryAutofocusPlotSink {
        private readonly Dispatcher _dispatcher;
        private AutofocusPlotWindow? _window;
        private AutofocusPlotViewModel? _vm;

        public WpfOxySecondaryAutofocusPlotSink(Dispatcher dispatcher) {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void StartNewRun(int totalSteps) {
            _dispatcher.BeginInvoke((Action)(() => {
                // Close previous window (new run => new curve)
                try { _window?.Close(); } catch { /* ignore */ }

                _vm = new AutofocusPlotViewModel();
                _vm.StartNewRun(totalSteps);

                _window = new AutofocusPlotWindow {
                    DataContext = _vm,
                    Topmost = true,
                    Owner = Application.Current?.MainWindow
                };

                _window.Show();
                _window.Activate();
            }));
        }

        public void AddSample(FocusSample sample, int stepIndex1Based, int totalSteps, int bestPos, double bestHfr) {
            if (sample == null) return;

            _dispatcher.BeginInvoke((Action)(() => {
                if (_vm == null) return;
                _vm.AddSample(stepIndex1Based, totalSteps, sample.Position, sample.Hfr, bestPos, bestHfr);
            }));
        }

        public void SetFitBest(int bestFromFitPos, double bestFromFitHfr) {
            _dispatcher.BeginInvoke((Action)(() => _vm?.SetFitBest(bestFromFitPos, bestFromFitHfr)));
        }

        public void SetPhase(string statusText) {
            _dispatcher.BeginInvoke((Action)(() => {
                if (_vm == null) return;
                // Keep the existing step prefix, just append phase if present
                if (!string.IsNullOrWhiteSpace(statusText)) {
                    _vm.StatusLine = statusText;
                }
            }));
        }
    }
}
