using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.Services.Plugins;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// A plugin's declared settings and controls (#600 item 17), its card's
    /// lifecycle (item 18) and the plugin kit (item 20): every kind read from
    /// a <c>setting</c> line, the limits, a change reaching the plugin before
    /// anything after it, the values kept in PadForge.xml and dropped with a
    /// device, the catalog's items, the file held until the hello and a
    /// change after it, a silent plugin's devices offline at 5 s and the
    /// plugin ended at 60 s unless a debugger is attached, and the kit's
    /// guide, build settings, build script and command line.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PluginSettingsTests
    {
        private readonly ITestOutputHelper _output;

        public PluginSettingsTests(ITestOutputHelper output) => _output = output;

        private static string Answer(PluginSettingSet set, string line) => PluginLines.Answer(set, AutomationLine.Parse(line));

        // ── Declarations ──

        [Fact]
        public void EveryKind_IsReadFromItsLine_WithWordsOnTheLineOrInTheObject()
        {
            var set = new PluginSettingSet();
            Assert.Equal("ok key=on value=true", Answer(set, "setting key=on kind=switch name=\"Clutch Inverted\" default=1"));
            Assert.Equal("ok key=dead value=3", Answer(set, "setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 step=1 unit=% group=Pedals {\"default\":3}"));
            Assert.Equal("ok key=curve value=prog", Answer(set, "setting key=curve kind=choice name=\"Brake Curve\" {\"choices\":[{\"value\":\"lin\",\"name\":\"Linear\"},{\"value\":\"prog\",\"name\":\"Progressive\"}],\"default\":\"prog\"}"));
            Assert.Equal("ok key=label value=\"\"", Answer(set, "setting key=label kind=text name=Label"));
            Assert.Equal("ok key=port value=\"\"", Answer(set, "setting key=port kind=status name=Port"));
            Assert.Equal("ok key=cal", Answer(set, "setting key=cal kind=button name=Calibrate help=\"Press each pedal fully once.\""));
            Assert.Equal("ok key=steer value=0,0", Answer(set, "setting key=steer kind=pad name=\"Steer by Hand\" stay=1"));
            Assert.Equal("ok key=pick", Answer(set, "setting key=pick kind=input name=\"Shift Button\"").Replace(" value=\"\"", string.Empty));
            Assert.Equal("ok key=about device=pedals", Answer(set, "setting key=about kind=help device=pedals help=\"Three pedals on one board.\""));

            var d = set.Declarations().ToDictionary(x => x.Key);
            Assert.Equal(9, d.Count);
            Assert.Equal(Enum.GetValues<PluginSettingKind>().OrderBy(k => k), d.Values.Select(x => x.Kind).OrderBy(k => k));
            Assert.Equal((0.0, 20.0, 1.0, "%", "Pedals"), (d["dead"].Min, d["dead"].Max, d["dead"].Step, d["dead"].Unit, d["dead"].Group));
            Assert.Equal(new[] { "Linear", "Progressive" }, d["curve"].Choices.Select(c => c.Words));
            Assert.True(d["steer"].Stay);
            Assert.Equal("pedals", d["about"].Device);
            Assert.Equal("Press each pedal fully once.", d["cal"].Help);
            // The kinds' words are the README's.
            Assert.Equal(Enum.GetValues<PluginSettingKind>().Select(PluginDeclarations.Word).OrderBy(w => w),
                PluginDeclarations.Kinds.Keys.OrderBy(w => w));
        }

        [Fact]
        public void ABadDeclaration_IsRefused_AndTheCardSaysWhy()
        {
            var set = new PluginSettingSet();
            Assert.StartsWith("err bad-argument key is", Answer(set, "setting key=\"a b\" kind=switch"));
            Assert.StartsWith("err bad-argument kind is", Answer(set, "setting key=a kind=slider"));
            Assert.StartsWith("err bad-argument a number's min and max", Answer(set, "setting key=a kind=number min=5 max=5"));
            Assert.StartsWith("err bad-argument the choice A is given twice", Answer(set, "setting key=a kind=choice {\"choices\":[\"A\",\"A\"]}"));
            Assert.StartsWith("err bad-argument default: Port takes a number from 1 to 65535", Answer(set, "setting key=port kind=number name=Port min=1 max=65535 default=0"));
            Assert.Equal("default: Port takes a number from 1 to 65535", set.Refused);
            Assert.Empty(set.Declarations());
            // The card says why: the line the refusal set.
            var run = new PluginRun(new PluginData { Path = "p.exe", Sha256 = "00" });
            Assert.StartsWith("err", Answer(run.Settings, "setting key=a kind=slider"));
            Assert.Equal(string.Format(Strings.Instance.Plugins_Missing_Refused_Format, run.Settings.Refused), PluginsController.MissingLine(run));
            // A good declaration clears it.
            Assert.StartsWith("ok", Answer(run.Settings, "setting key=a kind=switch"));
            Assert.Null(run.Settings.Refused);
        }

        [Fact]
        public void TheLimits_Hold_64Settings_1KBAValue_16KBAPlugin()
        {
            var set = new PluginSettingSet();
            for (int i = 0; i < PluginDeclarations.MostSettings; i++)
                Assert.StartsWith("ok", Answer(set, "setting key=t" + i + " kind=text"));
            Assert.StartsWith("err too-many", Answer(set, "setting key=more kind=text"));
            // The same key again replaces, and does not count twice.
            Assert.StartsWith("ok", Answer(set, "setting key=t0 kind=text name=Again"));

            Assert.Equal("a value is up to 1 KB", set.Set(null, "t0", new string('x', 1025)));
            Assert.Null(set.Set(null, "t0", new string('x', 1024)));
            for (int i = 1; i < 16; i++) Assert.Null(set.Set(null, "t" + i, new string('y', 1024)));
            Assert.Equal("a plugin keeps up to 16 KB of values", set.Set(null, "t16", "z"));
            Assert.StartsWith("err too-long", Answer(set, "setting key=t16 value=z"));
        }

        [Fact]
        public void APluginsWords_AreCleanedAndCapped()
        {
            var set = new PluginSettingSet();
            Assert.StartsWith("ok", Answer(set, "setting key=a kind=switch name=\"" + new string('N', 100) + "\" help=\"line\u0007bell\u202Eturn " + new string('h', 400) + "\" unit=x"));
            var d = set.Declarations().Single();
            Assert.Equal(64, d.Name.Length);
            Assert.DoesNotContain('\u0007', d.Help);
            Assert.DoesNotContain('\u202E', d.Help);
            Assert.True(d.Help.Length <= PluginDeclarations.LongestHelp);
            // A setting with no name shows its key.
            Assert.StartsWith("ok", Answer(set, "setting key=speed kind=number"));
            Assert.Equal("speed", set.Find(null, "speed").Name);
        }

        // ── Values ──

        [Fact]
        public void AChange_ReachesThePluginAtOnce_AndAResetSendsTheDefault()
        {
            var set = new PluginSettingSet();
            var sent = new List<string>();
            set.Send = sent.Add;
            Answer(set, "setting key=dead kind=number min=0 max=20 step=0.5 default=3");
            Answer(set, "setting key=fine kind=number min=0 max=1 step=0.1");
            Answer(set, "setting key=cal kind=button");
            Answer(set, "setting key=clutch kind=switch device=pedals");
            Answer(set, "setting key=port kind=status");
            Answer(set, "setting key=curve kind=choice {\"choices\":[\"Linear\",\"Progressive\"]}");

            Assert.Null(set.Set(null, "dead", "4.3"));
            Assert.Equal("e setting key=dead value=4.5", sent.Last());
            Assert.Null(set.Set(null, "fine", "0.30000000000000004"));
            Assert.Equal("0.3", set.ValueOf(null, "fine"));
            Assert.Equal("dead takes a number from 0 to 20", set.Set(null, "dead", "21"));
            Assert.Null(set.Set("pedals", "clutch", "on"));
            Assert.Equal("e setting key=clutch value=true device=pedals", sent.Last());
            Assert.Null(set.Press(null, "cal"));
            Assert.Equal("e press key=cal", sent.Last());
            set.AskChoices(null, "curve");
            Assert.Equal("e choices key=curve", sent.Last());
            Assert.Equal("port is read only", set.Set(null, "port", "COM4"));

            // The plugin's own lines: a status, and a list kept current. They
            // are not sent back.
            int count = sent.Count;
            Assert.Equal("ok", Answer(set, "setting key=port value=COM4"));
            Assert.Equal("COM4", set.ValueOf(null, "port"));
            Assert.Equal("ok", Answer(set, "setting key=curve {\"choices\":[\"Linear\",\"Progressive\",\"Race\"]}"));
            Assert.Equal(3, set.Find(null, "curve").Choices.Count);
            Assert.Equal(count, sent.Count);

            // A reset sends the default. Reset All of the plugin leaves the
            // device's, and the device's leaves the plugin's.
            set.Reset(null, "dead");
            Assert.Equal("e setting key=dead value=3", sent.Last());
            set.Set(null, "dead", "10");
            set.ResetAll("pedals");
            Assert.Equal("false", set.ValueOf("pedals", "clutch"));
            Assert.Equal("10", set.ValueOf(null, "dead"));
            set.ResetAll(null);
            Assert.Equal("3", set.ValueOf(null, "dead"));
        }

        [Fact]
        public void Values_AreKeptOnlyWhenTheyDiffer_AndADevicesGoWithIt()
        {
            var set = new PluginSettingSet();
            Answer(set, "setting key=dead kind=number min=0 max=20 default=3");
            Answer(set, "setting key=clutch kind=switch device=pedals");
            Answer(set, "setting key=wheel kind=switch device=wheel");
            Assert.Null(set.Save());
            set.Set(null, "dead", "3");
            Assert.Null(set.Save());
            set.Set(null, "dead", "5");
            set.Set("pedals", "clutch", "true");
            set.Set("wheel", "wheel", "true");
            // A device the plugin says is gone for good takes its values.
            Assert.Equal("ok", Answer(set, "setting clear device=wheel"));
            Assert.Null(set.Find("wheel", "wheel"));
            var saved = set.Save();
            Assert.Equal(new[] { (string)null + "|dead|5", "pedals|clutch|true" }, saved.Select(v => v.Device + "|" + v.Key + "|" + v.Value));

            // The next run starts with them, before it declares again, and
            // its declaration's answer carries them.
            var next = new PluginSettingSet();
            next.Load(saved);
            Assert.Equal("ok key=dead value=5", Answer(next, "setting key=dead kind=number min=0 max=20 default=3"));
            // A kept value the new declaration does not take gives way.
            Assert.Equal("ok key=dead value=0", Answer(next, "setting key=dead kind=number min=0 max=4 default=0"));
            // A run's end takes its declarations and keeps the values.
            next.EndRun();
            Assert.Empty(next.Declarations());
            Assert.Equal("true", next.ValueOf("pedals", "clutch"));
        }

        [Fact]
        public void AKeptValueADeclarationDrops_LeavesPadForgeXml()
        {
            // The values a file holds: Dead Zone 3, and a port its next
            // build no longer takes.
            var set = new PluginSettingSet();
            set.Load(new[]
            {
                new PluginValueData { Key = "dead", Value = "3" },
                new PluginValueData { Key = "port", Value = "70000" },
            });
            int kept = 0;
            set.KeptChanged += () => kept++;
            // The plugin's next build makes 3 Dead Zone's default: the kept 3
            // is no longer kept, and PadForge.xml hears of it.
            Answer(set, "setting key=dead kind=number min=0 max=20 default=3");
            Assert.Equal(1, kept);
            Assert.Equal(new[] { "port" }, set.Save().Select(v => v.Key));
            // A value the declaration does not take goes too.
            Answer(set, "setting key=port kind=number min=1 max=65535 default=4000");
            Assert.Equal(2, kept);
            Assert.Null(set.Save());
            // Declared again with nothing to drop, nothing is heard.
            Answer(set, "setting key=dead kind=number min=0 max=20 default=3");
            Assert.Equal(2, kept);
        }

        [Fact]
        public void ADevicesValues_OutliveItsRunAndItsForgottenRow_AndGoWhenTheUserRemovesIt()
        {
            using var rig = new ControllerRig();
            var run = rig.AddRunning("pedals.exe");
            Answer(run.Settings, "setting key=clutch kind=switch device=pedals");
            Assert.Null(run.Settings.Set("pedals", "clutch", "true"));
            // The run ends, and the device's row is let go of with it: the
            // value is kept and saved, and the next run starts with it.
            rig.Launcher.Last.Exit(0);
            Assert.Empty(run.Settings.Declarations());
            rig.Controller.Manager.Add(rig.File("other.exe"), PluginFile.Sha256(rig.File("other.exe")));
            ControllerRig.Pump();
            var saved = rig.Vm.PluginRecords.First(p => p.Identity == run.Identity).Values;
            Assert.Equal("pedals|clutch|true", Assert.Single(saved).Device + "|clutch|" + saved[0].Value);
            var next = new PluginSettingSet();
            next.Load(saved);
            Assert.Equal("ok key=clutch value=true device=pedals", Answer(next, "setting key=clutch kind=switch device=pedals"));
            // The user removes the device's row on the Devices page: its
            // values go.
            rig.Controller.DeviceRemoved(ProgramDeviceDeclaration.InstanceGuid("plugin", run.Identity, "pedals"));
            Assert.Null(run.Settings.Save());
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Plugins.cs");
            Assert.Contains("_viewModel.Devices.RemoveDeviceRequested += OnPluginDeviceRemoved;", window);
        }

        // ── The card and the catalog ──

        [Fact]
        public void TheHost_DrawsGroupsAndCompactRows_AndFollowsTheSwitchItsRowsAreShownWith()
        {
            var set = new PluginSettingSet();
            Answer(set, "setting key=hand kind=switch name=\"By Hand\"");
            Answer(set, "setting key=a kind=number group=Pedals");
            Answer(set, "setting key=b kind=pad look=compact when=hand");
            Answer(set, "setting key=c kind=switch look=compact when=hand");
            Answer(set, "setting key=d kind=help device=pedals help=x");
            var host = new PluginSettingsHost(set, null, "Test", Dispatcher.CurrentDispatcher);
            // Each group in the order its first declaration came: the
            // unnamed one first, then Pedals.
            Assert.Collection(host.Rows,
                r => Assert.Equal("hand", ((PluginSettingItem)r).Key),
                r =>
                {
                    var row = (PluginCompactRow)r;
                    Assert.Equal("b", Assert.Single(row.Pads).Key);
                    Assert.Equal("c", Assert.Single(row.Toggles).Key);
                    Assert.False(row.IsShown);
                },
                r => Assert.Equal("Pedals", ((PluginGroupHeading)r).Title),
                r => Assert.Equal("a", ((PluginSettingItem)r).Key));
            host.Item("hand").IsOn = true;
            Assert.True(((PluginCompactRow)host.Rows[1]).IsShown);
            // A pad set by the mouse is one change of both axes.
            var sent = new List<string>();
            set.Send = sent.Add;
            host.Item("b").Pad = new System.Windows.Point(50, -25);
            Assert.Equal("e setting key=b value=50,-25", Assert.Single(sent));
            Assert.Equal("Test_b", host.Item("b").AutomationId);
            // A NaN or an infinity from a box sends nothing and keeps the
            // value: the number's and each axis of the pad's.
            sent.Clear();
            host.Item("a").Number = 40;
            Assert.Equal("e setting key=a value=40", Assert.Single(sent));
            sent.Clear();
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                host.Item("a").Number = bad;
                host.Item("b").SetPad(bad, 10);
                host.Item("b").SetPad(10, bad);
            }
            Assert.Empty(sent);
            Assert.Equal(40, host.Item("a").Number);
            Assert.Equal(new System.Windows.Point(50, -25), host.Item("b").Pad);
            // The device's own host shows its help.
            var device = new PluginSettingsHost(set, "pedals", "Device", Dispatcher.CurrentDispatcher);
            Assert.Equal("help", ((PluginSettingItem)Assert.Single(device.Rows)).KindWord);
        }

        [Fact]
        public void APluginsDeclarations_AreCatalogItems_ThatAProgramSetsAndPresses()
        {
            using var rig = new ControllerRig();
            var run = rig.AddRunning("pedals.exe");
            var set = run.Settings;
            var pressed = new List<string>();
            set.Send = pressed.Add;
            Answer(set, "setting key=dead kind=number min=0 max=20 default=3");
            Answer(set, "setting key=cal kind=button name=Calibrate");
            Answer(set, "setting key=clutch kind=switch device=pedals");
            Answer(set, "setting key=about kind=help help=words");
            var items = LiveCatalog.Items();
            string id = "plugin/" + LiveCatalog.ShortId(run.Path);
            var dead = items.Single(i => i.Id == id + "/dead");
            Assert.Equal(LiveKind.PluginDeclaration, dead.Kind);
            Assert.Equal(ReachClass.Free, dead.Reach);
            dead.Write("7");
            Assert.Equal("7", dead.Read());
            Assert.Throws<ArgumentException>(() => dead.Write("70"));
            items.Single(i => i.Id == id + "/cal").Press();
            Assert.Equal("e press key=cal", pressed.Last());
            Assert.Contains(items, i => i.Id == id + "/pedals/clutch");
            Assert.DoesNotContain(items, i => i.Id == id + "/about");
            // The Test Controller's too.
            Assert.Contains(items, i => i.Id == PluginsController.TestControllerId + "/" + TestControllerPattern.KeyHand && i.Reach == ReachClass.Free);
            // The plugin's own entry stays the catalog's object row.
            Assert.Contains(LiveCatalog.Entries(null), e => e.Id == id + "/dead");
            rig.Dispose();
            Assert.DoesNotContain(LiveCatalog.Items(), i => i.Id == id + "/dead");
        }

        [Fact]
        public void TheCard_NamesItsState_ItsDevices_ItsSigner_AndWhyItsSettingsAreMissing()
        {
            using var rig = new ControllerRig();
            var s = Strings.Instance;
            var run = rig.Controller.Manager.Add(rig.File("pedals.exe"), PluginFile.Sha256(rig.File("pedals.exe")), "Example Studio");
            Assert.Equal(s.Plugins_Missing_NotRunning, PluginsController.MissingLine(run));
            rig.Vm.EnablePlugins = true;
            rig.Controller.Manager.SetEngine(true);
            ControllerRig.Pump();
            var grant = rig.Controller.Manager.AcceptToken(rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable], rig.Launcher.Last.Id);
            Assert.NotNull(grant);
            Assert.Equal(s.Plugins_Missing_None, PluginsController.MissingLine(run));
            Answer(run.Settings, "setting key=dead kind=number");
            Assert.Null(PluginsController.MissingLine(run));
            rig.Controller.Refresh();
            var row = rig.Vm.PluginRows.Single();
            Assert.Equal(s.Common_Running + ". " + string.Format(s.Plugins_Row_Signed_Format, "Example Studio"), row.State);
            Assert.Contains(row.Settings.Rows, r => r is PluginSettingItem { Key: "dead" });

            // A plugin that stopped is named on the status line, which Home's
            // Plugins tile shows.
            for (int i = 0; i < PluginManager.MostFailures; i++)
            {
                rig.Launcher.Last.Exit(1);
                rig.Now += 31_000;
                rig.Controller.Manager.Tick();
            }
            rig.Controller.Refresh();
            Assert.EndsWith(string.Format(s.Plugins_Problem_Failed_Format, "pedals.exe"), rig.Vm.PluginsStatus);
            string home = ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml");
            Assert.Contains("Status=\"{Binding PluginsStatus}\"", home.Substring(home.IndexOf("Key=\"Plugins\"", StringComparison.Ordinal)));
            // Saved with its signer.
            Assert.Equal("Example Studio", rig.Vm.PluginRecords.Single().Signer);
        }

        // ── Lifecycle ──

        [Fact]
        public void TheFile_IsHeldFromItsHashUntilTheHello_AndAChangeAfter_ShowsApproveAndRestart()
        {
            using var rig = new ControllerRig();
            string path = rig.File("bridge.exe");
            var run = rig.AddRunning("bridge.exe");
            // Held: nothing can write it before the hello.
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "changed"));
            rig.Controller.Manager.AcceptToken(rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable], rig.Launcher.Last.Id);
            // Free after it.
            File.WriteAllText(path, "rebuilt");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            rig.Controller.Manager.CheckFiles();
            Assert.True(run.FileChanged);
            Assert.Equal(PluginStatus.Running, run.Status);
            rig.Controller.Refresh();
            var row = rig.Vm.PluginRows.Single();
            Assert.True(row.ShowApprove);
            Assert.Contains(Strings.Instance.Plugins_State_Changed, row.State);
            Assert.Contains(rig.Controller.Manager.LogOf(run), l => l.Kind == PluginLogKind.ChangedWhileRunning);

            // Approve and Restart: the run of the old bytes ends and the new
            // ones start.
            var first = rig.Launcher.Last;
            rig.Controller.Manager.Approve(run, PluginFile.Sha256(path), null);
            Assert.True(first.Stopped);
            Assert.NotSame(first, rig.Launcher.Last);
            Assert.False(run.FileChanged);
            Assert.Equal(PluginStatus.Running, run.Status);
            Assert.Equal(PluginFile.Sha256(path), run.Sha256);
        }

        [Fact]
        public void ASilentPlugin_IsEnded_UnlessADebuggerIsAttached()
        {
            using var rig = new ControllerRig();
            var run = rig.AddRunning("bridge.exe");
            rig.Controller.Manager.AcceptToken(rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable], rig.Launcher.Last.Id);
            var process = rig.Launcher.Last;
            process.Debugged = true;
            Assert.False(rig.Controller.Manager.EndSilent(run));
            Assert.False(process.Stopped);
            process.Debugged = false;
            Assert.True(rig.Controller.Manager.EndSilent(run));
            Assert.True(process.Stopped);
            // Its end is a failure: the back-off starts it again.
            Assert.Equal(PluginStatus.Restarting, run.Status);
            Assert.Equal(1, run.Failures);
            Assert.Contains(rig.Controller.Manager.LogOf(run), l => l.Kind == PluginLogKind.Silent);
            Assert.Equal(Strings.Instance.Plugins_Log_Silent, PluginsController.LogText(rig.Controller.Manager.LogOf(run).Last(l => l.Kind == PluginLogKind.Silent))[^Strings.Instance.Plugins_Log_Silent.Length..]);
        }

        [Fact]
        public async Task OnThePipe_APluginDeclaresAndHears_ItsDevicesGoOfflineAt5s_AndItIsEndedAt60s()
        {
            using var pipe = new AutomationPipeRig(AutomationLevel.Commands);
            long offset = 0;
            using var rig = new ControllerRig(pipe.Host, () => Environment.TickCount64 + offset);
            rig.Vm.EnablePlugins = true;
            var run = rig.AddRunning("pedals.exe");
            for (int id = 100001; id < 100200; id++) rig.Launcher.Last.Children.Add(id);
            rig.Launcher.Last.Children.Add(Environment.ProcessId);
            using var plugin = pipe.Connect();
            Assert.StartsWith("ok padforge=", plugin.Final("hello name=\"Pedal Box\" kind=plugin token=" + rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable]));
            string declared = plugin.Final("declare key=pedals name=\"Pedal Box Pedals\" {\"axes\":[\"Brake\"]}");
            string device = Regex.Match(declared, @"id=(\S+)").Groups[1].Value;
            Assert.Equal("ok key=dead value=3", plugin.Final("setting key=dead kind=number min=0 max=20 default=3"));
            Assert.StartsWith("ok", plugin.Final("setting key=clutch kind=switch device=pedals"));

            // The user's change reaches it as an event.
            Assert.Null(run.Settings.Set(null, "dead", "9"));
            Assert.Equal("e setting key=dead value=9", plugin.Event());
            // A script's setting line is a verb it does not know.
            using var script = pipe.Open();
            Assert.StartsWith("err unknown-command", script.Final("setting key=x kind=switch"));

            // The device panel's card for its device.
            var card = rig.Controller.CardFor(ProgramDeviceDeclaration.InstanceGuid("plugin", run.Identity, "pedals"));
            Assert.Equal(string.Format(Strings.Instance.Plugins_Device_AddedBy_Format, "Pedal Box"), card.AddedBy);
            Assert.Equal("clutch", ((PluginSettingItem)Assert.Single(card.Settings.Rows)).Key);
            Assert.Equal(new[] { "Pedal Box Pedals" }, rig.Controller.DeviceNames(run));

            // Silent: at 5 s its device goes offline at rest, at 60 s it is
            // ended.
            offset = PluginsController.OfflineAfterSilenceMs + 500;
            rig.Controller.CheckSilence();
            Assert.Null(pipe.Host.Sessions.Devices.Find(device)?.Owner);
            Assert.False(rig.Launcher.Last.Stopped);
            offset = PluginsController.EndAfterSilenceMs + 500;
            rig.Controller.CheckSilence();
            Assert.True(rig.Launcher.Starts[0].Process.Stopped);
            await AutomationPipeRig.Until(() => run.Status == PluginStatus.Restarting);
        }

        [Fact]
        public async Task ASilentPluginWithDevicesAlone_IsEndedAt60s()
        {
            using var pipe = new AutomationPipeRig(AutomationLevel.Commands);
            long offset = 0;
            using var rig = new ControllerRig(pipe.Host, () => Environment.TickCount64 + offset);
            rig.Vm.EnablePlugins = true;
            var run = rig.AddRunning("wheel.exe");
            for (int id = 100001; id < 100200; id++) rig.Launcher.Last.Children.Add(id);
            rig.Launcher.Last.Children.Add(Environment.ProcessId);
            using var plugin = pipe.Connect();
            Assert.StartsWith("ok padforge=", plugin.Final("hello name=Wheel kind=plugin token=" + rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable]));
            string device = Regex.Match(plugin.Final("declare key=wheel name=Wheel {\"axes\":[\"Steer\"]}"), @"id=(\S+)").Groups[1].Value;
            Assert.False(run.Settings.HasDeclarations);
            offset = PluginsController.OfflineAfterSilenceMs + 500;
            rig.Controller.CheckSilence();
            Assert.Null(pipe.Host.Sessions.Devices.Find(device)?.Owner);
            // Its devices are offline and it declared no setting: it is
            // still watched, and ended at 60 s.
            offset = PluginsController.EndAfterSilenceMs + 500;
            rig.Controller.CheckSilence();
            Assert.True(rig.Launcher.Starts[0].Process.Stopped);
            await AutomationPipeRig.Until(() => run.Status == PluginStatus.Restarting);
        }

        [Fact]
        public void AdaptiveTriggerEffects_ReachAPlugin_AsTrigfx()
        {
            using var pipe = new AutomationPipeRig(AutomationLevel.SendInputs);
            pipe.Host.AcceptPluginToken = (token, pid) => token == "k" ? new AutomationPluginGrant("Pad Maker") : null;
            pipe.Host.ApplyPlugins(true);
            using var plugin = pipe.Connect();
            Assert.StartsWith("ok padforge=", plugin.Final("hello name=m kind=plugin token=k"));
            Assert.StartsWith("ok", plugin.Final("declare key=fx name=Fx {\"buttons\":[\"A\"],\"feedback\":[\"trigfx\"]}"));
            var entry = pipe.Host.Sessions.Devices.All().Single(e => e.Key == "fx");
            Assert.True(entry.Device.Shape.TriggerEffects);
            Assert.True(entry.Device.HasRumble);
            var left = new byte[] { 0x21, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var right = new byte[] { 0x26, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
            Assert.True(entry.Device.SetTriggerEffects(left, right));
            Assert.Equal("e trigfx " + entry.Id + " 21010203040506070809" + "0a 26090807060504030201" + "00", plugin.Event());
            // The same pair again is no new event. A device that did not
            // declare them takes none.
            entry.Device.SetTriggerEffects(left, right);
            Assert.Empty(plugin.Drain());
            Assert.StartsWith("ok", plugin.Final("declare key=plain name=Plain {\"buttons\":[\"A\"]}"));
            Assert.False(pipe.Host.Sessions.Devices.All().Single(e => e.Key == "plain").Device.SetTriggerEffects(left, right));
            // A program that declares the device again hears the last ones.
            Assert.Contains(ProgramDevices.TriggerEffectsLine(entry.Id, left, right), ProgramDevices.Replay(entry));
            // The poll pass hands a program's device the slot's effects.
            string pass = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            Assert.Contains("programFx.SetTriggerEffects(atLeftBlock, atRightBlock);", pass);
        }

        [Fact]
        public void APlugin_MayAdd16Devices_AScript8()
        {
            using var pipe = new AutomationPipeRig(AutomationLevel.SendInputs);
            pipe.Host.AcceptPluginToken = (token, pid) => token == "k" ? new AutomationPluginGrant("Many") : null;
            pipe.Host.ApplyPlugins(true);
            using var plugin = pipe.Connect();
            Assert.StartsWith("ok padforge=", plugin.Final("hello name=m kind=plugin token=k"));
            for (int i = 0; i < ProgramDevices.MostPerPlugin; i++)
                Assert.StartsWith("ok", plugin.Final("declare key=d" + i + " name=D {\"buttons\":[\"A\"]}"));
            Assert.StartsWith("err too-many", plugin.Final("declare key=d16 name=D {\"buttons\":[\"A\"]}"));
            using var script = pipe.Open();
            for (int i = 0; i < ProgramDevices.MostPerSession; i++)
                Assert.StartsWith("ok", script.Final("declare key=s" + i + " name=S {\"buttons\":[\"A\"]}"));
            Assert.StartsWith("err too-many", script.Final("declare key=s8 name=S {\"buttons\":[\"A\"]}"));
        }

        [Fact]
        public void ADroppedFile_IsTakenAlone_AndGoesThroughTheSameConfirmation()
        {
            string file = Path.GetTempFileName();
            try
            {
                var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { file });
                Assert.Equal(file, ExtrasPage.DroppedPluginFile(data));
                Assert.Null(ExtrasPage.DroppedPluginFile(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { file, file })));
                Assert.Null(ExtrasPage.DroppedPluginFile(new System.Windows.DataObject(System.Windows.DataFormats.Text, "x")));
                string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
                Assert.Contains("AddPluginFile(path);", code);
                Assert.Contains("AddPluginFile(dialog.FileName);", code);
                string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml");
                Assert.Contains("Drop=\"PluginsPage_Drop\"", page);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void TheInputPick_TakesTheFirstControlPressed_AndNothingAfterItsTime()
        {
            var before = new CustomInputState();
            var after = before.Clone();
            Assert.Null(PluginInputPick.Changed(before, after));
            after.Buttons[3] = true;
            Assert.Equal("Button 3", PluginInputPick.Changed(before, after));
            after = before.Clone();
            after.Axis[1] = 60000;
            Assert.Equal("Axis 1", PluginInputPick.Changed(before, after));
            after = before.Clone();
            after.Povs[0] = 9000;
            before.Povs[0] = -1;
            Assert.Equal("POV 0", PluginInputPick.Changed(before, after));
        }

        // ── The kit ──

        [Fact]
        public void TheKitsGuide_ListsEveryVerb_Event_DeclarationWord_AndKind()
        {
            string readme = Encoding.UTF8.GetString(PluginKit.Read("README.md"));
            string sessions = ExtrasPageTests.RepoText("PadForge.App", "Services", "Automation", "AutomationSessions.cs");
            string run = sessions.Substring(sessions.IndexOf("internal async Task RunAsync(", StringComparison.Ordinal));
            run = run.Substring(0, run.IndexOf("default:", StringComparison.Ordinal));
            var verbs = Regex.Matches(run, "case \"(\\w+)\":").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(verbs.Count > 20);
            // A verb as a word of its own: `set` is not found inside `setting`.
            foreach (string verb in verbs)
                Assert.True(Regex.IsMatch(readme, "`" + verb + "[ `]"), "the guide lacks " + verb);
            foreach (string e in new[] { "e rumble", "e trigrumble", "e trigfx", "e led", "e player", "e setting", "e press", "e choices", "e lease",
                         "e state", "e b", "e h", "e a", "e x", "e dev", "e profile", "e engine", "e level" })
                Assert.Contains("`" + e, readme);
            foreach (string kind in PluginDeclarations.Kinds.Keys) Assert.Contains("| `" + kind + "` |", readme);
            foreach (string word in new[] { "key", "kind", "name", "help", "group", "device", "default", "min", "max", "step", "unit", "choices", "stay", "when", "look" })
                Assert.Contains("`" + word + "`", readme);
            foreach (string part in new[] { "buttons", "axes", "sliders", "hats", "gyro", "accel", "battery", "touchpads", "feedback" })
                Assert.Contains("`" + part + "`", readme);
            Assert.DoesNotContain("\t", readme);
        }

        [Fact]
        public void PluginKit_WritesTheKitIntoTheFolderNamed_AndSaysHowToBuildIt()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.KitLine." + Guid.NewGuid().ToString("N"), "kit");
            try
            {
                var said = new List<string>();
                var errors = new List<string>();
                Assert.Equal(0, PluginKit.RunFromCommandLine(dir, said.Add, errors.Add));
                Assert.Empty(errors);
                Assert.Contains("build.ps1", said.Single());
                foreach (var (name, _) in PluginKit.Files) Assert.True(File.Exists(Path.Combine(dir, name)), name);
                foreach (var library in AutomationLibraries.All) Assert.True(File.Exists(Path.Combine(dir, library.FileName)));
                string project = File.ReadAllText(Path.Combine(dir, "PadForgePlugin.csproj"));
                Assert.Contains("<Compile Include=\"PadForgePlugin.cs\" />", project);
                Assert.Contains("<Compile Include=\"PipeOwner.cs\" />", project);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", project);
                Assert.Contains("PadForgePlugin.csproj", File.ReadAllText(Path.Combine(dir, "build.ps1")));
                // A file the user had there that is not the kit's is left.
                File.WriteAllText(Path.Combine(dir, "mine.txt"), "mine");
                Assert.Equal(0, PluginKit.RunFromCommandLine(dir, said.Add, errors.Add));
                Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "mine.txt")));
                // A folder that cannot be made names what failed.
                Assert.Equal(1, PluginKit.RunFromCommandLine(Path.Combine(dir, "mine.txt", "under"), said.Add, errors.Add));
                Assert.StartsWith("The plugin kit could not be written to", errors.Single());
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch (IOException) { } }
        }

        [Fact]
        public void TheCommandLine_TakesPluginKit_WithItsFolder_AndRunsItWithoutAnyRunningCopy()
        {
            var r = CommandLineSwitches.Parse(new[] { "--plugin-kit", @"C:\Kits\Mine" });
            Assert.Null(r.Error);
            Assert.Equal(@"C:\Kits\Mine", r.PluginKit);
            Assert.Equal(@"C:\Kits\Mine", CommandLineSwitches.Parse(new[] { "--plugin-kit=C:\\Kits\\Mine" }).PluginKit);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--plugin-kit" }).Error);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--plugin-kit", "--show" }).Error);
            Assert.Contains("--plugin-kit <folder>", CommandLineSwitches.HelpText);

            string asked = null;
            var seams = new ClientRun.Seams
            {
                PluginKit = (folder, say, sayError) => { asked = folder; return 0; },
                CopyRuns = () => throw new InvalidOperationException("the kit needs no running copy"),
            };
            Assert.True(ClientRun.TryRun(new[] { "--plugin-kit", "C:\\k" }, out int code, seams));
            Assert.Equal(0, code);
            Assert.Equal("C:\\k", asked);
            Assert.True(ClientRun.TryRun(new[] { "--plugin-kit" }, out code, new ClientRun.Seams { TellError = _ => { } }));
            Assert.Equal(ClientRun.WrongCommandLine, code);
        }

        /// <summary>The kit's C# plugin is built with the kit's own build
        /// script and settings file and the .NET SDK on this PC, then run as
        /// a plugin against a host on a pipe of the test's own: it declares
        /// its controller and its settings, moves its stick, and hears a
        /// setting the user changes.</summary>
        [Fact]
        public async Task TheKitsCSharpPlugin_BuildsWithItsScript_AndRunsAsAPlugin()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.KitBuild." + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "kit");
            try
            {
                PluginKit.WriteInto(dir);
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                var start = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(dir, "build.ps1") + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // The build sees no repository's settings and no test host's
                // variables that steer MSBuild.
                foreach (string name in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "DOTNET_HOST_PATH" })
                    start.Environment.Remove(name);
                using (var build = Process.Start(start))
                {
                    var output = build.StandardOutput.ReadToEndAsync();
                    var error = build.StandardError.ReadToEndAsync();
                    if (!build.WaitForExit(300_000))
                    {
                        build.Kill(entireProcessTree: true);
                        Assert.Fail("the kit's build did not finish");
                    }
                    string said = await output + await error;
                    _output.WriteLine(said);
                    Assert.True(build.ExitCode == 0, said);
                }
                string exe = Path.Combine(dir, "bin", "PadForgePlugin.exe");
                Assert.True(File.Exists(exe), exe);

                using var pipe = new AutomationPipeRig(AutomationLevel.Commands);
                var set = new PluginSettingSet();
                var logged = new ConcurrentQueue<string>();
                pipe.Host.AcceptPluginToken = (token, pid) =>
                {
                    if (token != "KITKEY") return null;
                    var grant = new AutomationPluginGrant("Kit Plugin", logged.Enqueue);
                    PluginLines.Bind(grant, set);
                    return grant;
                };
                pipe.Host.ApplyPlugins(true);
                var run = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                run.Environment[PluginManager.TokenVariable] = "KITKEY";
                run.Environment[PluginManager.PipeVariable] = pipe.PipeName;
                using var plugin = Process.Start(run);
                try
                {
                    ProgramDevices.Entry entry = null;
                    await AutomationPipeRig.Until(() => (entry = pipe.Host.Sessions.Devices.All().FirstOrDefault(e => e.Key == "kitpad" && e.Owner != null)) != null, 30000);
                    Assert.Equal("Kit Pad", entry.Device.Name);
                    await AutomationPipeRig.Until(() => set.Find(null, "turns") != null, 10000);
                    Assert.Equal(new[] { PluginSettingKind.Number, PluginSettingKind.Switch, PluginSettingKind.Button, PluginSettingKind.Status },
                        set.Declarations().Select(d => d.Kind));
                    int first = entry.Device.PeekState().Axis[0];
                    await AutomationPipeRig.Until(() => entry.Device.PeekState().Axis[0] != first, 10000);
                    Assert.Null(set.Set(null, "speed", "3"));
                    await AutomationPipeRig.Until(() => logged.Contains("speed is 3."), 10000);
                    await AutomationPipeRig.Until(() => set.ValueOf(null, "turns") is { Length: > 0 }, 10000);
                }
                finally
                {
                    if (!plugin.HasExited) plugin.Kill(entireProcessTree: true);
                    plugin.WaitForExit(5000);
                }
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // ── A plugins' part with a manager of the test's own ──

        private sealed class ControllerRig : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PluginSettings." + Guid.NewGuid().ToString("N"));
            private readonly AutomationHost _ownHost;
            public readonly PluginManagerTests.FakeLauncher Launcher = new();
            public readonly DashboardViewModel Vm = new();
            public readonly PluginsController Controller;
            public long Now = 1_000_000;
            private bool _disposed;

            public ControllerRig(AutomationHost host = null, Func<long> nowMs = null)
            {
                Directory.CreateDirectory(Dir);
                if (host == null)
                {
                    _ownHost = new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                        "PadForge.Tests.PluginSettings." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown,
                        automationPipeName: "PadForge.Tests.PluginSettings.Automation." + Guid.NewGuid().ToString("N"));
                    host = _ownHost;
                }
                var manager = new PluginManager(Launcher, "pipe", () => Now, commandFor: p => new PluginCommand(p, p, Dir), timer: false,
                    runCheck: work => work());
                Controller = new PluginsController(host, Vm, Dispatcher.CurrentDispatcher, manager, nowMs);
            }

            public string File(string name)
            {
                string path = Path.Combine(Dir, name);
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, name);
                return path;
            }

            public PluginRun AddRunning(string name)
            {
                string path = File(name);
                var run = Controller.Manager.Add(path, PluginFile.Sha256(path));
                Vm.EnablePlugins = true;
                Controller.Manager.SetEngine(true);
                Pump();
                Assert.Equal(PluginStatus.Running, run.Status);
                return run;
            }

            public static void Pump()
            {
                for (int i = 0; i < 3; i++)
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Controller.Dispose();
                _ownHost?.Dispose();
                try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
