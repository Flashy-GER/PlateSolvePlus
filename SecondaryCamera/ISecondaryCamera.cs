using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    public interface ISecondaryCamera : IDisposable {
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken ct);
        Task DisconnectAsync(CancellationToken ct);

        Task<SecondaryCameraFrame> CaptureAsync(
            double exposureSeconds,
            int binX,
            int binY,
            int? gain,
            CancellationToken ct);
    }
}
