using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using System;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal interface IServiceFactory : IDisposable {
        ITelescopeReferenceService GetTelescopeReferenceService();
        ISecondaryCameraService GetSecondaryCameraService(string progId);
        IOffsetService GetOffsetService();
        IPlateSolveService GetPlateSolveService(IImageDataFactory imageDataFactory, IPlateSolverFactory plateSolverFactory);
        IAscomDeviceDiscoveryService GetAscomDiscoveryService();
    }

    internal sealed class ServiceFactory : IServiceFactory {
        private ITelescopeReferenceService? telescopeReferenceService;

        private ISecondaryCameraService? secondaryCameraService;
        private string? secondaryProgId;

        private IOffsetService? offsetService;

        private IPlateSolveService? plateSolveService;
        private IImageDataFactory? cachedImageDataFactory;
        private IPlateSolverFactory? cachedPlateSolverFactory;

        private IAscomDeviceDiscoveryService? ascomDiscoveryService;

        public ITelescopeReferenceService GetTelescopeReferenceService() {
            telescopeReferenceService ??= new TelescopeReferenceService();
            return telescopeReferenceService;
        }

        public ISecondaryCameraService GetSecondaryCameraService(string progId) {
            if (string.IsNullOrWhiteSpace(progId)) throw new ArgumentNullException(nameof(progId));

            // If progId changes, rebuild camera service (COM driver binding depends on it)
            if (secondaryCameraService == null || !string.Equals(secondaryProgId, progId, StringComparison.OrdinalIgnoreCase)) {
                try { secondaryCameraService?.Dispose(); } catch { }
                secondaryCameraService = new SecondaryCameraService(progId);
                secondaryProgId = progId;
            }

            return secondaryCameraService;
        }

        public IOffsetService GetOffsetService() {
            offsetService ??= new OffsetService();
            return offsetService;
        }

        public IPlateSolveService GetPlateSolveService(IImageDataFactory imageDataFactory, IPlateSolverFactory plateSolverFactory) {
            if (imageDataFactory == null) throw new ArgumentNullException(nameof(imageDataFactory));
            if (plateSolverFactory == null) throw new ArgumentNullException(nameof(plateSolverFactory));

            if (plateSolveService == null ||
                !ReferenceEquals(cachedImageDataFactory, imageDataFactory) ||
                !ReferenceEquals(cachedPlateSolverFactory, plateSolverFactory)) {

                plateSolveService = new PlateSolveService(imageDataFactory, plateSolverFactory);
                cachedImageDataFactory = imageDataFactory;
                cachedPlateSolverFactory = plateSolverFactory;
            }

            return plateSolveService;
        }

        public IAscomDeviceDiscoveryService GetAscomDiscoveryService() {
            ascomDiscoveryService ??= new AscomDeviceDiscoveryService();
            return ascomDiscoveryService;
        }

        public void Dispose() {
            try { telescopeReferenceService?.Dispose(); } catch { }
            try { secondaryCameraService?.Dispose(); } catch { }

            telescopeReferenceService = null;
            secondaryCameraService = null;
            offsetService = null;

            plateSolveService = null;
            cachedImageDataFactory = null;
            cachedPlateSolverFactory = null;

            ascomDiscoveryService = null;
            secondaryProgId = null;
        }
    }
}
