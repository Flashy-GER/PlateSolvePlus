using System;
using System.Reflection;
using System.Windows.Input;
using NINA.Core.Model;
using NINA.Core.Utility;

namespace NINA.Plugins.PlateSolvePlus {
    public class PlateSolvePlusDockableVM : BaseINPC {
        // Injecte hier das, was du hast (ServiceProvider, Mediator, VM…)
        private readonly object telescopeSource;

        public PlateSolvePlusSettings Settings { get; }

        public ICommand CalibrateOffsetCommand { get; }
        public ICommand ResetOffsetCommand { get; }

        private string statusLine = "Waiting for solve…";
        public string StatusLine {
            get => statusLine;
            set { statusLine = value; RaisePropertyChanged(); }
        }

        private (double raDeg, double decDeg)? lastGuiderSolveDeg;

        private string lastGuiderSolveText = "-";
        public string LastGuiderSolveText {
            get => lastGuiderSolveText;
            set { lastGuiderSolveText = value; RaisePropertyChanged(); }
        }

        private string correctedSolveText = "-";
        public string CorrectedSolveText {
            get => correctedSolveText;
            set { correctedSolveText = value; RaisePropertyChanged(); }
        }

        public string LastCalibrationText =>
            Settings.LastOffsetCalibrationUtc.HasValue
                ? Settings.LastOffsetCalibrationUtc.Value.ToString("yyyy-MM-dd HH:mm:ss")
                : "-";

        public PlateSolvePlusDockableVM(object telescopeSource, PlateSolvePlusSettings settings) {
            this.telescopeSource = telescopeSource;
            Settings = settings;

            CalibrateOffsetCommand = new RelayCommand(_ => CalibrateOffset(), _ => CanCalibrate());
            ResetOffsetCommand = new RelayCommand(_ => {
                Settings.ResetOffset();
                StatusLine = "Offset reset.";
                RaisePropertyChanged(nameof(LastCalibrationText));
                UpdateCorrectedText();
            });

            Settings.PropertyChanged += (_, __) => {
                RaisePropertyChanged(nameof(LastCalibrationText));
                UpdateCorrectedText();
            };
        }

