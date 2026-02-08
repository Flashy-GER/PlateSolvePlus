using NINA.Plugins.PlateSolvePlus.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    /// <summary>
    /// Adapter: nutzt deinen vorhandenen SecondaryCameraService (service/SecondaryCameraService.cs)
    /// und mappt CapturedFrame -> SecondaryFrame.
    ///
    /// Erwartet CapturedFrame: Width, Height, BitDepth, Pixels (int[] oder int[,])
    /// </summary>
    public sealed class SecondaryCameraCaptureAdapter : ISecondaryCameraCaptureService {
        private readonly ISecondaryCameraService _secondaryCameraService;

        public SecondaryCameraCaptureAdapter(ISecondaryCameraService secondaryCameraService) {
            _secondaryCameraService = secondaryCameraService ?? throw new ArgumentNullException(nameof(secondaryCameraService));
        }

        public async Task<SecondaryFrame> CaptureAsync(SecondaryCaptureRequest request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            var captured = await _secondaryCameraService.CaptureAsync(
                request.ExposureSeconds,
                request.BinX,
                request.BinY,
                request.Gain,
                ct
            ).ConfigureAwait(false);

            int width = captured.Width;
            int height = captured.Height;
            int bitDepth = captured.BitDepth;
            int[] pixels = Flatten(captured.Pixels);

            if (pixels.Length != width * height)
                throw new InvalidOperationException($"CapturedFrame pixel length mismatch: {pixels.Length} != {width}*{height}");

            return new SecondaryFrame(width, height, bitDepth, pixels, DateTime.UtcNow);
        }

        private static int[] Flatten(int[,] grid) {
            int h = grid.GetLength(0);
            int w = grid.GetLength(1);
            var arr = new int[w * h];
            int k = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    arr[k++] = grid[y, x];
            return arr;
        }
    }
}
