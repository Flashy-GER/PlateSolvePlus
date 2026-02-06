using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    /// <summary>
    /// Prototype star metric: find bright peaks and compute an approximate Half-Flux Radius.
    /// Works directly on int[] pixel buffers (row-major).
    /// </summary>
    public sealed class BasicHfrMetricService : IStarMetricService {
        public Task<StarMetricResult> MeasureAsync(SecondaryFrame frame, SecondaryAutofocusSettings settings, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            int w = frame.Width;
            int h = frame.Height;
            if (w < 32 || h < 32) return Task.FromResult(new StarMetricResult(double.NaN, 0));

            var img = frame.Pixels;

            // Robust threshold: mean + 3*std (quick)
            double mean = 0;
            for (int i = 0; i < img.Length; i++) mean += img[i];
            mean /= img.Length;

            double var = 0;
            for (int i = 0; i < img.Length; i++) {
                double d = img[i] - mean;
                var += d * d;
            }
            var /= img.Length;
            double std = Math.Sqrt(var);

            double thr = mean + 3.0 * std;

            // Find brightest peaks (candidates)
            int want = Math.Max(settings.MinStars, Math.Min(settings.MaxStars, 250));
            var peaks = FindPeaks(img, w, h, thr, want);

            if (peaks.Count < settings.MinStars)
                return Task.FromResult(new StarMetricResult(double.NaN, peaks.Count));

            var hfrs = new List<double>(peaks.Count);
            foreach (var (x, y) in peaks) {
                ct.ThrowIfCancellationRequested();
                var hfr = ComputeHalfFluxRadius(img, w, h, x, y, radius: 8);
                if (!double.IsNaN(hfr) && hfr > 0.1 && hfr < 50)
                    hfrs.Add(hfr);
            }

            if (hfrs.Count < Math.Max(3, settings.MinStars / 2))
                return Task.FromResult(new StarMetricResult(double.NaN, peaks.Count));

            double agg = settings.HfrMetric switch {
                HfrMetric.Mean =>
                    hfrs.Average(),

                HfrMetric.BestNMedian =>
                    Median(
                        hfrs
                            .OrderBy(v => v)
                            .Take(Math.Max(5, hfrs.Count / 5))
                            .ToList()
                    ),

                _ =>
                    Median(hfrs),
            };

            return Task.FromResult(new StarMetricResult(agg, peaks.Count));
        }

        private static List<(int x, int y)> FindPeaks(int[] img, int w, int h, double thr, int maxPeaks) {
            var peaks = new List<(int x, int y, int v)>(maxPeaks * 2);

            for (int y = 2; y < h - 2; y++) {
                int row = y * w;
                for (int x = 2; x < w - 2; x++) {
                    int v = img[row + x];
                    if (v < thr) continue;

                    // Local max in 3x3
                    if (v >= img[row + x - 1] && v >= img[row + x + 1] &&
                        v >= img[row - w + x] && v >= img[row + w + x] &&
                        v >= img[row - w + x - 1] && v >= img[row - w + x + 1] &&
                        v >= img[row + w + x - 1] && v >= img[row + w + x + 1]) {
                        peaks.Add((x, y, v));
                    }
                }
            }

            return peaks
                .OrderByDescending(p => p.v)
                .Take(maxPeaks)
                .Select(p => (p.x, p.y))
                .ToList();
        }

        private static double ComputeHalfFluxRadius(int[] img, int w, int h, int cx, int cy, int radius) {
            int x0 = Math.Max(0, cx - radius);
            int x1 = Math.Min(w - 1, cx + radius);
            int y0 = Math.Max(0, cy - radius);
            int y1 = Math.Min(h - 1, cy + radius);

            // background = edge ring average
            double bg = 0;
            int bgN = 0;
            for (int y = y0; y <= y1; y++) {
                int row = y * w;
                for (int x = x0; x <= x1; x++) {
                    bool edge = (x == x0 || x == x1 || y == y0 || y == y1);
                    if (!edge) continue;
                    bg += img[row + x];
                    bgN++;
                }
            }
            if (bgN == 0) return double.NaN;
            bg /= bgN;

            var pts = new List<(double r, double f)>(256);
            for (int y = y0; y <= y1; y++) {
                int row = y * w;
                for (int x = x0; x <= x1; x++) {
                    double dx = x - cx;
                    double dy = y - cy;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    double f = img[row + x] - bg;
                    if (f > 0) pts.Add((r, f));
                }
            }
            if (pts.Count < 20) return double.NaN;

            pts.Sort((a, b) => a.r.CompareTo(b.r));
            double total = pts.Sum(p => p.f);
            if (total <= 0) return double.NaN;

            double half = total * 0.5;
            double cum = 0;
            for (int i = 0; i < pts.Count; i++) {
                cum += pts[i].f;
                if (cum >= half) return pts[i].r;
            }
            return pts[^1].r;
        }

        private static double Median(List<double> v) {
            if (v.Count == 0) return double.NaN;
            v.Sort();
            int n = v.Count;
            if ((n & 1) == 1) return v[n / 2];
            return 0.5 * (v[n / 2 - 1] + v[n / 2]);
        }

    }
}
