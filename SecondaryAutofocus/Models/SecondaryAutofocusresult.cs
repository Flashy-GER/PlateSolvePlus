using System;
using System.Collections.Generic;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models {
    public sealed class SecondaryAutofocusResult {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public string? Error { get; init; }

        public int StartPosition { get; init; }
        public int BestPosition { get; init; }
        public double BestHfr { get; init; }

        public IReadOnlyList<FocusSample> Samples { get; init; } = Array.Empty<FocusSample>();
        public CurveFitResult? Fit { get; init; }
    }

    public sealed record CurveFitResult(
        string Model,
        double R2,
        double A,
        double B,
        double C,
        double EstimatedBestPosition
    );
}
