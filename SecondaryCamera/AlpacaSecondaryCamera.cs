using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    /// <summary>
    /// Alpaca implementation of ISecondaryCamera:
    /// - Connect/Disconnect
    /// - CaptureAsync(exposureSeconds, binX, binY, gain)
    /// </summary>
    public sealed class AlpacaSecondaryCamera : ISecondaryCamera {
        private readonly AlpacaCameraClient _client;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// Controls how long we wait after exposure start until image is ready.
        /// Default: max(30s, exposure*6).
        /// </summary>
        public Func<double, TimeSpan> MaxWaitPolicy { get; set; }
            = exp => TimeSpan.FromSeconds(Math.Max(30, exp * 6));

        /// <summary>
        /// Fallback bit depth if MaxADU is not supported.
        /// </summary>
        public int DefaultBitDepth { get; set; } = 16;

        public AlpacaSecondaryCamera(string host, int port, int deviceNumber, uint clientId = 1, TimeSpan? timeout = null, bool https = false) {
            _client = new AlpacaCameraClient(host, port, deviceNumber, clientId, timeout, https);
        }

        public void Dispose() => _client.Dispose();

        public async Task ConnectAsync(CancellationToken ct) {
            await _client.SetConnectedAsync(true, ct).ConfigureAwait(false);
            IsConnected = await _client.GetConnectedAsync(ct).ConfigureAwait(false);
        }

        public async Task DisconnectAsync(CancellationToken ct) {
            try {
                await _client.SetConnectedAsync(false, ct).ConfigureAwait(false);
            } finally {
                IsConnected = false;
            }
        }

        public async Task<double?> GetPixelSizeUmAsync(CancellationToken ct) {
            double? x = null;
            double? y = null;

            try { x = await _client.GetPixelSizeXAsync(ct).ConfigureAwait(false); } catch { }
            try { y = await _client.GetPixelSizeYAsync(ct).ConfigureAwait(false); } catch { }

            if (TryGetValidPixelSize(x, out var xValue) && TryGetValidPixelSize(y, out var yValue)) return (xValue + yValue) / 2.0;
            if (TryGetValidPixelSize(x, out xValue)) return xValue;
            if (TryGetValidPixelSize(y, out yValue)) return yValue;

            return null;
        }

        public async Task<SecondaryCameraFrame> CaptureAsync(
            double exposureSeconds,
            int binX,
            int binY,
            int? gain,
            CancellationToken ct) {
            if (exposureSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(exposureSeconds));
            if (binX <= 0) throw new ArgumentOutOfRangeException(nameof(binX));
            if (binY <= 0) throw new ArgumentOutOfRangeException(nameof(binY));

            // Set binning
            await _client.SetBinXAsync(binX, ct).ConfigureAwait(false);
            await _client.SetBinYAsync(binY, ct).ConfigureAwait(false);

            // IMPORTANT: Ensure the requested subframe/ROI is valid for the current binning.
            // Many Alpaca/ASCOM drivers persist StartX/StartY/NumX/NumY across sessions and/or require
            // NumX/NumY to be aligned to BinX/BinY. If ROI is out of range, StartExposure fails with
            // "subframe is outside main frame" (Alpaca error 1025).
            await EnsureFullFrameAsync(binX, binY, ct).ConfigureAwait(false);

            // Set gain if supported (ignore if not implemented)
            if (gain.HasValue) {
                try { await _client.SetGainAsync(gain.Value, ct).ConfigureAwait(false); } catch { /* optional */ }
            }

            // Try get bit depth (optional)
            int bitDepth = DefaultBitDepth;
            try {
                int maxAdu = await _client.GetMaxAduAsync(ct).ConfigureAwait(false);
                bitDepth = EstimateBitDepthFromMaxAdu(maxAdu, DefaultBitDepth);
            } catch {
                // ignore -> keep DefaultBitDepth
            }

            // Start exposure (Light=true for platesolving)
            await _client.StartExposureAsync(exposureSeconds, light: true, ct).ConfigureAwait(false);

            // Wait for imageready
            var deadline = DateTime.UtcNow + MaxWaitPolicy(exposureSeconds);

            while (true) {
                ct.ThrowIfCancellationRequested();

                bool ready;
                try {
                    ready = await _client.GetImageReadyAsync(ct).ConfigureAwait(false);
                } catch {
                    // Some cameras get flaky during readout; treat as not ready
                    ready = false;
                }

                if (ready) break;

                if (DateTime.UtcNow > deadline) {
                    // best effort stop/abort
                    try { await _client.StopExposureAsync(CancellationToken.None).ConfigureAwait(false); } catch { try { await _client.AbortExposureAsync(CancellationToken.None).ConfigureAwait(false); } catch { } }

                    throw new TimeoutException($"Alpaca image not ready after {MaxWaitPolicy(exposureSeconds)}.");
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
            }

            // Fetch image pixels
            var pixels = await _client.GetImageArray2DAsync(ct).ConfigureAwait(false);
            int height = pixels.GetLength(0);
            int width = pixels.GetLength(1);

            if (height == 0 || width == 0)
                throw new InvalidOperationException("Alpaca imagearray returned empty image.");

            return new SecondaryCameraFrame(
                pixels: pixels,
                width: width,
                height: height,
                bitDepth: bitDepth,
                utcTimestamp: DateTime.UtcNow);
        }

        private async Task EnsureFullFrameAsync(int binX, int binY, CancellationToken ct) {
            // Read sensor size in unbinned pixels
            int xSize = await _client.GetCameraXSizeAsync(ct).ConfigureAwait(false);
            int ySize = await _client.GetCameraYSizeAsync(ct).ConfigureAwait(false);

            if (xSize <= 0 || ySize <= 0)
                throw new InvalidOperationException($"Invalid Alpaca camera size: {xSize}x{ySize}.");

            // Align NumX/NumY to binning (largest multiple <= size)
            int numX = (xSize / binX) * binX;
            int numY = (ySize / binY) * binY;

            // Defensive: if bin is larger than sensor (shouldn't happen), fall back to full size
            if (numX <= 0) numX = xSize;
            if (numY <= 0) numY = ySize;

            // Reset ROI to full frame. Order matters for some drivers; Start* first is generally safe.
            await _client.SetStartXAsync(0, ct).ConfigureAwait(false);
            await _client.SetStartYAsync(0, ct).ConfigureAwait(false);
            await _client.SetNumXAsync(numX, ct).ConfigureAwait(false);
            await _client.SetNumYAsync(numY, ct).ConfigureAwait(false);
        }

        private static int EstimateBitDepthFromMaxAdu(int maxAdu, int fallback) {
            if (maxAdu <= 0) return fallback;

            // bitDepth ≈ ceil(log2(maxAdu + 1))
            // Clamp to sane range.
            double bits = Math.Ceiling(Math.Log(maxAdu + 1.0, 2.0));
            int bd = (int)bits;

            if (bd < 8) bd = 8;
            if (bd > 32) bd = 32;

            return bd;
        }

        private static bool IsValidPixelSize(double? value) =>
            value.HasValue &&
            !double.IsNaN(value.Value) &&
            !double.IsInfinity(value.Value) &&
            value.Value >= 0.1 &&
            value.Value <= 100.0;

        private static bool TryGetValidPixelSize(double? value, out double pixelSizeUm) {
            pixelSizeUm = value.GetValueOrDefault();
            return IsValidPixelSize(value);
        }
    }
}
