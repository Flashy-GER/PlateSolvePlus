using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface IStarMetricService {
        Task<StarMetricResult> MeasureAsync(SecondaryFrame frame, SecondaryAutofocusSettings settings, CancellationToken ct);
    }

    public sealed record StarMetricResult(double Hfr, int StarCount);
}
