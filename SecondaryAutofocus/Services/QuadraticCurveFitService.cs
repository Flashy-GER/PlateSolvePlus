using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public sealed class QuadraticCurveFitService : ICurveFitService {
        public CurveFitResult Fit(IReadOnlyList<FocusSample> samples) {
            // Fit y = ax^2 + bx + c via normal equations
            var pts = samples
                .Where(s => !double.IsNaN(s.Hfr) && s.StarCount > 0)
                .Select(s => (x: (double)s.Position, y: s.Hfr))
                .ToList();

            if (pts.Count < 5)
                return new CurveFitResult("Quadratic", 0, 0, 0, 0, pts.Count > 0 ? pts[0].x : 0);

            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0;
            double sy = 0, sxy = 0, sx2y = 0;

            foreach (var p in pts) {
                double x = p.x;
                double x2 = x * x;
                double x3 = x2 * x;
                double x4 = x2 * x2;

                sx += x;
                sx2 += x2;
                sx3 += x3;
                sx4 += x4;

                sy += p.y;
                sxy += x * p.y;
                sx2y += x2 * p.y;
            }

            // Solve:
            // [ n   sx   sx2 ] [ c ]   [ sy   ]
            // [ sx  sx2  sx3 ] [ b ] = [ sxy  ]
            // [ sx2 sx3  sx4 ] [ a ]   [ sx2y ]
            double n = pts.Count;

            // Build matrix
            double[,] A =
            {
                { n,  sx,  sx2 },
                { sx, sx2, sx3 },
                { sx2,sx3, sx4 }
            };
            double[] B = { sy, sxy, sx2y };

            var (c, b, a) = Solve3x3(A, B);

            double xBest = (Math.Abs(a) < 1e-12) ? pts.OrderBy(p => p.y).First().x : (-b / (2 * a));

            // R^2
            double yMean = sy / n;
            double ssTot = pts.Sum(p => (p.y - yMean) * (p.y - yMean));
            double ssRes = pts.Sum(p => {
                double yHat = a * p.x * p.x + b * p.x + c;
                double e = p.y - yHat;
                return e * e;
            });
            double r2 = (ssTot <= 1e-12) ? 0 : Math.Max(0, 1.0 - (ssRes / ssTot));

            return new CurveFitResult("Quadratic", r2, a, b, c, xBest);
        }

        public int GetBestPosition(CurveFitResult fit, int fallbackBest, int minPos, int maxPos) {
            int est = (int)Math.Round(fit.EstimatedBestPosition);
            if (minPos < maxPos) {
                if (est < minPos) est = minPos;
                if (est > maxPos) est = maxPos;
            }
            if (est == 0) est = fallbackBest;
            return est;
        }

        private static (double c, double b, double a) Solve3x3(double[,] m, double[] v) {
            // Gaussian elimination (small, stable enough for AF)
            double[,] a = (double[,])m.Clone();
            double[] b = (double[])v.Clone();

            for (int i = 0; i < 3; i++) {
                // Pivot
                int piv = i;
                double best = Math.Abs(a[i, i]);
                for (int r = i + 1; r < 3; r++) {
                    double val = Math.Abs(a[r, i]);
                    if (val > best) { best = val; piv = r; }
                }
                if (piv != i) {
                    for (int c = 0; c < 3; c++) {
                        (a[i, c], a[piv, c]) = (a[piv, c], a[i, c]);
                    }
                    (b[i], b[piv]) = (b[piv], b[i]);
                }

                double diag = a[i, i];
                if (Math.Abs(diag) < 1e-18)
                    return (0, 0, 0);

                // Normalize
                for (int c = i; c < 3; c++) a[i, c] /= diag;
                b[i] /= diag;

                // Eliminate
                for (int r = 0; r < 3; r++) {
                    if (r == i) continue;
                    double f = a[r, i];
                    if (Math.Abs(f) < 1e-18) continue;
                    for (int c = i; c < 3; c++) a[r, c] -= f * a[i, c];
                    b[r] -= f * b[i];
                }
            }

            // Now b holds solution for [c,b,a]
            return (b[0], b[1], b[2]);
        }
    }
}
