using System;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    public sealed class SecondaryCameraFrame {
        public SecondaryCameraFrame(int[,] pixels, int width, int height, int bitDepth, DateTime utcTimestamp) {
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
            Width = width;
            Height = height;
            BitDepth = bitDepth;
            UtcTimestamp = utcTimestamp;
        }

        /// <summary>
        /// Pixel matrix [y, x] (Row-major). Values are raw ADU.
        /// </summary>
        public int[,] Pixels { get; }

        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// Typical: 8/12/14/16.
        /// </summary>
        public int BitDepth { get; }

        public DateTime UtcTimestamp { get; }
    }
}
