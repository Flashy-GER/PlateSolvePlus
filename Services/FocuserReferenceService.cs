using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using OxyPlot;

namespace NINA.Plugins.PlateSolvePlus.Services {

    [Export(typeof(IFocuserReferenceService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class FocuserReferenceService : IFocuserReferenceService, IFocuserConsumer, IDisposable {

        private IFocuserMediator? _mediator;
        private FocuserInfo _last = new FocuserInfo();
        public event EventHandler? ReferenceUpdated;
        public IFocuserMediator? FocuserMediator {
            get => _mediator;
            set {
                if (ReferenceEquals(_mediator, value)) return;

                // unhook old
                if (_mediator != null) {
                    InvokeConsumerMethod(_mediator, "RemoveConsumer", this);
                }

                _mediator = value;

                // hook new
                if (_mediator != null) {
                    // Your dump proves this exists:
                    // DeviceMediator<..>.RegisterConsumer(IFocuserConsumer)
                    InvokeConsumerMethod(_mediator, "RegisterConsumer", this);
                    Logger.Debug("[PlateSolvePlus] FocuserReferenceService registered as IFocuserConsumer.");
                } else {
                    Logger.Debug("[PlateSolvePlus] FocuserReferenceService mediator cleared.");
                }

                // initial refresh
                ReferenceUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        public int Position { get; private set; }
        public bool IsConnected { get; private set; }
        public int LastPosition => _last?.Position ?? 0;
        public bool CanMoveAbsolute {
            get {
                // If your interface requires this flag, keep it simple:
                // Connected usually implies motor usable; actual move will still fail safely if not supported.
                return IsConnected;
            }
        }

        public bool TryGetPosition(out int position) {
            position = _last?.Position ?? 0;
            return (_last?.Connected ?? false) && position > 0;
        }

        // =========================
        // IFocuserConsumer callbacks
        // =========================

        // This is the crucial callback for live updates (comes from FocuserVM -> focuserMediator.Broadcast(FocuserInfo))
        public void UpdateDeviceInfo(FocuserInfo info) {
            _last = info ?? new FocuserInfo();
            Logger.Debug($"[PlateSolvePlus] UpdateDeviceInfo received: this={GetHashCode()} Connected={_last.Connected} Pos={_last.Position}");
            ReferenceUpdated?.Invoke(this, EventArgs.Empty);
            Position = _last.Position;
            IsConnected = _last.Connected;
        }

        // These are required because FocuserMediator calls them on consumers (see your pasted source).
        // We don't need them for connection state, so no-op is fine.
        public void UpdateUserFocused(FocuserInfo info) { }
        public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
        public void NewAutoFocusPoint(DataPoint dataPoint) { }
        public void AutoFocusRunStarting() { }

        public void Dispose() {
            try {
                if (_mediator != null) {
                    InvokeConsumerMethod(_mediator, "RemoveConsumer", this);
                }
            } catch { }
            _mediator = null;
        }

        private static void InvokeConsumerMethod(object mediator, string methodName, IFocuserConsumer consumer) {
            var t = mediator.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            // Match both direct + explicit interface method names
            var mi = methods.FirstOrDefault(m =>
                string.Equals(m.Name, methodName, StringComparison.Ordinal) ||
                m.Name.EndsWith("." + methodName, StringComparison.Ordinal));

            if (mi == null) {
                Logger.Warning($"[PlateSolvePlus] Mediator method not found: {methodName}");
                return;
            }

            var ps = mi.GetParameters();
            if (ps.Length != 1) {
                Logger.Warning($"[PlateSolvePlus] Mediator method signature unexpected: {mi.Name}({ps.Length} params)");
                return;
            }

            // Ensure parameter accepts IFocuserConsumer
            if (!ps[0].ParameterType.IsAssignableFrom(typeof(IFocuserConsumer))) {
                Logger.Warning($"[PlateSolvePlus] Mediator method param mismatch: {mi.Name} expects {ps[0].ParameterType.FullName}");
                return;
            }

            mi.Invoke(mediator, new object[] { consumer });
        }
    }
}
