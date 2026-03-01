using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface ISecondaryCameraCaptureService {
        Task<SecondaryFrame> CaptureAsync(SecondaryCaptureRequest request, CancellationToken ct);
    }

    public sealed record SecondaryCaptureRequest(
        double ExposureSeconds,
        int BinX,
        int BinY,
        int? Gain
    );

    /// <summary>
    /// Frame in a solver/metric-friendly format (no encoding).
    /// Pixels are row-major, length = Width * Height.
    /// </summary>
    public sealed record SecondaryFrame(
        int Width,
        int Height,
        int BitDepth,
        int[] Pixels,
        DateTime TimestampUtc
    );
}
