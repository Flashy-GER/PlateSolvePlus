using System;

namespace NINA.Plugins.PlateSolvePlus.Utils {
    internal static class ImageConvert {
        public static ushort[] ConvertToUShortRowMajor(int[,] pixels, int width, int height) {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            var arr = new ushort[width * height];
            int idx = 0;

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int v = pixels[y, x];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    arr[idx++] = (ushort)v;
                }
            }

            return arr;
        }
    }
}
