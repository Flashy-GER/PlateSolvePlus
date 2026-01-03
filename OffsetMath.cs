using System;

namespace NINA.Plugins.PlateSolvePlus {
    internal static class OffsetMath {
        // Erwartung: RA/Dec in DEGREES (am Himmel)
        // Wenn du RA in Stunden hast -> vorher Stunden*15.
        public static (double dRaArcsec, double dDecArcsec) ComputeOffsetArcsec(
            double mainRaDeg, double mainDecDeg,
            double guideRaDeg, double guideDecDeg) {
            // ΔDec (arcsec)
            var dDecArcsec = (mainDecDeg - guideDecDeg) * 3600.0;

            // ΔRA am Himmel (arcsec on sky) = ΔRA(deg) * cos(dec) * 3600
            var decRad = DegToRad(mainDecDeg);
            var dRaDeg = NormalizeDeltaDegrees(mainRaDeg - guideRaDeg); // wrap safe
            var dRaArcsec = dRaDeg * Math.Cos(decRad) * 3600.0;

            return (dRaArcsec, dDecArcsec);
        }

        public static (double raDegCorr, double decDegCorr) ApplyOffsetArcsec(
            double guideRaDeg, double guideDecDeg,
            double offsetRaArcsec, double offsetDecArcsec) {
            var decCorr = guideDecDeg + (offsetDecArcsec / 3600.0);

            // RA korrigieren: RA_corr = RA_guide + (ΔRA_arcsec / 3600) / cos(dec_corr)
            var cosDec = Math.Cos(DegToRad(decCorr));
            if (Math.Abs(cosDec) < 1e-12) cosDec = 1e-12; // Polschutz

            var raCorr = guideRaDeg + ((offsetRaArcsec / 3600.0) / cosDec);

            // RA wieder in [0..360)
            raCorr = NormalizeDegrees(raCorr);

            return (raCorr, decCorr);
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;

        private static double NormalizeDegrees(double deg) {
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        // Delta in [-180..+180], damit wrap sauber ist
        private static double NormalizeDeltaDegrees(double d) {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return d;
        }
    }
}
