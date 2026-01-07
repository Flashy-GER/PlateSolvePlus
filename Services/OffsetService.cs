using NINA.Plugins.PlateSolvePlus.PlateSolving;
using System;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal sealed class OffsetCalibrationResult {
        public double RotationAngleDeg { get; }
        public double DeltaRaArcsec { get; }
        public double DeltaDecArcsec { get; }
        public DateTime CalibrationUtc { get; }

        public OffsetCalibrationResult(double rotationAngleDeg, double deltaRaArcsec, double deltaDecArcsec, DateTime calibrationUtc) {
            RotationAngleDeg = rotationAngleDeg;
            DeltaRaArcsec = deltaRaArcsec;
            DeltaDecArcsec = deltaDecArcsec;
            CalibrationUtc = calibrationUtc;
        }
    }

    internal interface IOffsetService {
        OffsetCalibrationResult Calibrate(
            PlateSolvePlusSettings settings,
            double mainRaDeg,
            double mainDecDeg,
            double guideRaDeg,
            double guideDecDeg);

        (double raDeg, double decDeg) ApplyToGuiderSolve(PlateSolvePlusSettings settings, double guideRaDeg, double guideDecDeg);

        string GetQuaternionText(PlateSolvePlusSettings settings);
        string GetRotationAngleText(PlateSolvePlusSettings settings);
    }

    internal sealed class OffsetService : IOffsetService {

        public OffsetCalibrationResult Calibrate(
            PlateSolvePlusSettings settings,
            double mainRaDeg,
            double mainDecDeg,
            double guideRaDeg,
            double guideDecDeg) {

            if (settings == null) throw new ArgumentNullException(nameof(settings));

            // Rotation (STANDARD)
            var (qw, qx, qy, qz) = OffsetMath.ComputeRotationQuaternion(
                mainRaDeg, mainDecDeg,
                guideRaDeg, guideDecDeg);

            settings.RotationQw = qw;
            settings.RotationQx = qx;
            settings.RotationQy = qy;
            settings.RotationQz = qz;

            // Legacy arcsec for comparison/debug
            // Legacy arcsec for comparison/debug
            var (dRaArcsec, dDecArcsec) = OffsetMath.ComputeOffsetArcsec(mainRaDeg, mainDecDeg, guideRaDeg, guideDecDeg);
            settings.OffsetRaArcsec = dRaArcsec;
            settings.OffsetDecArcsec = dDecArcsec;

            // ✅ canonical (nullable)
            settings.OffsetLastCalibratedUtc = DateTime.UtcNow;

            var angleDeg = ComputeRotationAngleDeg(qw);

            // OffsetCalibrationResult expects DateTime (non-null) -> safe because we just set it
            return new OffsetCalibrationResult(angleDeg, dRaArcsec, dDecArcsec, settings.OffsetLastCalibratedUtc.Value);

        }

        public (double raDeg, double decDeg) ApplyToGuiderSolve(PlateSolvePlusSettings settings, double guideRaDeg, double guideDecDeg) {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!settings.OffsetEnabled) {
                return (guideRaDeg, guideDecDeg);
            }

            if (settings.OffsetMode == OffsetMode.Rotation) {
                return OffsetMath.ApplyRotationQuaternion(
                    guideRaDeg,
                    guideDecDeg,
                    settings.RotationQw,
                    settings.RotationQx,
                    settings.RotationQy,
                    settings.RotationQz);
            }

            return OffsetMath.ApplyOffsetArcsec(
                guideRaDeg,
                guideDecDeg,
                settings.OffsetRaArcsec,
                settings.OffsetDecArcsec);
        }

        public string GetQuaternionText(PlateSolvePlusSettings settings) {
            if (settings == null) return "(1,0,0,0)";

            return $"({settings.RotationQw:0.######}, {settings.RotationQx:0.######}, {settings.RotationQy:0.######}, {settings.RotationQz:0.######})";
        }

        public string GetRotationAngleText(PlateSolvePlusSettings settings) {
            if (settings == null) return "-";
            return $"{ComputeRotationAngleDeg(settings.RotationQw):0.####}";
        }

        private static double ComputeRotationAngleDeg(double qw) {
            double c = qw;
            if (c < -1) c = -1;
            if (c > 1) c = 1;
            double angleRad = 2.0 * Math.Acos(c);
            return angleRad * (180.0 / Math.PI);
        }
    }
}
