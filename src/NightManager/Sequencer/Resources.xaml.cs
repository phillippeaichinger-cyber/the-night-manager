using System.ComponentModel.Composition;
using System.Windows;

namespace NightManager.Plugin.Sequencer {
    [Export(typeof(ResourceDictionary))]
    public partial class Resources : ResourceDictionary {
        public Resources() {
            InitializeComponent();
        }
    }
}
