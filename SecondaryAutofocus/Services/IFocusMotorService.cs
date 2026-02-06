using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public interface IFocusMotorService {
        Task<int> GetPositionAsync(CancellationToken ct);
        Task MoveToAsync(int position, CancellationToken ct);
        Task SettleAsync(int settleMs, CancellationToken ct);
    }
}
