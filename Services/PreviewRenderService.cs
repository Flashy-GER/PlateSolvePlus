using NINA.Plugins.PlateSolvePlus.Models;
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugins.PlateSolvePlus.Services {
        internal sealed class PreviewRenderService : IPreviewRenderService {
            public BitmapSource RenderPreview(CapturedFrame frame, PreviewRenderOptions options) {

            if (frame == null) throw new ArgumentNullException(nameof(frame));
            options ??= new PreviewRenderOptions();

            int w = frame.Width;
            int h = frame.Height;

            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException("Width/Height must be > 0");
            if (frame.Pixels.GetLength(0) != h || frame.Pixels.GetLength(1) != w)
                throw new ArgumentException("CapturedFrame.Pixels dimensions do not match Width/Height");

            // Convert int[,] to float[] grayscale (either 0..(2^bitDepth-1) or 0..some driver range)
            // Layout: one float per pixel
            var mono = new float[w * h];

            int idx = 0;
            int maxInt = GetTheoreticalMax(frame.BitDepth);

            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int v = frame.Pixels[y, x];
                    if (v < 0) v = 0;
                    // Don't hard clamp to theoretical max because some drivers misreport bit depth.
                    // We'll stretch anyway. But we can keep it within int range.
                    mono[idx++] = v;
                }
            }

            // Stretch to 0..1
            if (options.AutoStretch)
                ApplyPercentileStretchInPlace(mono, options.StretchLowPercentile, options.StretchHighPercentile);
            else
                NormalizeByMaxInPlace(mono, maxInt);

            // Gamma
            if (Math.Abs(options.Gamma - 1.0) > 1e-6)
                ApplyGammaInPlace(mono, options.Gamma);

            // Pack to BGRA32 (grayscale)
            return ToBitmapSourceBgra32Gray(mono, w, h);
        }

        private static int GetTheoreticalMax(int bitDepth) {
            if (bitDepth <= 0) return 65535;
            if (bitDepth >= 31) return int.MaxValue;
            return (1 << bitDepth) - 1;
        }

        // =========================
        // Stretch
        // =========================

        private static void ApplyPercentileStretchInPlace(float[] mono, double lowP, double highP) {
            lowP = Clamp01(lowP);
            highP = Clamp01(highP);
            if (highP <= lowP) { lowP = 0.01; highP = 0.995; }

            // Copy + sort for percentile window
            var sorted = (float[])mono.Clone();
            Array.Sort(sorted);

            int lo = (int)Math.Round((sorted.Length - 1) * lowP);
            int hi = (int)Math.Round((sorted.Length - 1) * highP);
            lo = Math.Clamp(lo, 0, sorted.Length - 1);
            hi = Math.Clamp(hi, 0, sorted.Length - 1);

            float min = sorted[lo];
            float max = sorted[hi];
            if (max <= min) max = min + 1f;

            float inv = 1f / (max - min);
            for (int i = 0; i < mono.Length; i++)
                mono[i] = Clamp01f((mono[i] - min) * inv);
        }

        private static void NormalizeByMaxInPlace(float[] mono, int max) {
            if (max <= 0) max = 1;
            float inv = 1f / max;
            for (int i = 0; i < mono.Length; i++)
                mono[i] = Clamp01f(mono[i] * inv);
        }

        private static void ApplyGammaInPlace(float[] mono01, double gamma) {
            float g = (float)gamma;
            for (int i = 0; i < mono01.Length; i++)
                mono01[i] = (float)Math.Pow(Clamp01f(mono01[i]), g);
        }

        // =========================
        // Pack to WPF BitmapSource
        // =========================

        private static BitmapSource ToBitmapSourceBgra32Gray(float[] mono01, int w, int h) {
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];

            int p = 0;
            for (int i = 0; i < mono01.Length; i++) {
                byte v = ToByte(mono01[i]);
                pixels[p + 0] = v;   // B
                pixels[p + 1] = v;   // G
                pixels[p + 2] = v;   // R
                pixels[p + 3] = 255; // A
                p += 4;
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private static byte ToByte(float v01) {
            v01 = Clamp01f(v01);
            int v = (int)Math.Round(v01 * 255f);
            return (byte)Math.Clamp(v, 0, 255);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static float Clamp01f(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
