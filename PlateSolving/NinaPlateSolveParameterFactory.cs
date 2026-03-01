using System;

namespace NINA.Plugins.PlateSolvePlus.PlateSolving {
    public static class NinaPlateSolveParameterFactory {
        public static NINA.PlateSolving.PlateSolveParameter Create(
            double? searchRadiusDeg,
            int? downsample,
            int? timeoutSec,
            double? focalLengthMm,
            double? pixelSizeUm,
            bool? sync = null) {
            var p = new NINA.PlateSolving.PlateSolveParameter();

            // Best-effort: nur setzen, wenn Property existiert (Builds variieren)
            TrySet(p, "SearchRadius", searchRadiusDeg);
            TrySet(p, "SearchRadiusDeg", searchRadiusDeg);

            TrySet(p, "Downsample", downsample);
            TrySet(p, "DownSample", downsample);
            TrySet(p, "DownSampling", downsample);
            TrySet(p, "DownSamplingFactor", downsample);

            TrySet(p, "Timeout", timeoutSec);
            TrySet(p, "TimeoutSeconds", timeoutSec);
            TrySet(p, "SolveTimeout", timeoutSec);
            TrySet(p, "SolveTimeoutSeconds", timeoutSec);

            TrySet(p, "FocalLength", focalLengthMm);
            TrySet(p, "FocalLengthMm", focalLengthMm);

            TrySet(p, "PixelSize", pixelSizeUm);
            TrySet(p, "PixelSizeUm", pixelSizeUm);

            return p;
        }

        private static void TrySet(object obj, string prop, object? value) {
            if (value == null) return;

            var pi = obj.GetType().GetProperty(prop);
            if (pi == null || !pi.CanWrite) return;

            try {
                var targetType = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
                var converted = Convert.ChangeType(value, targetType);
                pi.SetValue(obj, converted);
            } catch {
                // ignore
            }
        }
    }
}
