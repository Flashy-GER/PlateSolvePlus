using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugins.PlateSolvePlus.PlateSolving;
using NINA.Plugins.PlateSolvePlus.Utils;
using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Profile.Interfaces;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal sealed class PlateSolveRunResult {
        public CapturedFrame Frame { get; }
        public double SearchRadiusDeg { get; }
        public int Downsample { get; }
        public int TimeoutSec { get; }
        public double FocalLengthMm { get; }
        public double PixelSizeUm { get; }

        public object RawResult { get; }
        public string DebugDump { get; }

        public bool HasCoordinates => RaDeg.HasValue && DecDeg.HasValue;
        public double? RaDeg { get; }
        public double? DecDeg { get; }

        public PlateSolveRunResult(
            CapturedFrame frame,
            double searchRadiusDeg,
            int downsample,
            int timeoutSec,
            double focalLengthMm,
            double pixelSizeUm,
            object rawResult,
            string debugDump,
            double? raDeg,
            double? decDeg) {

            Frame = frame;
            SearchRadiusDeg = searchRadiusDeg;
            Downsample = downsample;
            TimeoutSec = timeoutSec;
            FocalLengthMm = focalLengthMm;
            PixelSizeUm = pixelSizeUm;

            RawResult = rawResult;
            DebugDump = debugDump;

            RaDeg = raDeg;
            DecDeg = decDeg;
        }
    }

    internal interface IPlateSolveService {
        Task<PlateSolveRunResult> SolveAsync(
            CapturedFrame frame,
            IPlateSolveSettings plateSolveSettings,
            double focalLengthMm,
            double pixelSizeUm,
            CancellationToken ct);
    }

    internal sealed class PlateSolveService : IPlateSolveService {
        private readonly IImageDataFactory imageDataFactory;
        private readonly IPlateSolverFactory plateSolverFactory;

        public PlateSolveService(IImageDataFactory imageDataFactory, IPlateSolverFactory plateSolverFactory) {
            this.imageDataFactory = imageDataFactory ?? throw new ArgumentNullException(nameof(imageDataFactory));
            this.plateSolverFactory = plateSolverFactory ?? throw new ArgumentNullException(nameof(plateSolverFactory));
        }

        public async Task<PlateSolveRunResult> SolveAsync(
            CapturedFrame frame,
            IPlateSolveSettings plateSolveSettings,
            double focalLengthMm,
            double pixelSizeUm,
            CancellationToken ct) {

            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (plateSolveSettings == null) throw new ArgumentNullException(nameof(plateSolveSettings));

            // Read settings (robust against property name variants)
            double searchRadiusDeg = ReflectionRead.ReadDouble(plateSolveSettings, "SearchRadius", "SearchRadiusDeg", "Radius", "RadiusDeg") ?? 5.0;
            int downsample = ReflectionRead.ReadInt(plateSolveSettings, "Downsample", "DownSample", "DownSampling", "DownSamplingFactor") ?? 2;
            int timeoutSec = ReflectionRead.ReadInt(plateSolveSettings, "Timeout", "TimeoutSeconds", "SolveTimeout", "SolveTimeoutSeconds") ?? 60;

            // Build IImageData
            ushort[] packed = ImageConvert.ConvertToUShortRowMajor(frame.Pixels, frame.Width, frame.Height);
            var imageData = imageDataFactory.CreateBaseImageData(
                input: packed,
                width: frame.Width,
                height: frame.Height,
                bitDepth: frame.BitDepth,
                isBayered: false,
                metaData: new ImageMetaData());

            // Build solve parameter
            var parameter = NinaPlateSolveParameterFactory.Create(
                searchRadiusDeg: searchRadiusDeg,
                downsample: downsample,
                timeoutSec: timeoutSec,
                focalLengthMm: focalLengthMm,
                pixelSizeUm: pixelSizeUm
            );

            var solver = plateSolverFactory.GetPlateSolver(plateSolveSettings);

            var raw = await solver.SolveAsync(imageData, parameter, null, ct).ConfigureAwait(false);

            var dump = ReflectionRead.DumpObject(raw);

            // Extract RA/Dec if possible
            double? raDeg = null;
            double? decDeg = null;

            if (TryExtractRaDecDeg(raw, out var ra, out var dec)) {
                raDeg = ra;
                decDeg = dec;
            }

            return new PlateSolveRunResult(
                frame,
                searchRadiusDeg,
                downsample,
                timeoutSec,
                focalLengthMm,
                pixelSizeUm,
                raw,
                dump,
                raDeg,
                decDeg);
        }

        private static bool TryExtractRaDecDeg(object result, out double raDeg, out double decDeg) {
            raDeg = 0;
            decDeg = 0;
            if (result == null) return false;

            if (ReflectionRead.TryReadNumber(result, new[] { "RightAscension", "RA" }, out var raVal) &&
                ReflectionRead.TryReadNumber(result, new[] { "Declination", "Dec" }, out var decVal)) {

                raDeg = AstroFormat.GuessRaToDegrees(raVal);
                decDeg = decVal;
                return true;
            }

            var coordsProp = result.GetType().GetProperty("Coordinates", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var coords = coordsProp?.GetValue(result);

            if (coords != null) {
                if (ReflectionRead.TryReadNumber(coords, new[] { "RightAscension", "RA" }, out raVal) &&
                    ReflectionRead.TryReadNumber(coords, new[] { "Declination", "Dec" }, out decVal)) {

                    raDeg = AstroFormat.GuessRaToDegrees(raVal);
                    decDeg = decVal;
                    return true;
                }
            }

            return false;
        }
    }
}
