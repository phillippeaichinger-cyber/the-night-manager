using Moq;
using NUnit.Framework;
using NINA.Astrometry.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NightManager.Plugin.Runtime;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Windows;

namespace NightManager.Plugin.Tests {
    [NonParallelizable, Apartment(ApartmentState.STA)]
    public class CompositionTests {
        /// <summary>Exactly the services NINA's PluginLoader.GetContainer provides that this plugin imports.</summary>
        private static CompositionContainer Create() {
            var container = new CompositionContainer(new AssemblyCatalog(typeof(Plugin).Assembly));
            var profile = new Mock<IProfile>();
            profile.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
            profile.SetupGet(p => p.Name).Returns("Test Observatory");
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
            container.ComposeExportedValue(profiles.Object);
            container.ComposeExportedValue(Mock.Of<ICameraMediator>());
            container.ComposeExportedValue(Mock.Of<ITelescopeMediator>());
            container.ComposeExportedValue(Mock.Of<IGuiderMediator>());
            container.ComposeExportedValue(Mock.Of<IFocuserMediator>());
            container.ComposeExportedValue(Mock.Of<IFilterWheelMediator>());
            container.ComposeExportedValue(Mock.Of<IRotatorMediator>());
            container.ComposeExportedValue(Mock.Of<IWeatherDataMediator>());
            container.ComposeExportedValue(Mock.Of<ISafetyMonitorMediator>());
            container.ComposeExportedValue(Mock.Of<IDomeMediator>());
            container.ComposeExportedValue(Mock.Of<IFlatDeviceMediator>());
            container.ComposeExportedValue(Mock.Of<IImageSaveMediator>());
            container.ComposeExportedValue(Mock.Of<ISequenceMediator>());
            container.ComposeExportedValue(Mock.Of<INighttimeCalculator>());
            return container;
        }

        [Test]
        public async Task ManifestComposes_StartsAndStopsCleanly() {
            using var container = Create();
            var manifest = (Plugin)container.GetExportedValue<IPluginManifest>();

            await manifest.Initialize();
            Assert.That(NightManagerRuntime.Current, Is.Not.Null);
            Assert.That(manifest.Options, Is.Not.Null);
            Assert.That(manifest.Service.IsPaired, Is.False);

            await manifest.Teardown();
            Assert.That(NightManagerRuntime.Current, Is.Null);
        }

        [Test]
        public void SequenceItemsComposeInTheirOwnContainer() {
            using var parts = Create();

            var items = parts.GetExportedValues<NINA.Sequencer.SequenceItem.ISequenceItem>().ToList();

            Assert.That(items.Select(i => i.GetType().Name), Is.EquivalentTo(new[] { "NotifyPhoneInstruction", "EndSessionInstruction" }));
        }

        [Test]
        public void ExportedResourceDictionariesAndTemplatesConstruct() {
            // NINA drag/drop behaviors require an Application and a main window.
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var grid = new System.Windows.Controls.Grid();
            Application.Current.MainWindow = new Window { Content = grid };
            NameScope.SetNameScope(Application.Current.MainWindow, new NameScope());
            Application.Current.MainWindow.RegisterName("RootGrid", grid);
            foreach (var name in new[] { "ProfileService", "SVGDictionary", "Brushes", "Converters" }) {
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml", UriKind.Relative)
                });
            }

            var dictionaries = typeof(Plugin).Assembly.GetTypes().Where(t => typeof(ResourceDictionary).IsAssignableFrom(t)).ToList();
            Assert.That(dictionaries, Has.Count.EqualTo(2));

            foreach (var type in dictionaries) {
                var dictionary = (ResourceDictionary)Activator.CreateInstance(type);
                foreach (var template in dictionary.Values.OfType<DataTemplate>()) {
                    Assert.That(template.LoadContent(), Is.Not.Null, type.FullName);
                }
            }

            Assert.That(new Options.Resources().Contains(new Plugin().Name + "_Options"), Is.True, "Options page key must match the plugin name.");
        }
    }
}
