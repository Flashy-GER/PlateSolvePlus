using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.Services {

    [Export(typeof(IPlateSolvePlusContext))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class PlateSolvePlusContext : IPlateSolvePlusContext, IDisposable {

        private readonly object gate = new object();

        private readonly IAscomDeviceDiscoveryService ascomDiscovery;

        private ISecondaryCameraService? activeSecondaryCameraService;
        private string? activeSecondaryCameraProgId;

        private GuiderSolveSnapshot? lastGuiderSolve;

        public IAscomDeviceDiscoveryService AscomDiscovery => ascomDiscovery;

        public string? CurrentSecondaryCameraProgId { get; set; }

        public GuiderSolveSnapshot? LastGuiderSolve {
            get {
                lock (gate) return lastGuiderSolve;
            }
        }

        public event EventHandler? LastGuiderSolveUpdated;

        public PlateSolvePlusContext() {
            ascomDiscovery = new AscomDeviceDiscoveryService();
        }

        public void SetActiveSecondaryCameraProgId(string progId) {
            if (string.IsNullOrWhiteSpace(progId)) throw new ArgumentNullException(nameof(progId));

            ISecondaryCameraService? oldServiceToDispose = null;

            lock (gate) {
                if (string.Equals(activeSecondaryCameraProgId, progId, StringComparison.OrdinalIgnoreCase)) {
                    CurrentSecondaryCameraProgId = progId;
                    return;
                }

                oldServiceToDispose = activeSecondaryCameraService;

                activeSecondaryCameraProgId = progId;
                CurrentSecondaryCameraProgId = progId;
                activeSecondaryCameraService = new SecondaryCameraService(progId);
            }

            DisposeSecondaryCameraServiceSafely(oldServiceToDispose);
        }

        public ISecondaryCameraService GetActiveSecondaryCameraService() {
            lock (gate) {
                if (activeSecondaryCameraService == null) {
                    throw new InvalidOperationException(
                        "Active secondary camera service not initialized. Call SetActiveSecondaryCameraProgId() first.");
                }
                return activeSecondaryCameraService;
            }
        }

        public void SetLastGuiderSolve(GuiderSolveSnapshot snapshot) {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            EventHandler? handler;
            lock (gate) {
                lastGuiderSolve = snapshot;
                handler = LastGuiderSolveUpdated;
            }

            try { handler?.Invoke(this, EventArgs.Empty); } catch { }
        }

        public void Dispose() {
            ISecondaryCameraService? svc;
            lock (gate) {
                svc = activeSecondaryCameraService;
                activeSecondaryCameraService = null;
                activeSecondaryCameraProgId = null;
                CurrentSecondaryCameraProgId = null;
                lastGuiderSolve = null;
            }

            DisposeSecondaryCameraServiceSafely(svc);
        }

        private static void DisposeSecondaryCameraServiceSafely(ISecondaryCameraService? svc) {
            if (svc == null) return;

            try {
                if (svc.IsConnected) {
                    try {
                        svc.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    } catch { }
                }
            } catch { }

            try { svc.Dispose(); } catch { }
        }
    }
}