        // Diese Methode rufst du in deinem bestehenden Solve-Flow auf,
        // sobald der Guider platesolve erfolgreich war.
        //
        // Wichtig: RA/Dec hier in DEGREES übergeben!
        public void OnGuiderSolveSuccess(double guideRaDeg, double guideDecDeg) {
            lastGuiderSolveDeg = (guideRaDeg, guideDecDeg);

            LastGuiderSolveText =
                $"RA: {FormatRaHms(guideRaDeg)}  |  Dec: {FormatDecDms(guideDecDeg)}  (deg: {guideRaDeg:0.######}, {guideDecDeg:0.######})";

            StatusLine = Settings.OffsetEnabled
                ? "Guider solve received. Offset is enabled."
                : "Guider solve received. Offset is disabled.";

            UpdateCorrectedText();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool CanCalibrate() {
            return lastGuiderSolveDeg.HasValue && TryGetTelescopeRaDecDeg(out _, out _);
        }

        private void CalibrateOffset() {
            if (!lastGuiderSolveDeg.HasValue) {
                StatusLine = "No guider solve available yet.";
                return;
            }

            if (!TryGetTelescopeRaDecDeg(out var mainRaDeg, out var mainDecDeg)) {
                StatusLine = "Could not read telescope RA/Dec for main reference.";
                return;
            }

            var (guideRaDeg, guideDecDeg) = lastGuiderSolveDeg.Value;

            var (dRaArcsec, dDecArcsec) = OffsetMath.ComputeOffsetArcsec(
                mainRaDeg, mainDecDeg,
                guideRaDeg, guideDecDeg);

            Settings.OffsetRaArcsec = dRaArcsec;
            Settings.OffsetDecArcsec = dDecArcsec;
            Settings.LastOffsetCalibrationUtc = DateTime.UtcNow;

            StatusLine =
                $"Offset calibrated. Main(Telescope) - Guide(last).  dRA={dRaArcsec:0.###}\"  dDec={dDecArcsec:0.###}\"";

            RaisePropertyChanged(nameof(LastCalibrationText));
            UpdateCorrectedText();
        }

        private void UpdateCorrectedText() {
            if (!lastGuiderSolveDeg.HasValue) {
                CorrectedSolveText = "-";
                return;
            }

            var (guideRaDeg, guideDecDeg) = lastGuiderSolveDeg.Value;

            if (!Settings.OffsetEnabled) {
                CorrectedSolveText = "Offset disabled → using guider solve as-is.";
                return;
            }

            var (raCorr, decCorr) = OffsetMath.ApplyOffsetArcsec(
                guideRaDeg, guideDecDeg,
                Settings.OffsetRaArcsec, Settings.OffsetDecArcsec);

            CorrectedSolveText =
                $"RA: {FormatRaHms(raCorr)}  |  Dec: {FormatDecDms(decCorr)}  (deg: {raCorr:0.######}, {decCorr:0.######})";
        }

        // -------------------------
        // Telescope coordinate read
        // -------------------------
        private bool TryGetTelescopeRaDecDeg(out double raDeg, out double decDeg) {
            raDeg = 0;
            decDeg = 0;

            try {
                // Wir versuchen mehrere typische Property-Namen:
                // - RightAscension / Declination (oft Stunden + Grad)
                // - RA / Dec
                // - Coordinates.RightAscension etc.

                object src = telescopeSource;

                // 1) Direktproperties
                if (TryReadNumber(src, new[] { "RightAscension", "RA" }, out var raVal) &&
                    TryReadNumber(src, new[] { "Declination", "Dec" }, out var decVal)) {
                    // Heuristik: RA kann Stunden sein (0..24) oder Grad (0..360)
                    raDeg = GuessRaToDegrees(raVal);
                    decDeg = decVal;
                    return true;
                }

                // 2) Nested: Coordinates.*
                if (TryReadObject(src, new[] { "Coordinates", "TelescopeCoordinates", "CurrentCoordinates" }, out var coords)) {
                    if (TryReadNumber(coords, new[] { "RightAscension", "RA" }, out raVal) &&
                        TryReadNumber(coords, new[] { "Declination", "Dec" }, out decVal)) {
                        raDeg = GuessRaToDegrees(raVal);
                        decDeg = decVal;
                        return true;
                    }
                }

                // 3) Nested: Position.*
                if (TryReadObject(src, new[] { "Position", "TelescopePosition" }, out var pos)) {
                    if (TryReadNumber(pos, new[] { "RightAscension", "RA" }, out raVal) &&
                        TryReadNumber(pos, new[] { "Declination", "Dec" }, out decVal)) {
                        raDeg = GuessRaToDegrees(raVal);
                        decDeg = decVal;
                        return true;
                    }
                }

                return false;
            } catch {
                return false;
            }
        }

        private static bool TryReadObject(object src, string[] names, out object obj) {
            obj = null;
            foreach (var n in names) {
                var p = src.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public);
                if (p == null) continue;

                obj = p.GetValue(src);
                if (obj != null) return true;
            }
            return false;
        }

        private static bool TryReadNumber(object src, string[] names, out double value) {
            value = 0;
            foreach (var n in names) {
                var p = src.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public);
                if (p == null) continue;

                var v = p.GetValue(src);
                if (v == null) continue;

                if (v is double d) { value = d; return true; }
                if (v is float f) { value = f; return true; }
                if (v is int i) { value = i; return true; }
                if (double.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
            }
            return false;
        }

        private static double GuessRaToDegrees(double ra) {
            // wenn RA in [0..24] -> vermutlich Stunden
            if (ra >= 0 && ra <= 24.0) return ra * 15.0;
            // wenn RA in [0..360] -> Grad
            return ra;
        }

        // -------------------------
        // Formatting helpers
        // -------------------------
        private static string FormatRaHms(double raDeg) {
            // RA in hours
            var raHours = raDeg / 15.0;
            if (raHours < 0) raHours += 24.0;
            raHours %= 24.0;

            var h = (int)Math.Floor(raHours);
            var mFloat = (raHours - h) * 60.0;
            var m = (int)Math.Floor(mFloat);
            var s = (mFloat - m) * 60.0;

            return $"{h:00}:{m:00}:{s:00.##}";
        }

        private static string FormatDecDms(double decDeg) {
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
