using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    /// <summary>
    /// Focuser motor adapter for NINA focuser mediator.
    ///
    /// IMPORTANT:
    /// IFocuserMediator does not expose a safe "read position" API. Using MoveFocuserRelative(0)
    /// as a position query can re-enter driver/middleware move code and may lead to stack exhaustion
    /// (the "Schutzseite für den Stapel" crash) depending on driver behavior.
    ///
    /// This implementation therefore:
    ///  - Serializes all focuser calls (no concurrent Move/Get)
    ///  - Maintains a cached position based on the return value of MoveFocuser/MoveFocuserRelative
    ///  - Returns the cached position for GetPositionAsync (no move call)
    ///
    /// If the cached position is not yet known, GetPositionAsync returns the last commanded target if present,
    /// otherwise 0. This is safer than calling MoveFocuserRelative(0) and avoids re-entrancy crashes.
    /// </summary>
    public sealed class NinaFocuserMotorService : IFocusMotorService {
        private readonly IFocuserMediator _focuser;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private int? _cachedPosition;
        private int? _lastTarget;

        public NinaFocuserMotorService(IFocuserMediator focuserMediator) {
            _focuser = focuserMediator ?? throw new ArgumentNullException(nameof(focuserMediator));
        }

        public async Task<int> GetPositionAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            // No mediator API to read position safely. Return cached/last-known values.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (_cachedPosition.HasValue) return _cachedPosition.Value;
                if (_lastTarget.HasValue) return _lastTarget.Value;

                // Unknown at startup; return 0 rather than issuing a move-based "query".
                return 0;
            } finally {
                _gate.Release();
            }
        }

        public async Task MoveToAsync(int position, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                _lastTarget = position;

                // Absolute move; mediator returns new position (driver-dependent but typically current position).
                int newPos = await _focuser.MoveFocuser(position, ct).ConfigureAwait(false);
                _cachedPosition = newPos;
            } finally {
                _gate.Release();
            }
        }

        public Task SettleAsync(int settleMs, CancellationToken ct) {
            if (settleMs <= 0) return Task.CompletedTask;
            return Task.Delay(settleMs, ct);
        }

        /// <summary>
        /// Optional helper (not part of interface): if you have an external source of truth (e.g. FocuserInfo),
        /// you can call this to seed the cached position to improve OneWayApproach/backlash strategies.
        /// </summary>
        public void SeedPosition(int position) {
            _cachedPosition = position;
            _lastTarget = position;
        }
    }
}
