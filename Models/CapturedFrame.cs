using System;

namespace NINA.Plugins.PlateSolvePlus.Models {

    /// <summary>
    /// Minimal DTO to decouple Dockables from concrete capture result types.
    /// </summary>
    internal sealed class CapturedFrame {
        public int Width { get; }
        public int Height { get; }
        public int BitDepth { get; }
        public int[,] Pixels { get; }

        public CapturedFrame(int width, int height, int bitDepth, int[,] pixels) {
            Width = width;
            Height = height;
            BitDepth = bitDepth;
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        }
    }
}
