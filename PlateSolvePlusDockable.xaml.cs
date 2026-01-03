using System.Windows.Controls;

namespace NINA.Plugins.PlateSolvePlus {
    public partial class PlateSolvePlusDockable : UserControl {
        public PlateSolvePlusDockable(PlateSolvePlusDockableVM vm) {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
