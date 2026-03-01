using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugins.PlateSolvePlus.Utils;
using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal sealed class TelescopeReferenceUpdatedEventArgs : EventArgs {
        public bool Success { get; }
        public string StatusText { get; }
        public double? RaDeg { get; }
        public double? DecDeg { get; }

        public TelescopeReferenceUpdatedEventArgs(bool success, string statusText, double? raDeg, double? decDeg) {
            Success = success;
            StatusText = statusText ?? "";
            RaDeg = raDeg;
            DecDeg = decDeg;
        }
    }

    internal interface ITelescopeReferenceService : IDisposable {
        ITelescopeMediator? TelescopeMediator { get; set; }

        bool TryGetCurrentRaDec(out double raDeg, out double decDeg);

        event EventHandler<TelescopeReferenceUpdatedEventArgs>? ReferenceUpdated;
    }

    // MEF export so both dockables can import the same shared instance
    [Export(typeof(ITelescopeReferenceService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class TelescopeReferenceService : ITelescopeReferenceService {
        private ITelescopeMediator? telescopeMediator;

        public ITelescopeMediator? TelescopeMediator {
            get => telescopeMediator;
            set {
                if (ReferenceEquals(telescopeMediator, value)) return;

                if (telescopeMediator != null) {
                    try { telescopeMediator.Slewed -= TelescopeMediator_Slewed; } catch { }
                }

                telescopeMediator = value;

                if (telescopeMediator != null) {
                    try { telescopeMediator.Slewed += TelescopeMediator_Slewed; } catch { }
                }

                PublishCurrent();
            }
        }

        public event EventHandler<TelescopeReferenceUpdatedEventArgs>? ReferenceUpdated;

        public bool TryGetCurrentRaDec(out double raDeg, out double decDeg) {
            raDeg = 0;
            decDeg = 0;

            if (telescopeMediator == null) return false;

            try {
                // 1) ITelescopeMediator.GetCurrentPosition() -> Coordinates or nested Coordinates
                var pos = telescopeMediator.GetCurrentPosition();
                if (pos != null) {
                    if (ReflectionRead.TryReadNumber(pos, new[] { "RightAscension", "RA", "Ra", "RaHours" }, out var raVal) &&
                        ReflectionRead.TryReadNumber(pos, new[] { "Declination", "Dec", "DEC" }, out var decVal)) {

                        raDeg = AstroFormat.GuessRaToDegrees(raVal);
                        decDeg = decVal;
                        return true;
                    }

                    var nested = TryGetPropObj(pos, "Coordinates");
                    if (nested != null &&
                        ReflectionRead.TryReadNumber(nested, new[] { "RightAscension", "RA", "Ra", "RaHours" }, out raVal) &&
                        ReflectionRead.TryReadNumber(nested, new[] { "Declination", "Dec", "DEC" }, out decVal)) {

                        raDeg = AstroFormat.GuessRaToDegrees(raVal);
                        decDeg = decVal;
                        return true;
                    }
                }

                // 2) ITelescopeMediator.GetInfo() -> in your setup RightAscension (hours) and Declination (deg) are doubles
                var info = InvokeGetInfo(telescopeMediator);
                if (info != null) {
                    if (ReflectionRead.TryReadNumber(info, new[] { "RightAscension" }, out var raHours) &&
                        ReflectionRead.TryReadNumber(info, new[] { "Declination" }, out var decDegrees)) {

                        raDeg = AstroFormat.GuessRaToDegrees(raHours);
                        decDeg = decDegrees;
                        return true;
                    }

                    // fallback: Coordinates property on info
                    var coords = TryGetPropObj(info, "Coordinates") ?? TryGetPropObj(info, "TargetCoordinates");
                    if (coords != null &&
                        ReflectionRead.TryReadNumber(coords, new[] { "RightAscension", "RA", "Ra", "RaHours" }, out var raC) &&
                        ReflectionRead.TryReadNumber(coords, new[] { "Declination", "Dec", "DEC" }, out var decC)) {

                        raDeg = AstroFormat.GuessRaToDegrees(raC);
                        decDeg = decC;
                        return true;
                    }
                }

                // 3) Last resort: direct properties on mediator
                if (ReflectionRead.TryReadNumber(telescopeMediator, new[] { "RightAscension", "RA", "Ra" }, out var raM) &&
                    ReflectionRead.TryReadNumber(telescopeMediator, new[] { "Declination", "Dec", "DEC" }, out var decM)) {

                    raDeg = AstroFormat.GuessRaToDegrees(raM);
                    decDeg = decM;
                    return true;
                }

                return false;
            } catch {
                return false;
            }
        }

        private static object? InvokeGetInfo(ITelescopeMediator tm) {
            try {
                var mi = tm.GetType().GetMethod("GetInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return mi?.Invoke(tm, null);
            } catch {
                return null;
            }
        }

        private static object? TryGetPropObj(object src, string name) {
            try {
                var p = src.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                return p?.GetValue(src);
            } catch {
                return null;
            }
        }

        public void Dispose() {
            if (telescopeMediator != null) {
                try { telescopeMediator.Slewed -= TelescopeMediator_Slewed; } catch { }
            }
            telescopeMediator = null;
        }

        // Slewed event handler expects Task return type (not void)
        private async Task TelescopeMediator_Slewed(object sender, MountSlewedEventArgs e) {
            try {
                PublishCurrent();
                await Task.CompletedTask;
            } catch {
                // keep event handler robust
            }
        }

        private void PublishCurrent() {
            if (telescopeMediator == null) {
                ReferenceUpdated?.Invoke(this, new TelescopeReferenceUpdatedEventArgs(
                    success: false,
                    statusText: "ITelescopeMediator not available.",
                    raDeg: null,
                    decDeg: null));
                return;
            }

            if (TryGetCurrentRaDec(out var raDeg, out var decDeg)) {
                ReferenceUpdated?.Invoke(this, new TelescopeReferenceUpdatedEventArgs(
                    success: true,
                    statusText: "Updated via Slew/Refresh.",
                    raDeg: raDeg,
                    decDeg: decDeg));
            } else {
                ReferenceUpdated?.Invoke(this, new TelescopeReferenceUpdatedEventArgs(
                    success: false,
                    statusText: "Could not read telescope coordinates.",
                    raDeg: null,
                    decDeg: null));
            }
        }
    }
}
