using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A plugin as PadForge runs it, for the catalog's tests (#600 items 6,
    /// 17 and 18): a host that serves the Automation pipe, the plugins' part
    /// with a launcher that starts nothing, a plugin running, and the
    /// plugin's own session over the real pipe, which declares its device
    /// and its settings as a plugin does.
    /// </summary>
    internal sealed class PluginCatalogRig : IDisposable
    {
        /// <summary>A file name past the 64 characters a program's name keeps,
        /// so the name a device's identity is made from is cut.</summary>
        internal const string LongName = "Pedal Box Bridge for the three pedal USB serial board of Example Studio";

        public readonly string Dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PluginCatalog." + Guid.NewGuid().ToString("N"));
        public readonly string PipeName;
        public readonly AutomationHost Host;
        public readonly PluginManagerTests.FakeLauncher Launcher = new();
        public readonly DashboardViewModel Vm;
        public readonly PluginsController Controller;
        private readonly AutomationHost _ownHost;
        private readonly List<AutomationTestClient> _sessions = new();

        /// <param name="vm">The Extras page's data the plugins' part draws
        /// its cards into: the window's own, for a catalog that reads
        /// them.</param>
        public PluginCatalogRig(AutomationHost host = null, string pipeName = null, DashboardViewModel vm = null)
        {
            Vm = vm ?? new DashboardViewModel();
            Directory.CreateDirectory(Dir);
            if (host == null)
            {
                pipeName = "PadForge.Tests.PluginCatalog.Automation." + Guid.NewGuid().ToString("N");
                _ownHost = TestConsent.AllowAll(new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                    "PadForge.Tests.PluginCatalog." + Guid.NewGuid().ToString("N"), automationPipeName: pipeName));
                _ownHost.Apply(true, AutomationLevel.Commands);
                host = _ownHost;
            }
            Host = host;
            PipeName = pipeName;
            var manager = new PluginManager(Launcher, "pipe", () => 1_000_000, commandFor: p => new PluginCommand(p, p, Dir), timer: false,
                runCheck: work => work());
            Controller = new PluginsController(host, Vm, Dispatcher.CurrentDispatcher, manager);
        }

        /// <summary>Adds a plugin file as the user's confirmation does, and
        /// starts it with the engine.</summary>
        public PluginRun AddRunning(string fileName, string signer = null)
        {
            string path = Path.Combine(Dir, fileName);
            File.WriteAllText(path, fileName);
            var run = Controller.Manager.Add(path, PluginFile.Sha256(path), signer);
            Vm.EnablePlugins = true;
            Controller.Manager.SetEngine(true);
            Assert.Equal(PluginStatus.Running, run.Status);
            return run;
        }

        /// <summary>The plugin's own session: hello with the token its start
        /// was given, from inside the process PadForge started.</summary>
        public AutomationTestClient Connect(string name = "Pedal Box")
        {
            var start = Launcher.Starts[^1];
            start.Process.Children.Add(Environment.ProcessId);
            var client = new AutomationTestClient(PipeName);
            _sessions.Add(client);
            Assert.StartsWith("ok padforge=", client.Final("hello name=\"" + name + "\" kind=plugin token=" + start.Variables[PluginManager.TokenVariable]));
            return client;
        }

        public void Dispose()
        {
            foreach (var session in _sessions) session.Dispose();
            Controller.Dispose();
            _ownHost?.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Plugins and their declarations in the catalog (#600 items 6, 17 and
    /// 18), read through the catalog an assistant reaches: a plugin device's
    /// own settings sit under the device's own ID, the one list_devices
    /// gives, and read and set reach them there. Reading a plugin gives its
    /// state, its reason, its signer and its devices, as its card shows
    /// them, and a plugin's help reaches the program: each declaration's on
    /// its item, a device's with the device, and a help paragraph as a line.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AssistantPluginCatalogTests
    {
        [Fact]
        public void APluginDevicesSettings_SitUnderTheDevicesOwnId_AndAreReadAndSetThere()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                using var rig = new PluginCatalogRig();
                var run = rig.AddRunning(PluginCatalogRig.LongName + ".exe");
                var session = rig.Connect();
                string declared = session.Final("declare key=pedals name=\"Pedal Box\" {\"buttons\":[\"A\"]}");
                Assert.StartsWith("ok id=", declared);
                var entry = rig.Host.Sessions.Devices.Find(declared.Substring(6));
                Assert.NotNull(entry);
                Assert.StartsWith("ok key=clutch", session.Final("setting key=clutch kind=switch name=\"Clutch Inverted\" device=pedals"));
                Assert.StartsWith("ok key=dead", session.Final("setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 unit=% device=pedals default=3"));
                // The device's row on the Devices page, as the engine adds it
                // for the device the session declared.
                p.Vm.Devices.Devices.Add(new DeviceRowViewModel { DeviceName = "Pedal Box", InstanceGuid = entry.Guid });
                string device = "device/" + entry.Id;

                var read = p.Catalog.Read(device);
                var clutch = Assert.Single(read.Children, c => c.Id == device + "/clutch");
                Assert.Equal("plugin_setting", clutch.Kind);
                Assert.Equal("free", clutch.Reach);
                Assert.Contains(read.Children, c => c.Id == device + "/dead");
                // set and press reach it by the same ID, as the user's change.
                var set = p.Catalog.Set(device + "/clutch", JsonSerializer.SerializeToElement(true));
                Assert.Equal(device + "/clutch", set.Id);
                Assert.Equal("true", run.Settings.ValueOf("pedals", "clutch"));
                Assert.Equal(device + "/dead", p.Catalog.Read(device + "/dead").Place.Id);
                // The plugin's own ID no longer carries the device's.
                Assert.Equal("unknown-setting", Assert.Throws<AssistantRefusal>(() =>
                    p.Catalog.Read("plugin/" + PadForge.Services.Catalog.LiveCatalog.ShortId(run.Path) + "/pedals/clutch")).Code);

                // The user removes the device's row: its values go with it,
                // whatever the length of the plugin's name.
                rig.Controller.DeviceRemoved(entry.Guid);
                Assert.DoesNotContain("pedals", run.Settings.Devices(withValues: true));
            });
        }

        /// <summary>A window that shows a page of the test's own, never
        /// activated, as the catalog's tests stand theirs.</summary>
        private static System.Windows.Window WindowFor(System.Windows.FrameworkElement page)
        {
            var window = new System.Windows.Window
            {
                Content = page, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None,
            };
            window.Show();
            return window;
        }

        private static void Until(System.Windows.Window window, Func<bool> done, int ms = 4000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms)
            {
                System.Threading.Thread.Sleep(50);
                PageRenderDump.Settle(window.Dispatcher);
            }
        }

        /// <summary>show rings a plugin by its card on the Plugins page, the
        /// Test Controller's and a plugin's of its own (#600 item 7: every
        /// kind of place rings).</summary>
        [Fact]
        public void ShowAPlugin_RingsItsCard()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                using var rig = new PluginCatalogRig(vm: p.Vm.Dashboard);
                var run = rig.AddRunning("pedals.exe");
                rig.Controller.Refresh();
                var row = Assert.Single(p.Vm.Dashboard.PluginRows);
                var window = WindowFor(p.Extras);
                p.Window = window;
                try
                {
                    p.Extras.Show("Plugins");
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = "plugin/" + PadForge.Services.Catalog.LiveCatalog.ShortId(run.Path), Words = "Yours.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => p.Pointer.Target is PadForge.Controls.FoldCard { DataContext: PluginRowItem });
                    Assert.True(p.Pointer.Target is PadForge.Controls.FoldCard { DataContext: var shown } && ReferenceEquals(shown, row));
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = PluginsController.TestControllerId, Words = "Built in.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => p.Pointer.Target is PadForge.Controls.FoldCard card
                                        && System.Windows.Automation.AutomationProperties.GetAutomationId(card) == "Plugins_TestControllerCard");
                    Assert.Equal("Plugins_TestControllerCard", System.Windows.Automation.AutomationProperties.GetAutomationId(p.Pointer.Target));
                }
                finally
                {
                    p.Pointer.Clear();
                    window.Close();
                }
            });
        }

        /// <summary>A press of the pointer's target, as the user's click on
        /// it.</summary>
        private static void Click(System.Windows.UIElement element)
            => element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
                Source = element,
            });

        private static AssistantShowRequest ShowOf(string id, bool walk) => new()
        {
            Program = "Claude Code",
            Walk = walk,
            Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
        };

        /// <summary>Whether the pointer rings a declaration's own control.</summary>
        private static bool RingsSetting(AutomationAssistantCatalogTests.Pages p, string automationId)
            => p.Pointer.Target is { DataContext: PluginSettingItem item } target
               && System.Windows.Automation.AutomationProperties.GetAutomationId(target) == automationId
               && item.AutomationId == automationId;

        /// <summary>
        /// show rings a plugin's setting by its own control (#600 item 7:
        /// every kind of place rings), in each of its three places: a
        /// plugin's own and the Test Controller's in their cards on the
        /// Plugins page, a device's on the device's panel. A plain show
        /// opens the way itself. A walk rings each click on the way, the
        /// shut card and the device's row among them, and leaves each to
        /// the user.
        /// </summary>
        [Fact]
        public void ShowAPluginsSetting_RingsItsControl_AndAWalkLeavesTheWayToTheUser()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                using var rig = new PluginCatalogRig(vm: p.Vm.Dashboard);
                var run = rig.AddRunning("pedals.exe");
                var session = rig.Connect();
                Assert.StartsWith("ok key=dead", session.Final("setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 unit=% default=3"));
                string declared = session.Final("declare key=pedals name=\"Pedal Box\" {\"buttons\":[\"A\"]}");
                var entry = rig.Host.Sessions.Devices.Find(declared.Substring(6));
                Assert.NotNull(entry);
                Assert.StartsWith("ok key=clutch", session.Final("setting key=clutch kind=switch name=\"Clutch Inverted\" device=pedals"));
                var device = new DeviceRowViewModel { DeviceName = "Pedal Box", InstanceGuid = entry.Guid };
                p.Vm.Devices.Devices.Add(device);
                p.Vm.Devices.RefreshCounts();
                rig.Controller.Refresh();
                var row = Assert.Single(p.Vm.Dashboard.PluginRows);
                string shortId = PadForge.Services.Catalog.LiveCatalog.ShortId(run.Path);
                var tc = rig.Controller.BuiltIns.TestSettings.Declarations()
                    .First(d => d.Device == null && d.When == null && d.Kind is not (PluginSettingKind.Help or PluginSettingKind.Status));
                var kept = PadForge.Common.CardFoldState.Snapshot();

                var window = WindowFor(p.Extras);
                p.Window = window;
                try
                {
                    p.Vm.SelectedNavTag = "Extras";
                    p.Extras.Show("Plugins");
                    PageRenderDump.Settle(window.Dispatcher);
                    foreach (var (id, cardId, control) in new[]
                    {
                        ("plugin/" + shortId + "/dead", row.FoldId, "Plugins_" + shortId + "_dead"),
                        (PluginsController.TestControllerId + "/" + tc.Key, "Plugins_TestControllerCard", "Plugins_TestController_" + tc.Key),
                    })
                    {
                        var card = PadForge.Search.SearchLocator.VisualDescendants(p.Extras).OfType<PadForge.Controls.FoldCard>()
                            .Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == cardId);
                        card.ShowForSearch(false);
                        PageRenderDump.Settle(window.Dispatcher);

                        // A walk rings the shut card, which the user opens.
                        Assert.True(p.Catalog.Show(ShowOf(id, walk: true)).Shown);
                        Until(window, () => ReferenceEquals(p.Pointer.Target, card));
                        Assert.Same(card, p.Pointer.Target);
                        Assert.False(card.IsExpanded);
                        card.ShowForSearch(true);
                        Click(card);
                        Until(window, () => RingsSetting(p, control));
                        Assert.True(RingsSetting(p, control), id + " rings " + (p.Pointer.Target?.GetType().Name ?? "nothing"));
                        p.Pointer.Clear();

                        // A plain show opens the card itself.
                        card.ShowForSearch(false);
                        PageRenderDump.Settle(window.Dispatcher);
                        Assert.True(p.Catalog.Show(ShowOf(id, walk: false)).Shown);
                        Until(window, () => RingsSetting(p, control));
                        Assert.True(RingsSetting(p, control), id + " rings " + (p.Pointer.Target?.GetType().Name ?? "nothing"));
                        Assert.True(card.IsExpanded);
                        p.Pointer.Clear();
                    }
                }
                finally
                {
                    p.Pointer.Clear();
                    window.Close();
                    PadForge.Common.CardFoldState.Load(kept);
                }

                // A device's setting, on the device's panel.
                string clutch = "device/" + entry.Id + "/clutch";
                var devices = WindowFor(p.Devices);
                p.Window = devices;
                try
                {
                    p.Vm.SelectedNavTag = "Devices";
                    PageRenderDump.Settle(devices.Dispatcher);
                    Assert.NotSame(device, p.Vm.Devices.SelectedDevice);
                    // A walk rings the device's row, and the user picks it.
                    var walked = p.Catalog.Show(ShowOf(clutch, walk: true));
                    Assert.True(walked.Shown);
                    for (int i = 0; i < 6 && !RingsSetting(p, "Device_Plugin_pedals_clutch"); i++)
                    {
                        Until(devices, () => p.Pointer.Target != null);
                        var target = p.Pointer.Target;
                        Assert.True(target != null, "step " + (i + 1) + " of " + walked.Steps + " rings nothing: " + p.Pointer.Callout?.Words);
                        if (target is PadForge.Controls.FoldPart fold)
                        {
                            Assert.False(fold.IsExpanded);
                            fold.ShowForSearch(true);
                        }
                        else
                        {
                            Assert.Same(device, target.DataContext);
                            Assert.NotSame(device, p.Vm.Devices.SelectedDevice);
                            p.Vm.Devices.SelectedDevice = device;
                        }
                        Click(target);
                        Until(devices, () => !ReferenceEquals(p.Pointer.Target, target));
                    }
                    Assert.True(RingsSetting(p, "Device_Plugin_pedals_clutch"), "the device's setting rings " + (p.Pointer.Target?.GetType().Name ?? "nothing"));
                    p.Pointer.Clear();

                    // A plain show brings the device's panel up itself.
                    p.Vm.Devices.SelectedDevice = p.Vm.Devices.Devices[0];
                    PageRenderDump.Settle(devices.Dispatcher);
                    Assert.True(p.Catalog.Show(ShowOf(clutch, walk: false)).Shown);
                    Until(devices, () => RingsSetting(p, "Device_Plugin_pedals_clutch"));
                    Assert.True(RingsSetting(p, "Device_Plugin_pedals_clutch"));
                    Assert.Same(device, p.Vm.Devices.SelectedDevice);
                }
                finally
                {
                    p.Pointer.Clear();
                    devices.Close();
                    PadForge.Common.CardFoldState.Load(kept);
                }
            });
        }

        /// <summary>A plain show of a plugin's setting from another page:
        /// the Plugins page has never drawn the setting's card, so the card is
        /// opened once the page lays it out, and the control rings. A
        /// plugin's own card and the Test Controller's, each on pages built
        /// fresh.</summary>
        [Fact]
        public void APlainShowOfAPluginsSetting_FromAnotherPage_OpensItsCard()
        {
            foreach (bool testController in new[] { false, true })
            {
                AutomationAssistantCatalogTests.WithPages(p =>
                {
                    using var rig = new PluginCatalogRig(vm: p.Vm.Dashboard);
                    var run = rig.AddRunning("pedals.exe");
                    var session = rig.Connect();
                    Assert.StartsWith("ok key=dead", session.Final("setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 unit=% default=3"));
                    rig.Controller.Refresh();
                    string shortId = PadForge.Services.Catalog.LiveCatalog.ShortId(run.Path);
                    var tc = rig.Controller.BuiltIns.TestSettings.Declarations()
                        .First(d => d.Device == null && d.When == null && d.Kind is not (PluginSettingKind.Help or PluginSettingKind.Status));
                    var (id, control) = testController
                        ? (PluginsController.TestControllerId + "/" + tc.Key, "Plugins_TestController_" + tc.Key)
                        : ("plugin/" + shortId + "/dead", "Plugins_" + shortId + "_dead");
                    var kept = PadForge.Common.CardFoldState.Snapshot();
                    var window = WindowFor(p.Extras);
                    p.Window = window;
                    try
                    {
                        p.Vm.SelectedNavTag = "Extras";
                        PageRenderDump.Settle(window.Dispatcher);
                        Assert.Null(p.Extras.CurrentExtra);
                        Assert.True(p.Catalog.Show(ShowOf(id, walk: false)).Shown);
                        Until(window, () => RingsSetting(p, control));
                        Assert.True(RingsSetting(p, control), id + " rings " + (p.Pointer.Target?.GetType().Name ?? "nothing"));
                        Assert.Equal("Plugins", p.Extras.CurrentExtra);
                    }
                    finally
                    {
                        p.Pointer.Clear();
                        window.Close();
                        PadForge.Common.CardFoldState.Load(kept);
                    }
                });
            }
        }

        [Fact]
        public void ReadingAPlugin_GivesItsCardsLine_ItsDevices_ItsHelp_AndWhatItDeclared()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                using var rig = new PluginCatalogRig(vm: p.Vm.Dashboard);
                var run = rig.AddRunning("pedals.exe", signer: "Example Studio");
                var session = rig.Connect();
                string declared = session.Final("declare key=pedals name=\"Pedal Box\" {\"buttons\":[\"A\"]}");
                var entry = rig.Host.Sessions.Devices.Find(declared.Substring(6));
                Assert.NotNull(entry);
                Assert.StartsWith("ok key=dead", session.Final("setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 unit=% default=3 help=\"How far a pedal moves before it counts.\""));
                Assert.StartsWith("ok key=curve", session.Final("setting key=curve kind=choice name=\"Brake Curve\" {\"choices\":[{\"value\":\"lin\",\"name\":\"Linear\"},{\"value\":\"prog\",\"name\":\"Progressive\"}],\"default\":\"prog\"}"));
                Assert.StartsWith("ok key=cal", session.Final("setting key=cal kind=button name=Calibrate help=\"Press each pedal fully once.\""));
                Assert.StartsWith("ok key=intro", session.Final("setting key=intro kind=help help=\"Pedal Box reads the board on its serial port.\""));
                Assert.StartsWith("ok key=about", session.Final("setting key=about kind=help device=pedals help=\"Three pedals on one USB serial board.\""));
                p.Vm.Devices.Devices.Add(new DeviceRowViewModel { DeviceName = "Pedal Box", InstanceGuid = entry.Guid });
                rig.Controller.Refresh();
                var row = Assert.Single(p.Vm.Dashboard.PluginRows);
                string id = "plugin/" + PadForge.Services.Catalog.LiveCatalog.ShortId(run.Path);

                // The plugin as its card shows it: its state, the devices it
                // added and its signer on the card's line.
                var plugin = p.Catalog.Read(id);
                Assert.Equal("plugin", plugin.Place.Kind);
                Assert.Equal(row.State, plugin.Place.ValueText);
                Assert.Contains(s.Common_Running, plugin.Place.ValueText);
                Assert.Contains(string.Format(s.Plugins_Row_Added_Format, "Pedal Box"), plugin.Place.ValueText);
                Assert.Contains(string.Format(s.Plugins_Row_Signed_Format, "Example Studio"), plugin.Place.ValueText);
                Assert.Contains(row.State, plugin.Lines);
                // Its devices, by the IDs list_devices gives.
                Assert.Contains(plugin.Children, c => c.Id == "device/" + entry.Id && c.Kind == "device");
                // Its help paragraph, as a line and as its help.
                Assert.Contains("Pedal Box reads the board on its serial port.", plugin.Lines);
                Assert.Equal("Pedal Box reads the board on its serial port.", plugin.Place.Help);
                // find gives the same line.
                Assert.Equal(row.State, p.Catalog.Find(null, "plugin", 50).Places.Single(f => f.Id == id).ValueText);
                // Never its file's place.
                Assert.DoesNotContain(rig.Dir, JsonSerializer.Serialize(plugin), StringComparison.OrdinalIgnoreCase);

                // What it declared, each with its help and its range or its
                // choices.
                var dead = Assert.Single(plugin.Children, c => c.Id == id + "/dead");
                Assert.Equal(("number", (double?)0, (double?)20, "%"), (dead.Takes, dead.Minimum, dead.Maximum, dead.Unit));
                Assert.Equal(3, dead.Value.Value.GetDouble());
                Assert.Equal("How far a pedal moves before it counts.", dead.Help);
                var curve = Assert.Single(plugin.Children, c => c.Id == id + "/curve");
                Assert.Equal("choice", curve.Takes);
                Assert.Equal(new[] { "Linear", "Progressive" }, curve.Choices);
                Assert.Equal("Progressive", curve.ValueText);
                Assert.Equal("Press each pedal fully once.", Assert.Single(plugin.Children, c => c.Id == id + "/cal").Help);
                p.Catalog.Set(id + "/curve", JsonSerializer.SerializeToElement("Linear"));
                Assert.Equal("lin", run.Settings.ValueOf(null, "curve"));
                Assert.Equal("out-of-range", Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(id + "/dead", JsonSerializer.SerializeToElement(70))).Code);

                // The device's help reads with the device.
                var device = p.Catalog.Read("device/" + entry.Id);
                Assert.Equal("Three pedals on one USB serial board.", device.Place.Help);
                Assert.Contains("Three pedals on one USB serial board.", device.Lines);

                // A plugin that stopped says why, as its card does.
                rig.Launcher.Last.Exit(3);
                rig.Controller.Refresh();
                Assert.Equal(PluginStatus.Restarting, run.Status);
                var stopped = p.Catalog.Read(id).Place.ValueText;
                Assert.Equal(row.State, stopped);
                Assert.StartsWith(PluginsController.StateOf(run), stopped);
                Assert.Contains("3", PluginsController.StateOf(run));

                // The Test Controller reads as a plugin too.
                var test = p.Catalog.Read(PluginsController.TestControllerId);
                Assert.Equal("plugin", test.Place.Kind);
                Assert.Equal(p.Vm.Dashboard.TestControllerLine, test.Place.ValueText);
                Assert.Contains(test.Children, c => c.Id.StartsWith(PluginsController.TestControllerId + "/", StringComparison.Ordinal));
            });
        }
    }
}
