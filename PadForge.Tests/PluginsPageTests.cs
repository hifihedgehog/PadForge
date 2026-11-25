using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Resources.Strings;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// The Plugins page's cards (#597): Built In, Your Plugins and More in
    /// that order between the status card and the note, every setting with
    /// its reset, Search's entries, the names a screen reader says, and the
    /// page kept in step with the plugins by the plugins' part, a flood of
    /// changes drawn a few times.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PluginsPageTests
    {
        private static string Body()
        {
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml");
            int from = page.IndexOf("<StackPanel x:Name=\"PluginsPage\"", StringComparison.Ordinal);
            Assert.True(from > 0);
            return page.Substring(from);
        }

        private static XElement PageElement()
        {
            var doc = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return doc.Descendants().Single(e => (string)e.Attribute(x + "Name") == "PluginsPage");
        }

        [Fact]
        public void TheCards_StandInOrder_BetweenTheStatusCardAndTheNote()
        {
            string body = Body();
            int At(string text)
            {
                int i = body.IndexOf(text, StringComparison.Ordinal);
                Assert.True(i > 0, text + " is not on the page");
                return i;
            }
            int status = At("x:Name=\"PluginsStatusCard\"");
            int cards = At("<StackPanel x:Name=\"PluginsCards\">");
            int builtIn = At("Plugins_Card_BuiltIn, Source");
            int test = At("Plugins_TestController, Source");
            int replay = At("Plugins_Replay, Source");
            int yours = At("Plugins_Card_Yours, Source");
            int add = At("AutomationProperties.AutomationId=\"Plugins_Add\"");
            int more = At("x:Name=\"PluginsMore\"");
            int kit = At("AutomationProperties.AutomationId=\"Plugins_SaveKit\"");
            int log = At("AutomationProperties.AutomationId=\"Plugins_LogPlugin\"");
            Assert.True(status < cards && cards < builtIn && builtIn < test && test < replay && replay < yours
                && yours < add && add < more && more < kit && kit < log);
        }

        [Fact]
        public void EverySetting_HasItsReset_AndTheResetsPutTheDefaultsBack()
        {
            var resets = Regex.Matches(Body().Substring(0, Body().IndexOf("x:Name=\"PluginsMoreList\"", StringComparison.Ordinal)),
                "CommandParameter=\"(\\w+)\"[^>]*AutomationProperties.AutomationId=\"(Reset_[^\"]+)\"").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
            Assert.Equal(new[] { "EnablePlugins", "TestControllerOn", "ReplayOn", "ReplayRepeat" }, resets.Select(r => r.Item1));
            string bindings = ExtrasPageTests.RepoText("PadForge.Tests", "ResetControlBindings.json");
            foreach (var (property, id) in resets) Assert.Contains("\"Id\": \"" + id + "\"", bindings);

            var vm = new DashboardViewModel();
            Assert.False(vm.TestControllerOn);
            Assert.False(vm.ReplayOn);
            Assert.False(vm.ReplayRepeat);
            vm.TestControllerOn = true;
            vm.ReplayOn = true;
            vm.ReplayRepeat = true;
            foreach (var (property, _) in resets) vm.ResetSettingCommand.Execute(property);
            Assert.False(vm.TestControllerOn);
            Assert.False(vm.ReplayOn);
            Assert.False(vm.ReplayRepeat);
        }

        [Fact]
        public void Search_FindsEverySettingAndAction_OfThePluginsPage()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName).Entries.Where(e => e.Page == SearchPage.Extras && e.ExtraKey == "Plugins").ToList();
            foreach (var key in new[] { "Plugins_TestController", "Plugins_Replay", "Plugins_Replay_Repeat" })
                Assert.True(index.Any(e => e.Kind == SearchKind.Setting && e.Label.Key == key), "no setting " + key);
            foreach (var key in new[] { "Plugins_Add", "Plugins_Kit_Save", "Plugins_Replay_Open" })
                Assert.True(index.Any(e => e.Kind == SearchKind.Action && e.Label.Key == key), "no action " + key);
            Assert.True(index.Single(e => e.Kind == SearchKind.Action && e.Label.Key == "Plugins_Kit_Save").UnderMore);
        }

        [Fact]
        public void EveryControl_HasANameAScreenReaderSays()
        {
            var page = PageElement();
            foreach (var e in page.Descendants().Where(e => e.Name.LocalName is "CheckBox" or "ComboBox" or "Button"))
            {
                bool named = e.Attribute("Content") != null || e.Attribute("AutomationProperties.Name") != null
                    || e.Elements().Any(c => c.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal));
                Assert.True(named, e.Name.LocalName + " " + (string)e.Attribute("AutomationProperties.AutomationId") + " has no name");
            }
            // The card's switch is named for its plugin, and the log's list
            // for its row.
            string body = Body();
            Assert.Contains("AutomationProperties.Name=\"{Binding Name, TargetNullValue=''}\"", body);
            Assert.Contains("AutomationProperties.Name=\"{Binding Plugins_Log, Source={x:Static strings:Strings.Instance}}\"", body);
            // A list item says its plugin's name.
            Assert.Equal("Bridge", NewRow("Bridge").ToString());
        }

        private static PluginRowItem NewRow(string name)
        {
            var row = new PluginRowItem(new object(), 1, null);
            row.Show(name, @"C:\p.exe", "Running", true, false, false);
            return row;
        }

        // ── The page kept in step ──

        private sealed class Rig : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PluginsPage." + Guid.NewGuid().ToString("N"));
            public readonly AutomationHost Host = new(new RecordingAutomationTarget(), new InlineAutomationUi(),
                "PadForge.Tests.Pages." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown,
                automationPipeName: "PadForge.Tests.Pages.Automation." + Guid.NewGuid().ToString("N"));
            public readonly PluginManagerTests.FakeLauncher Launcher = new();
            public readonly DashboardViewModel Vm = new();
            public readonly PluginsController Controller;

            public Rig()
            {
                Directory.CreateDirectory(Dir);
                var manager = new PluginManager(Launcher, "pipe", commandFor: p => new PluginCommand(p, p, Dir), timer: false,
                    runCheck: work => work());
                Controller = new PluginsController(Host, Vm, Dispatcher.CurrentDispatcher, manager);
            }

            public string File(string name)
            {
                string path = Path.Combine(Dir, name);
                System.IO.File.WriteAllText(path, name);
                return path;
            }

            public void Dispose()
            {
                Controller.Dispose();
                Host.Dispose();
                try { Directory.Delete(Dir, true); } catch (IOException) { }
            }
        }

        private static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }

        [Fact]
        public void TheRows_TheStatusLineAndTheLog_FollowThePlugins()
        {
            using var rig = new Rig();
            var s = Strings.Instance;
            var actions = rig.Vm.PluginActions;
            Assert.NotNull(actions);
            Assert.False(rig.Vm.HasPluginRows);

            var approval = actions.Describe(rig.File("bridge.exe"));
            Assert.Equal(PluginFile.Sha256(approval.Path), approval.Sha256);
            Assert.Null(approval.Signer);
            Assert.True(actions.Add(approval));
            Pump();
            var row = Assert.Single(rig.Vm.PluginRows);
            Assert.Equal("bridge.exe", row.Name);
            Assert.Equal(s.Common_Stopped + ". " + s.Plugins_Row_NotSigned, row.State);
            Assert.True(row.IsOn);
            Assert.Equal(1, rig.Vm.PluginListRevision);
            Assert.Equal("bridge", rig.Vm.PluginRecords.Single().Identity);

            rig.Vm.EnablePlugins = true;
            Pump();
            Assert.StartsWith(s.Plugins_State_Waiting, row.State);
            Assert.Equal(s.Extras_Plugins_Status_None, rig.Vm.PluginsStatus);
            Assert.False(rig.Vm.IsPluginsRunning);

            rig.Controller.Manager.SetEngine(true);
            Pump();
            Assert.StartsWith(s.Common_Running, row.State);
            Assert.Equal(string.Format(s.Plugins_Status_Format, 1, 0), rig.Vm.PluginsStatus);
            Assert.True(rig.Vm.IsPluginsRunning);

            // The switch in the row reaches the plugin.
            row.IsOn = false;
            Pump();
            Assert.True(rig.Launcher.Last.Stopped);
            Assert.StartsWith(s.Common_Stopped, row.State);
            row.IsOn = true;
            Pump();
            Assert.StartsWith(s.Common_Running, row.State);

            // An exit, the pause in words, and Start after five failures.
            rig.Launcher.Last.Exit(4);
            Pump();
            Assert.StartsWith(string.Format(s.Plugins_State_Exited_Format, 4, 1), row.State);
            Assert.False(row.ShowStart);

            // The log of the plugin picked, none picked as the page opens.
            Assert.Null(rig.Vm.SelectedLogPlugin);
            Assert.Empty(rig.Vm.PluginLogLines);
            rig.Vm.SelectedLogPlugin = row;
            Assert.Contains(rig.Vm.PluginLogLines, l => l.EndsWith(s.Plugins_Log_Started, StringComparison.Ordinal));
            Assert.Contains(rig.Vm.PluginLogLines, l => l.EndsWith(string.Format(s.Plugins_Log_Exited_Format, 4), StringComparison.Ordinal));

            actions.Remove(row);
            Pump();
            Assert.Empty(rig.Vm.PluginRows);
            Assert.Null(rig.Vm.SelectedLogPlugin);
            Assert.Empty(rig.Vm.PluginRecords);
        }

        [Fact]
        public void AFloodOfLogLines_IsDrawnAFewTimes_AndTheLogKeepsItsLast200()
        {
            using var rig = new Rig();
            var actions = rig.Vm.PluginActions;
            actions.Add(actions.Describe(rig.File("noisy.exe")));
            rig.Vm.EnablePlugins = true;
            rig.Controller.Manager.SetEngine(true);
            Pump();
            var row = rig.Vm.PluginRows.Single();
            rig.Vm.SelectedLogPlugin = row;
            var grant = rig.Controller.Manager.AcceptToken(rig.Launcher.Starts[0].Variables[PluginManager.TokenVariable], rig.Launcher.Last.Id);
            int before = rig.Controller.Draws;
            // From a worker, as a session's lines arrive.
            var worker = new Thread(() => { for (int i = 0; i < 20000; i++) grant.Log("line " + i); });
            worker.Start();
            worker.Join();
            Pump();
            int draws = rig.Controller.Draws - before;
            Assert.InRange(draws, 1, 20);
            Assert.Equal(PluginManager.LogLines, rig.Vm.PluginLogLines.Count);
            Assert.EndsWith("line 19999", rig.Vm.PluginLogLines[^1]);
        }

        [Fact]
        public void TheBuiltInPlugins_FollowTheirSwitches_AndReplaysLinesShowTheOpenFile()
        {
            using var rig = new Rig();
            var s = Strings.Instance;
            rig.Vm.EnablePlugins = true;
            rig.Vm.TestControllerOn = true;
            Pump();
            Assert.True(rig.Controller.BuiltIns.TestControllerLive);
            Assert.Equal(string.Format(s.Plugins_Status_Format, 1, 1), rig.Vm.PluginsStatus);
            // Its declared settings are drawn from the same host as a
            // plugin's, and Controllers adds devices.
            Assert.Same(rig.Controller.TestHost, rig.Vm.TestControllerSettings);
            Assert.Contains(rig.Vm.TestControllerSettings.Rows, r => r is PluginSettingItem { Key: TestControllerPattern.KeyMove });
            rig.Controller.BuiltIns.TestSettings.Set(null, TestControllerPattern.KeyControllers, "2");
            Pump();
            Assert.Equal(string.Format(s.Plugins_Status_Format, 1, 2), rig.Vm.PluginsStatus);
            Assert.Equal(s.Plugins_TC_State_Moving, rig.Vm.TestControllerLine);
            rig.Controller.BuiltIns.TestSettings.Set(null, TestControllerPattern.KeyHand, "true");
            Pump();
            Assert.Equal(s.Plugins_TC_State_ByHand, rig.Vm.TestControllerLine);
            Assert.NotEmpty(rig.Vm.TestControllerValues);
            rig.Controller.BuiltIns.TestSettings.Set(null, TestControllerPattern.KeyControllers, "1");
            rig.Controller.BuiltIns.TestSettings.Set(null, TestControllerPattern.KeyHand, "false");

            rig.Vm.ReplayOn = true;
            Assert.False(rig.Vm.HasReplayFile);
            Assert.False(rig.Vm.ReplayPlayCommand.CanExecute(null));
            Assert.Null(rig.Vm.PluginActions.OpenRecording(PluginBuiltInTests.KeptRecording));
            Assert.Equal(string.Format(s.Plugins_Replay_File_Format, "two-pads.zip", "0:00"), rig.Vm.ReplayFileLine);
            Assert.Equal(string.Format(s.Plugins_Replay_Position_Format, "0:00", "0:00"), rig.Vm.ReplayPositionLine);
            Assert.True(rig.Vm.ReplayPlayCommand.CanExecute(null));
            Assert.Equal(string.Format(s.Plugins_Status_Format, 2, 3), rig.Vm.PluginsStatus);

            rig.Vm.EnablePlugins = false;
            Pump();
            Assert.False(rig.Controller.BuiltIns.TestControllerLive);
            Assert.Equal(0, rig.Controller.BuiltIns.ReplayDeviceCount);
            Assert.Equal(s.Common_Stopped, rig.Vm.PluginsStatus);
            Assert.False(rig.Vm.ReplayPlayCommand.CanExecute(null));
        }

        [Fact]
        public void ThePluginsSettings_AreLoadedAgain_AtAFactoryReset()
        {
            using var rig = new Rig();
            rig.Vm.PluginActions.Add(rig.Vm.PluginActions.Describe(rig.File("one.exe")));
            Pump();
            Assert.Single(rig.Vm.PluginRows);
            rig.Vm.LoadPluginSettings(null, false, null, false, false);
            Pump();
            Assert.Empty(rig.Vm.PluginRows);
            Assert.Empty(rig.Controller.Manager.Snapshot());
        }

        [Fact]
        public void TheTestControllersFileValues_AreCheckedAsTheSettingsLoad_AndTheCheckedListIsMarkedToBeSaved()
        {
            using var rig = new Rig();
            int revision = rig.Vm.PluginValuesRevision;
            // PadForge.xml holds Move Time at its default and a Rest Time of 3 s.
            rig.Vm.LoadPluginSettings(null, false, new[]
            {
                new PadForge.Services.PluginValueData { Key = TestControllerPattern.KeyMove, Value = "4" },
                new PadForge.Services.PluginValueData { Key = TestControllerPattern.KeyRest, Value = "3" },
            }, false, false);
            Pump();
            // The value at its default is not kept: the Test Controller keeps
            // Rest Time alone, and the list the settings save is that checked
            // one, marked to be saved on the settings' own timer.
            Assert.Equal(new[] { TestControllerPattern.KeyRest }, rig.Controller.BuiltIns.TestSettings.Save().Select(v => v.Key));
            Assert.Equal(new[] { TestControllerPattern.KeyRest }, rig.Vm.TestControllerValues.Select(v => v.Key));
            Assert.True(rig.Vm.PluginValuesRevision > revision);

            // A file with nothing to check moves nothing and marks nothing.
            revision = rig.Vm.PluginValuesRevision;
            rig.Vm.LoadPluginSettings(null, false, rig.Vm.TestControllerValues, false, false);
            Pump();
            Assert.Equal(revision, rig.Vm.PluginValuesRevision);
            Assert.Equal(new[] { TestControllerPattern.KeyRest }, rig.Controller.BuiltIns.TestSettings.Save().Select(v => v.Key));
        }

        [Fact]
        public void TheStatesInWords_NameEveryReason()
        {
            var s = Strings.Instance;
            Assert.Equal(s.Plugins_Refused_NoShell, PluginsController.RefusalText(PluginRefusal.NoShell, 0));
            Assert.Equal(s.Plugins_Refused_ShellElevated, PluginsController.RefusalText(PluginRefusal.ShellElevated, 0));
            Assert.Equal(s.Plugins_Refused_NeedsAdministrator, PluginsController.RefusalText(PluginRefusal.NeedsAdministrator, 740));
            Assert.Equal(s.Plugins_Refused_Missing, PluginsController.RefusalText(PluginRefusal.Missing, 0));
            Assert.Equal(s.Plugins_Refused_NoProgram, PluginsController.RefusalText(PluginRefusal.NoProgram, 0));
            Assert.Equal(string.Format(s.Plugins_Refused_Failed_Format, 5), PluginsController.RefusalText(PluginRefusal.Failed, 5));
            Assert.Equal(s.Plugins_Refused_Other, PluginsController.RefusalText(PluginRefusal.OffLimits, 0));
            Assert.Equal("1:05", PluginsController.Clock(65_400_000));
            Assert.Equal("0:00", PluginsController.Clock(-5));
        }

        [Fact]
        public void TheApproval_ShowsTheFullPath_TheSignerAndTheSha256()
        {
            var s = Strings.Instance;
            string detail = ExtrasPage.ApprovalDetail(new PluginApproval(@"C:\Plugins\bridge.exe", "Contoso Ltd", "AB12"));
            Assert.Equal(string.Join("\n",
                string.Format(s.Plugins_Confirm_File_Format, @"C:\Plugins\bridge.exe"),
                string.Format(s.Plugins_Confirm_Signer_Format, "Contoso Ltd"),
                string.Format(s.Plugins_Confirm_Sha_Format, "AB12")), detail);
            Assert.Contains(s.Plugins_Confirm_Unsigned, ExtrasPage.ApprovalDetail(new PluginApproval(@"C:\p.exe", null, "CD")));
            // The approval reads the signer through the one reader the request
            // card and Activity use, which finds a catalog's signer too:
            // notepad.exe is signed by a Windows catalog.
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            Assert.Equal("Microsoft Windows", AuthenticodeSigner.Of(notepad));
            string controller = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginsController.cs");
            Assert.Contains("new PluginApproval(full, AuthenticodeSigner.Of(full), sha)", controller);
            // The add's confirmation is PadForge's own, with Cancel first.
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("ConfirmDialog.Show(owner, s.Plugins_Confirm_Title, s.Plugins_Confirm_Message, s.Plugins_Confirm_Add,", code);
        }
        /// <summary>Add a Plugin… and Approve… read the file for their
        /// confirmation off the interface's thread, so a large file's hash
        /// holds no page, and a second press while it is read does
        /// nothing.</summary>
        [Fact]
        public async System.Threading.Tasks.Task TheConfirmationsFile_IsReadOffTheInterfaceThread()
        {
            var actions = new DescribeOnly();
            var approval = await ExtrasPage.DescribeOffTheInterfaceThread(actions, @"C:\Plugins\bridge.exe");
            Assert.Equal(@"C:\Plugins\bridge.exe", approval.Path);
            Assert.NotEqual(Environment.CurrentManagedThreadId, actions.Thread);

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("var approval = await DescribePluginAsync(actions, path);", code);
            Assert.Contains("var approval = await DescribePluginAsync(actions, row.Path);", code);
            Assert.DoesNotContain("actions.Describe(dialog.FileName)", code);
            Assert.DoesNotContain("actions.Describe(row.Path)", code);
            Assert.Equal(2, code.Split("if (_describingPlugin) return;").Length - 1);
        }

        private sealed class DescribeOnly : IPluginsPageActions
        {
            public int Thread;
            public PluginApproval Describe(string path)
            {
                Thread = Environment.CurrentManagedThreadId;
                return new PluginApproval(path, null, "00");
            }
            public bool Has(string path) => false;
            public bool Add(PluginApproval approved) => false;
            public void Remove(PluginRowItem row) { }
            public void SetEnabled(PluginRowItem row, bool on) { }
            public void Approve(PluginRowItem row, PluginApproval approved) { }
            public void StartAgain(PluginRowItem row) { }
            public string OpenRecording(string path) => null;
            public void Play() { }
            public void Stop() { }
            public string SaveKit(string parent) => null;
        }
    }
}
