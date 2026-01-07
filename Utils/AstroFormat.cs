using System;

namespace NINA.Plugins.PlateSolvePlus.Utils {
    internal static class AstroFormat {
        /// <summary>
        /// Some NINA/solver APIs may provide RA in hours (0..24) or in degrees (0..360).
        /// This helper guesses and converts to degrees.
        /// </summary>
        public static double GuessRaToDegrees(double ra) => (ra >= 0 && ra <= 24.0) ? ra * 15.0 : ra;

        public static string FormatRaHms(double raDeg) {
            var raHours = raDeg / 15.0;
            if (raHours < 0) raHours += 24.0;
            raHours %= 24.0;

            var h = (int)Math.Floor(raHours);
            var mFloat = (raHours - h) * 60.0;
            var m = (int)Math.Floor(mFloat);
            var s = (mFloat - m) * 60.0;

            return $"{h:00}:{m:00}:{s:00.##}";
        }

        public static string FormatDecDms(double decDeg) {
            var sign = decDeg < 0 ? "-" : "+";
            var a = Math.Abs(decDeg);
            var d = (int)Math.Floor(a);
            var mFloat = (a - d) * 60.0;
            var m = (int)Math.Floor(mFloat);
            var s = (mFloat - m) * 60.0;

            return $"{sign}{d:00}° {m:00}' {s:00.##}\"";
        }
    }
}
