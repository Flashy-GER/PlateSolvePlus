using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    /// <summary>
    /// ASCOM Camera via COM ProgID (e.g. "ASCOM.ZWO.Camera" etc.)
    /// Uses dynamic dispatch to avoid hard compile-time dependency on ASCOM assemblies.
    /// </summary>
    public sealed class AscomComSecondaryCamera : ISecondaryCamera {
        private readonly string progId;
        private dynamic cam; // COM object

        public AscomComSecondaryCamera(string progId) {
            if (string.IsNullOrWhiteSpace(progId))
                throw new ArgumentException("ProgID must not be empty.", nameof(progId));

            this.progId = progId;
        }

        public bool IsConnected {
            get {
                try { return cam != null && cam.Connected == true; } catch { return false; }
            }
        }

        public Task ConnectAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            if (cam != null)
                return Task.CompletedTask;

            var t = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (t == null)
                throw new InvalidOperationException($"ASCOM ProgID not found: '{progId}'. Is the driver installed?");

            cam = Activator.CreateInstance(t);
            cam.Connected = true;

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            if (cam == null)
                return Task.CompletedTask;

            try {
                try { cam.Connected = false; } catch { /* ignore */ }
            } finally {
                try {
                    if (cam != null && Marshal.IsComObject(cam))
                        Marshal.FinalReleaseComObject(cam);
                } catch { /* ignore */ }
                cam = null;
            }

            return Task.CompletedTask;
        }

        public async Task<SecondaryCameraFrame> CaptureAsync(
            double exposureSeconds,
            int binX,
            int binY,
            int? gain,
            CancellationToken ct) {
            if (cam == null || !IsConnected)
                throw new InvalidOperationException("Secondary camera is not connected.");

            if (exposureSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(exposureSeconds), "Exposure must be > 0.");

            if (binX < 1) binX = 1;
            if (binY < 1) binY = 1;

            ct.ThrowIfCancellationRequested();

            // Best-effort set binning (not all drivers support it)
            TrySet(() => cam.BinX = binX);
            TrySet(() => cam.BinY = binY);

            // Best-effort set gain (many ASCOM drivers: Gain property exists, but not all)
            if (gain.HasValue)
                TrySet(() => cam.Gain = gain.Value);

            // Start exposure: StartExposure(Duration, Light)
            // Some drivers require Light=true for normal frames.
            cam.StartExposure(exposureSeconds, true);

            // Wait for ImageReady (polling)
            var start = DateTime.UtcNow;
            while (true) {
                ct.ThrowIfCancellationRequested();

                bool ready = false;
                try { ready = cam.ImageReady == true; } catch {
                    // If ImageReady not supported, fallback to a short delay then try read image
                    ready = false;
                }

                if (ready)
                    break;

                // Simple timeout protection (in addition to ct)
                if ((DateTime.UtcNow - start).TotalSeconds > Math.Max(10, exposureSeconds + 30))
                    throw new TimeoutException("Exposure timed out waiting for ImageReady.");

                await Task.Delay(200, ct);
            }

            // Read image data
            object imageArrayObj = null;

            // ASCOM camera exposes ImageArray (2D) or ImageArrayVariant
            try { imageArrayObj = cam.ImageArray; } catch {
                try { imageArrayObj = cam.ImageArrayVariant; } catch { /* ignore */ }
            }

            if (imageArrayObj == null)
                throw new InvalidOperationException("Could not read image array from ASCOM camera.");

            // Convert to int[,] (y,x)
            var pixels = ConvertToInt2D(imageArrayObj, out var width, out var height);

            int bitDepth = 16;
            try { bitDepth = (int)cam.CameraXSize > 0 ? 16 : 16; } catch { /* ignore */ }
            TryGet(() => bitDepth = (int)cam.MaxADU > 0 ? 16 : 16); // best-effort (MaxADU exists on some)

            return new SecondaryCameraFrame(pixels, width, height, bitDepth, DateTime.UtcNow);
        }

        public void Dispose() {
            try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* ignore */ }
        }

        private static void TrySet(Action action) {
            try { action(); } catch { /* ignore */ }
        }

        private static void TryGet(Action action) {
            try { action(); } catch { /* ignore */ }
        }

        private static int[,] ConvertToInt2D(object imageArrayObj, out int width, out int height) {
            // Most ASCOM drivers return a 2D array: int[,] or short[,]
            if (imageArrayObj is int[,] i2) {
                height = i2.GetLength(0);
                width = i2.GetLength(1);
                return i2;
            }

            if (imageArrayObj is short[,] s2) {
                height = s2.GetLength(0);
                width = s2.GetLength(1);
                var r = new int[height, width];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        r[y, x] = s2[y, x];
                return r;
            }

            if (imageArrayObj is object[,] o2) {
                height = o2.GetLength(0);
                width = o2.GetLength(1);
                var r = new int[height, width];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        r[y, x] = Convert.ToInt32(o2[y, x]);
                return r;
            }

            throw new NotSupportedException($"Unsupported ImageArray type: {imageArrayObj.GetType().FullName}");
        }
    }
}
