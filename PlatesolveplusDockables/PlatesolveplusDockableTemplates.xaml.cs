using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Flashy-Ger.NINA.Platesolveplus.PlatesolveplusDockables {
    [Export(typeof(ResourceDictionary))]
    public partial internal class MyPluginDockableTemplates : ResourceDictionary {
    public MyPluginDockableTemplates() {
        InitializeComponent();
    }
}
}