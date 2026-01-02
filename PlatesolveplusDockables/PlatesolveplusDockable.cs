using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Nito.Mvvm;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Flashy_Ger.NINA.Platesolveplus.PlatesolveplusDockables {
    public class PlateSolvePlusDockable : DockableVM {
        private readonly IGuiderMediator guiderMediator;

        public ICommand SolveGuiderCommand { get; }

        [ImportingConstructor]
        public PlateSolvePlusDockable(
            IProfileService profileService,
            IGuiderMediator guiderMediator
        ) : base(profileService) {
            this.guiderMediator = guiderMediator;

            Title = "PlateSolvePlus";

            // Icon aus deinem ResourceDictionary holen
            var dict = new ResourceDictionary {
                Source = new Uri(
                    "Flashy_Ger.NINA.Platesolveplus;component/PlatesolveplusDockables/PlatesolveplusDockableTemplates.xaml",
                    UriKind.RelativeOrAbsolute)
            };

            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Flashy_Ger.NINA.Platesolveplus_PlateSolveSVG"];
            ImageGeometry.Freeze();

            SolveGuiderCommand = new AsyncCommand(SolveGuiderAsync);
        }
        private Task SolveGuiderAsync() {
            // MVP: nur Debug / später ersetzen wir das durch echten Status
            System.Diagnostics.Debug.WriteLine("PlateSolvePlus: Solve (Guider) clicked.");
            return Task.CompletedTask;
        }


    }
}
