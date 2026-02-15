using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface IStarMetricService {
        Task<StarMetricResult> MeasureAsync(SecondaryFrame frame, SecondaryAutofocusSettings settings, CancellationToken ct);
    }

    public sealed record StarMetricResult(double Hfr, int StarCount);
}
