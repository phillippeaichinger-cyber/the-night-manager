using System.ComponentModel.Composition;
using System.Windows;

namespace NightManager.Plugin.Options {
    [Export(typeof(ResourceDictionary))]
    public partial class Resources : ResourceDictionary {
        public Resources() {
            InitializeComponent();
            // NINA looks up the options page by "<plugin name>_Options"; derive it from assembly metadata.
            this[new Plugin().Name + "_Options"] = this["OptionsTemplate"];
            Remove("OptionsTemplate");
        }
    }
}
