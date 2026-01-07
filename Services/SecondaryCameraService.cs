using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Plugins.PlateSolvePlus.SecondaryCamera;
using NINA.Plugins.PlateSolvePlus.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal interface ISecondaryCameraService : IDisposable {
        string ProgId { get; }
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken ct);
        Task DisconnectAsync(CancellationToken ct);
        Task<CapturedFrame> CaptureAsync(
            double exposureSeconds,
            int binX,
            int binY,
            int? gain,
            CancellationToken ct);

        Task<bool> OpenSetupDialogAsync();
    }

    internal sealed class SecondaryCameraService : ISecondaryCameraService {
        private ISecondaryCamera? camera;

        public string ProgId { get; }
        public bool IsConnected => camera?.IsConnected ?? false;

        public SecondaryCameraService(string progId) {
            ProgId = progId ?? throw new ArgumentNullException(nameof(progId));
        }

        public async Task ConnectAsync(CancellationToken ct) {
            DisposeCamera();

            camera = new AscomComSecondaryCamera(ProgId);
            await camera.ConnectAsync(ct).ConfigureAwait(false);
        }

        public async Task DisconnectAsync(CancellationToken ct) {
            if (camera != null) {
                try {
                    await camera.DisconnectAsync(ct).ConfigureAwait(false);
                } finally {
                    DisposeCamera();
                }
            }
        }

        public async Task<CapturedFrame> CaptureAsync(
            double exposureSeconds,
            int binX,
            int binY,
            int? gain,
            CancellationToken ct) {

            if (camera == null || !camera.IsConnected) {
                throw new InvalidOperationException("Secondary camera is not connected.");
            }

            var frame = await camera.CaptureAsync(exposureSeconds, binX, binY, gain, ct).ConfigureAwait(false);

            return new CapturedFrame(frame.Width, frame.Height, frame.BitDepth, frame.Pixels);
        }

        public Task<bool> OpenSetupDialogAsync() {
            var progId = ProgId;
            if (string.IsNullOrWhiteSpace(progId)) {
                return Task.FromResult(false);
            }

            // SetupDialog is ASCOM driver UI (COM) → must be STA
            return StaTaskRunner.RunAsync(() => {
                dynamic? comCam = null;
                try {
                    var t = Type.GetTypeFromProgID(progId, throwOnError: false);
                    if (t == null) return false;

                    comCam = Activator.CreateInstance(t);
                    comCam.SetupDialog();
                    return true;
                } catch {
                    return false;
                } finally {
                    ComHelpers.FinalRelease(comCam);
                }
            });
        }

        public void Dispose() => DisposeCamera();

        private void DisposeCamera() {
            try { camera?.Dispose(); } catch { }
            camera = null;
        }
    }
}
