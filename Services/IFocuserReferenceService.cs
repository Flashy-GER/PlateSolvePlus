using System;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Plugins.PlateSolvePlus.Services {
    internal interface IFocuserReferenceService : IDisposable {
        IFocuserMediator? FocuserMediator { get; set; }

        bool TryGetPosition(out int position);
        bool CanMoveAbsolute { get; }
        bool IsConnected { get; }

        event EventHandler? ReferenceUpdated;
    }
}
