using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface ISecondaryAutofocusService {
        Task<SecondaryAutofocusResult> RunAsync(SecondaryAutofocusSettings settings, SecondaryAutofocusRunState state, CancellationToken ct);
        void Cancel();
        Task RunAsync(PlateSolvePlusSettings.SecondaryAutofocusSettings settings, SecondaryAutofocusRunState runState, CancellationToken token);
    }
}
