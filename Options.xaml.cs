using System.ComponentModel.Composition;
using System.Windows;

namespace Flashy_Ger.NINA.Platesolveplus {
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {
        public Options() {
            InitializeComponent();
        }
    }
}
