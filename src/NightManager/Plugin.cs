using NightManager.Plugin.Options;
using NightManager.Plugin.Runtime;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using System.ComponentModel.Composition;

namespace NightManager.Plugin {
    [Export(typeof(IPluginManifest))]
    public class Plugin : PluginBase {
        private OptionsViewModel options;

        [Import]
        public NightManagerService Service { get; set; }

        /// <summary>Data context of the options page (bound as "Options.*" in XAML).</summary>
        public OptionsViewModel Options => options ??= Service is null ? null : new OptionsViewModel(Service);

        public override Task Initialize() {
            Service.Start();
            return Task.CompletedTask;
        }

        public override Task Teardown() => Service.StopAsync();
    }
}
