using System.Collections.Generic;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface ICurveFitService {
        CurveFitResult Fit(IReadOnlyList<FocusSample> samples);
        int GetBestPosition(CurveFitResult fit, int fallbackBest, int minPos, int maxPos);
    }
}
