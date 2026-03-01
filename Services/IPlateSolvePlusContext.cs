using System;

namespace NINA.Plugins.PlateSolvePlus.Services {

    internal sealed class GuiderSolveSnapshot {
        public DateTime UtcTimestamp { get; }
        public double RaDeg { get; }
        public double DecDeg { get; }
        public string? Summary { get; }

        public GuiderSolveSnapshot(DateTime utcTimestamp, double raDeg, double decDeg, string? summary = null) {
            UtcTimestamp = utcTimestamp;
            RaDeg = raDeg;
            DecDeg = decDeg;
            Summary = summary;
        }
    }

    internal interface IPlateSolvePlusContext {
        IAscomDeviceDiscoveryService AscomDiscovery { get; }

        /// <summary>Globale Auswahl (optional). Kann von Dockables gesetzt werden.</summary>
        string? CurrentSecondaryCameraProgId { get; set; }

        /// <summary>
        /// Setzt den "aktiven" SecondaryCamera ProgId. Der Context sorgt dafür,
        /// dass alte Instanzen freigegeben werden (Disconnect + Dispose).
        /// </summary>
        void SetActiveSecondaryCameraProgId(string progId);

        /// <summary>
        /// Liefert die aktive SecondaryCameraService Instanz (für CurrentSecondaryCameraProgId).
        /// </summary>
        ISecondaryCameraService GetActiveSecondaryCameraService();

        /// <summary>Letzter erfolgreicher Guider-Solve (vom CameraDockable).</summary>
        GuiderSolveSnapshot? LastGuiderSolve { get; }

        /// <summary>Wird gefeuert, wenn CameraDockable einen neuen Solve geschrieben hat.</summary>
        event EventHandler? LastGuiderSolveUpdated;

        /// <summary>Schreibt den Solve in den gemeinsamen Context.</summary>
        void SetLastGuiderSolve(GuiderSolveSnapshot snapshot);
    }
}
