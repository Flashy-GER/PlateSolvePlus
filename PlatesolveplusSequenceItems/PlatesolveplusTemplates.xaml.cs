using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Flashy_Ger.NINA.Platesolveplus.PlatesolveplusTestCategory {
    [Export(typeof(ResourceDictionary))]
    public partial internal class PluginItemTemplate : ResourceDictionary {
    public PluginItemTemplate() {
        InitializeComponent();
    }
}
}