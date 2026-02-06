using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Plot {
    public interface ISecondaryAutofocusPlotSink {
        void StartNewRun(int totalSteps);
        void AddSample(FocusSample sample, int stepIndex1Based, int totalSteps, int bestPos, double bestHfr);
        void SetFitBest(int bestFromFitPos, double bestFromFitHfr);
        void SetPhase(string statusText);
    }
}
