using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Plot {
    public sealed class AutofocusPlotViewModel : INotifyPropertyChanged {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly LineSeries _pointsSeries;
        private readonly LineSeries _fitSeries;
        private readonly ScatterSeries _currentMarker;
        private readonly ScatterSeries _bestMarker;

        private int _totalSteps;
        private string _statusLine = string.Empty;

        public PlotModel Model { get; }

        public string StatusLine {
            get => _statusLine;
            set { _statusLine = value; OnPropertyChanged(); }
        }

        public AutofocusPlotViewModel() {
            Model = new PlotModel { Title = "OAG Autofocus" };

            Model.Axes.Add(new LinearAxis {
                Position = AxisPosition.Bottom,
                Title = "Focus Position",
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            Model.Axes.Add(new LinearAxis {
                Position = AxisPosition.Left,
                Title = "HFR",
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot
            });

            _pointsSeries = new LineSeries {
                Title = "Samples",
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                LineStyle = LineStyle.None // points only
            };

            _fitSeries = new LineSeries {
                Title = "Fit",
                LineStyle = LineStyle.Solid
            };

            _currentMarker = new ScatterSeries {
                Title = "Current",
                MarkerType = MarkerType.Triangle,
                MarkerSize = 5
            };

            _bestMarker = new ScatterSeries {
                Title = "Best",
                MarkerType = MarkerType.Diamond,
                MarkerSize = 6
            };

            Model.Series.Add(_fitSeries);
            Model.Series.Add(_pointsSeries);
            Model.Series.Add(_currentMarker);
            Model.Series.Add(_bestMarker);
        }

        public void StartNewRun(int totalSteps) {
            _totalSteps = totalSteps;
            _pointsSeries.Points.Clear();
            _fitSeries.Points.Clear();
            _currentMarker.Points.Clear();
            _bestMarker.Points.Clear();
            StatusLine = $"Step 0/{_totalSteps}";
            Model.InvalidatePlot(true);
        }

        public void AddSample(int step1Based, int totalSteps, int pos, double hfr, int bestPos, double bestHfr) {
            _totalSteps = totalSteps;

            if (double.IsFinite(hfr)) {
                _pointsSeries.Points.Add(new DataPoint(pos, hfr));
            }

            // current marker
            _currentMarker.Points.Clear();
            if (double.IsFinite(hfr)) {
                _currentMarker.Points.Add(new ScatterPoint(pos, hfr));
            }

            // best marker (from running best)
            _bestMarker.Points.Clear();
            if (double.IsFinite(bestHfr)) {
                _bestMarker.Points.Add(new ScatterPoint(bestPos, bestHfr));
            }

            StatusLine = $"Step {step1Based}/{totalSteps} – HFR {hfr:0.00} – Pos {pos}";
            RecomputeFitIfPossible();

            Model.InvalidatePlot(true);
        }

        public void SetFitBest(int pos, double hfr) {
            // Mark best marker to fit best if caller wants override; we keep it simple:
            _bestMarker.Points.Clear();
            if (double.IsFinite(hfr)) {
                _bestMarker.Points.Add(new ScatterPoint(pos, hfr));
            }
            Model.InvalidatePlot(true);
        }

        private void RecomputeFitIfPossible() {
            var pts = _pointsSeries.Points
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                .ToList();

            if (pts.Count < 3) {
                _fitSeries.Points.Clear();
                return;
            }

            // Quadratic least squares: y = a x^2 + b x + c
            if (!TryFitQuadratic(pts, out var a, out var b, out var c)) {
                _fitSeries.Points.Clear();
                return;
            }

            var minX = pts.Min(p => p.X);
            var maxX = pts.Max(p => p.X);
            if (maxX <= minX) return;

            _fitSeries.Points.Clear();

            const int N = 120;
            var step = (maxX - minX) / (N - 1);

            for (int i = 0; i < N; i++) {
                var x = minX + step * i;
                var y = a * x * x + b * x + c;
                if (double.IsFinite(y)) {
                    _fitSeries.Points.Add(new DataPoint(x, y));
                }
            }
        }

        private static bool TryFitQuadratic(IReadOnlyList<DataPoint> pts, out double a, out double b, out double c) {
            // Normal equations for quadratic regression
            // Solve:
            // [sum x^4 sum x^3 sum x^2] [a] = [sum x^2 y]
            // [sum x^3 sum x^2 sum x  ] [b]   [sum x y]
            // [sum x^2 sum x   n      ] [c]   [sum y]
            a = b = c = 0;

            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0;
            double sy = 0, sxy = 0, sx2y = 0;
            int n = pts.Count;

            for (int i = 0; i < n; i++) {
                var x = pts[i].X;
                var y = pts[i].Y;

                var x2 = x * x;
                sx += x;
                sx2 += x2;
                sx3 += x2 * x;
                sx4 += x2 * x2;

                sy += y;
                sxy += x * y;
                sx2y += x2 * y;
            }

            // 3x3 solve via Cramer's rule (stable enough for typical focus positions)
            double det =
                sx4 * (sx2 * n - sx * sx) -
                sx3 * (sx3 * n - sx * sx2) +
                sx2 * (sx3 * sx - sx2 * sx2);

            if (Math.Abs(det) < 1e-12) return false;

            double detA =
                sx2y * (sx2 * n - sx * sx) -
                sx3 * (sxy * n - sx * sy) +
                sx2 * (sxy * sx - sx2 * sy);

            double detB =
                sx4 * (sxy * n - sx * sy) -
                sx2y * (sx3 * n - sx * sx2) +
                sx2 * (sx3 * sy - sxy * sx2);

            double detC =
                sx4 * (sx2 * sy - sx * sxy) -
                sx3 * (sx3 * sy - sx * sx2y) +
                sx2y * (sx3 * sx - sx2 * sx2);

            a = detA / det;
            b = detB / det;
            c = detC / det;

            return double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
