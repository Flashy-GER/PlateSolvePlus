using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables {
    [Export(typeof(ResourceDictionary))]
    public partial class PlatesolveplusDockableTemplates : ResourceDictionary {
        public PlatesolveplusDockableTemplates() {
            InitializeComponent();
        }
    }
}
