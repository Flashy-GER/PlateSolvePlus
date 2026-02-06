using System;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models {
    public sealed record FocusSample(
        int Position,
        double Hfr,
        int StarCount,
        DateTime TimestampUtc,
        string? Note = null
    );
}
