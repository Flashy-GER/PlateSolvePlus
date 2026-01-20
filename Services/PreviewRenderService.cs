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
            int maxInt = GetTheoreticalMax(frame.BitDepth);

            // If Bayer: Debayer to RGB and render color preview.
            // Else: keep grayscale (or you can also render grayscale as RGB).
            if (frame.IsBayered && frame.BayerPattern == BayerPattern.RGGB) {

                // Debayer to float RGB (raw ADU domain)
                var r = new float[w * h];
                var g = new float[w * h];
                var b = new float[w * h];

                DebayerRGGBToRgbFloat(frame.Pixels, w, h, r, g, b);

                // Build a luma buffer to compute ONE stretch window (prevents color shifts)
                var luma = new float[w * h];
                for (int i = 0; i < luma.Length; i++) {
                    // perceptual-ish weights; for astro preview this is totally fine
                    luma[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                }

                // Stretch window from luma
                GetPercentileWindow(luma, options.AutoStretch ? options.StretchLowPercentile : 0.0,
                                          options.AutoStretch ? options.StretchHighPercentile : 1.0, 
                                          maxInt,
                                          out float min, out float max);

                // Map each channel with the same min/max to 0..1
                ApplyWindowInPlace(r, min, max);
                ApplyWindowInPlace(g, min, max);
                ApplyWindowInPlace(b, min, max);

                // Gamma on each channel
                if (Math.Abs(options.Gamma - 1.0) > 1e-6) {
                    ApplyGammaInPlace(r, options.Gamma);
                    ApplyGammaInPlace(g, options.Gamma);
                    ApplyGammaInPlace(b, options.Gamma);
                }

                return ToBitmapSourceBgra32Rgb(r, g, b, w, h);
            }

            // ===== Non-bayer fallback: keep your existing grayscale pipeline =====
            int idx = 0;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int v = frame.Pixels[y, x];
                    if (v < 0) v = 0;
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

        // =========================
        // Bayer (RGGB) -> Luma (Green reconstruction)
        // =========================
        private static void DebayerRGGBToLumaFloat(int[,] src, int w, int h, float[] dst) {
            int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

            int P(int x, int y) {
                x = Clamp(x, 0, w - 1);
                y = Clamp(y, 0, h - 1);
                int v = src[y, x];
                return v < 0 ? 0 : v;
            }

            int idx = 0;
            for (int y = 0; y < h; y++) {
                bool yOdd = (y & 1) == 1;

                for (int x = 0; x < w; x++) {
                    bool xOdd = (x & 1) == 1;

                    // RGGB:
                    // y even: R G R G
                    // y odd : G B G B

                    float g;

                    if (!yOdd && !xOdd) {
                        // R -> interpolate green (edge-aware)
                        int gh = (P(x - 1, y) + P(x + 1, y)) >> 1;
                        int gv = (P(x, y - 1) + P(x, y + 1)) >> 1;
                        int dh = Math.Abs(P(x - 1, y) - P(x + 1, y));
                        int dv = Math.Abs(P(x, y - 1) - P(x, y + 1));
                        g = (dh < dv) ? gh : gv;
                    } else if (yOdd && xOdd) {
                        // B -> interpolate green (edge-aware)
                        int gh = (P(x - 1, y) + P(x + 1, y)) >> 1;
                        int gv = (P(x, y - 1) + P(x, y + 1)) >> 1;
                        int dh = Math.Abs(P(x - 1, y) - P(x + 1, y));
                        int dv = Math.Abs(P(x, y - 1) - P(x, y + 1));
                        g = (dh < dv) ? gh : gv;
                    } else {
                        // G pixels
                        g = P(x, y);
                    }

                    dst[idx++] = g;
                }
            }
        }

        // =========================
        // Bayer (RGGB) -> RGB floats
        // =========================
        private static void DebayerRGGBToRgbFloat(int[,] src, int w, int h, float[] r, float[] g, float[] b) {
            int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

            int P(int x, int y) {
                x = Clamp(x, 0, w - 1);
                y = Clamp(y, 0, h - 1);
                int v = src[y, x];
                return v < 0 ? 0 : v;
            }

            int idx = 0;
            for (int y = 0; y < h; y++) {
                bool yOdd = (y & 1) == 1;

                for (int x = 0; x < w; x++) {
                    bool xOdd = (x & 1) == 1;

                    // RGGB:
                    // y even: R G R G
                    // y odd : G B G B

                    float rr, gg, bb;

                    if (!yOdd && !xOdd) {
                        // R pixel
                        rr = P(x, y);
                        gg = 0.25f * (P(x - 1, y) + P(x + 1, y) + P(x, y - 1) + P(x, y + 1));
                        bb = 0.25f * (P(x - 1, y - 1) + P(x + 1, y - 1) + P(x - 1, y + 1) + P(x + 1, y + 1));
                    } else if (!yOdd && xOdd) {
                        // G pixel on R row
                        gg = P(x, y);
                        rr = 0.5f * (P(x - 1, y) + P(x + 1, y));
                        bb = 0.5f * (P(x, y - 1) + P(x, y + 1));
                    } else if (yOdd && !xOdd) {
                        // G pixel on B row
                        gg = P(x, y);
                        rr = 0.5f * (P(x, y - 1) + P(x, y + 1));
                        bb = 0.5f * (P(x - 1, y) + P(x + 1, y));
                    } else {
                        // B pixel
                        bb = P(x, y);
                        gg = 0.25f * (P(x - 1, y) + P(x + 1, y) + P(x, y - 1) + P(x, y + 1));
                        rr = 0.25f * (P(x - 1, y - 1) + P(x + 1, y - 1) + P(x - 1, y + 1) + P(x + 1, y + 1));
                    }

                    r[idx] = rr;
                    g[idx] = gg;
                    b[idx] = bb;
                    idx++;
                }
            }
        }

        private static void GetPercentileWindow(float[] luma, double lowP, double highP, int maxInt, out float min, out float max) {
            if (highP <= lowP) { lowP = 0.01; highP = 0.995; }

            if (lowP <= 0.0 && highP >= 1.0) {
                // "Normalize by max" window
                min = 0f;
                max = maxInt > 0 ? maxInt : 1f;
                return;
            }

            lowP = Clamp01(lowP);
            highP = Clamp01(highP);

            var sorted = (float[])luma.Clone();
            Array.Sort(sorted);

            int lo = (int)Math.Round((sorted.Length - 1) * lowP);
            int hi = (int)Math.Round((sorted.Length - 1) * highP);
            lo = Math.Clamp(lo, 0, sorted.Length - 1);
            hi = Math.Clamp(hi, 0, sorted.Length - 1);

            min = sorted[lo];
            max = sorted[hi];
            if (max <= min) max = min + 1f;
        }

        private static void ApplyWindowInPlace(float[] channel, float min, float max) {
            float inv = 1f / (max - min);
            for (int i = 0; i < channel.Length; i++) {
                channel[i] = Clamp01f((channel[i] - min) * inv);
            }
        }

        private static BitmapSource ToBitmapSourceBgra32Rgb(float[] r01, float[] g01, float[] b01, int w, int h) {
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];

            int p = 0;
            for (int i = 0; i < r01.Length; i++) {
                byte rr = ToByte(r01[i]);
                byte gg = ToByte(g01[i]);
                byte bb = ToByte(b01[i]);

                pixels[p + 0] = bb;  // B
                pixels[p + 1] = gg;  // G
                pixels[p + 2] = rr;  // R
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
