using System;

namespace NINA.Plugins.PlateSolvePlus {
    public static class OffsetMath {
        // ============================
        // Public API
        // ============================

        // Rotation calibration: returns quaternion (w,x,y,z) that rotates guide-vector to main-vector
        public static (double qw, double qx, double qy, double qz) ComputeRotationQuaternion(
            double mainRaDeg, double mainDecDeg,
            double guideRaDeg, double guideDecDeg) {
            var vg = RadecToUnitVector(guideRaDeg, guideDecDeg);
            var vm = RadecToUnitVector(mainRaDeg, mainDecDeg);

            // cross and dot
            var cx = vg.y * vm.z - vg.z * vm.y;
            var cy = vg.z * vm.x - vg.x * vm.z;
            var cz = vg.x * vm.y - vg.y * vm.x;

            var dot = Clamp(vg.x * vm.x + vg.y * vm.y + vg.z * vm.z, -1.0, 1.0);
            var crossNorm = Math.Sqrt(cx * cx + cy * cy + cz * cz);

            // If vectors are almost identical: identity rotation
            if (crossNorm < 1e-12) {
                // If opposite direction (rare in this context), choose arbitrary axis orthogonal to vg
                if (dot < -0.999999999999) {
                    var axis = FindOrthogonalAxis(vg);
                    // 180° rotation => qw=0, qv=axis normalized
                    return NormalizeQuaternion(0, axis.x, axis.y, axis.z);
                }

                return (1, 0, 0, 0);
            }

            // axis normalized
            var ax = cx / crossNorm;
            var ay = cy / crossNorm;
            var az = cz / crossNorm;

            // angle
            var theta = Math.Acos(dot);
            var half = theta / 2.0;

            var qw = Math.Cos(half);
            var s = Math.Sin(half);

            var qx = ax * s;
            var qy = ay * s;
            var qz = az * s;

            return NormalizeQuaternion(qw, qx, qy, qz);
        }

        // Apply rotation quaternion to guider solve -> corrected RA/Dec (deg)
        public static (double raDeg, double decDeg) ApplyRotationQuaternion(
            double guideRaDeg, double guideDecDeg,
            double qw, double qx, double qy, double qz) {
            var v = RadecToUnitVector(guideRaDeg, guideDecDeg);
            var vr = RotateVectorByQuaternion(v.x, v.y, v.z, qw, qx, qy, qz);
            return UnitVectorToRadec(vr.x, vr.y, vr.z);
        }

        // Legacy: Tangent-plane arcsec offset (kept as fallback/debug)
        public static (double dRaArcsec, double dDecArcsec) ComputeOffsetArcsec(
            double mainRaDeg, double mainDecDeg,
            double guideRaDeg, double guideDecDeg) {
            double dDecArcsec = (mainDecDeg - guideDecDeg) * 3600.0;

            double dRaDeg = WrapRaDeltaDeg(mainRaDeg - guideRaDeg);
            double cosDec = Math.Cos(Deg2Rad(mainDecDeg));
            double dRaArcsecOnSky = dRaDeg * cosDec * 3600.0;

            return (dRaArcsecOnSky, dDecArcsec);
        }

        public static (double raCorrDeg, double decCorrDeg) ApplyOffsetArcsec(
            double guideRaDeg, double guideDecDeg,
            double dRaArcsecOnSky, double dDecArcsec) {
            double decCorr = guideDecDeg + (dDecArcsec / 3600.0);

            double cosDec = Math.Cos(Deg2Rad(decCorr));
            if (Math.Abs(cosDec) < 1e-12)
                cosDec = 1e-12;

            double raCorr = guideRaDeg + ((dRaArcsecOnSky / 3600.0) / cosDec);
            raCorr = NormalizeRaDeg(raCorr);

            return (raCorr, decCorr);
        }

        // ============================
        // Vector / Quaternion helpers
        // ============================

        private static (double x, double y, double z) RadecToUnitVector(double raDeg, double decDeg) {
            double ra = Deg2Rad(raDeg);
            double dec = Deg2Rad(decDeg);

            double cosD = Math.Cos(dec);
            return (cosD * Math.Cos(ra), cosD * Math.Sin(ra), Math.Sin(dec));
        }

        private static (double raDeg, double decDeg) UnitVectorToRadec(double x, double y, double z) {
            // normalize for safety
            double n = Math.Sqrt(x * x + y * y + z * z);
            if (n < 1e-18) n = 1e-18;
            x /= n; y /= n; z /= n;

            double dec = Math.Asin(Clamp(z, -1, 1));
            double ra = Math.Atan2(y, x);
            if (ra < 0) ra += 2 * Math.PI;

            return (Rad2Deg(ra), Rad2Deg(dec));
        }

        private static (double x, double y, double z) RotateVectorByQuaternion(
            double vx, double vy, double vz,
            double qw, double qx, double qy, double qz) {
            // v' = q * v * q^-1 (with v as pure quaternion 0,v)
            // optimized quaternion-vector rotation
            // t = 2 * cross(q_vec, v)
            double tx = 2.0 * (qy * vz - qz * vy);
            double ty = 2.0 * (qz * vx - qx * vz);
            double tz = 2.0 * (qx * vy - qy * vx);

            // v' = v + qw * t + cross(q_vec, t)
            double cx = (qy * tz - qz * ty);
            double cy = (qz * tx - qx * tz);
            double cz = (qx * ty - qy * tx);

            return (vx + qw * tx + cx,
                    vy + qw * ty + cy,
                    vz + qw * tz + cz);
        }

        private static (double qw, double qx, double qy, double qz) NormalizeQuaternion(double qw, double qx, double qy, double qz) {
            double n = Math.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            if (n < 1e-18) return (1, 0, 0, 0);
            return (qw / n, qx / n, qy / n, qz / n);
        }

        private static (double x, double y, double z) FindOrthogonalAxis((double x, double y, double z) v) {
            // pick axis not parallel to v, then cross and normalize
            (double x, double y, double z) a = Math.Abs(v.x) < 0.9 ? (1, 0, 0) : (0, 1, 0);

            double cx = v.y * a.z - v.z * a.y;
            double cy = v.z * a.x - v.x * a.z;
            double cz = v.x * a.y - v.y * a.x;

            double n = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (n < 1e-18) return (1, 0, 0);
            return (cx / n, cy / n, cz / n);
        }

        // ============================
        // Angle helpers
        // ============================

        private static double Deg2Rad(double deg) => deg * (Math.PI / 180.0);
        private static double Rad2Deg(double rad) => rad * (180.0 / Math.PI);

        private static double NormalizeRaDeg(double raDeg) {
            raDeg %= 360.0;
            if (raDeg < 0) raDeg += 360.0;
            return raDeg;
        }

        private static double WrapRaDeltaDeg(double dRaDeg) {
            // wrap to [-180..+180]
            dRaDeg %= 360.0;
            if (dRaDeg > 180.0) dRaDeg -= 360.0;
            if (dRaDeg < -180.0) dRaDeg += 360.0;
            return dRaDeg;
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
