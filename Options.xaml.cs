using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.Plugins.PlateSolvePlus {
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {
        public Options() {
            InitializeComponent();
        }
    }
}
