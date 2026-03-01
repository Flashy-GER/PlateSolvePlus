using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface ISecondaryAutofocusService {
        Task<SecondaryAutofocusResult> RunAsync(
            SecondaryAutofocusSettings settings,
            SecondaryAutofocusRunState state,
            CancellationToken token);

        void Cancel();
    }
}

