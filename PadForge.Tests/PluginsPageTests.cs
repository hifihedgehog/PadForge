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

        /// <summary>Show in Folder starts Explorer through the desktop
        /// shell, as the user, with the plugin's file selected, as
        /// --settings-folder and the recording review's Show in Explorer do.
        /// It stands in the open card's bottom row before Remove and Reset
        /// All, the user's own.</summary>
        [Fact]
        public void ShowInFolder_StartsExplorerAsTheUser_WithThePluginsFileSelected()
        {
            var starts = new List<PadForge.Common.ShellLaunch.UserStart>();
            PadForge.Common.ShellLaunch.UserShellSeam = start => { starts.Add(start); return null; };
            try
            {
                using var rig = new Rig();
                string path = rig.File("bridge.exe");
                Assert.True(rig.Vm.PluginActions.Add(rig.Vm.PluginActions.Describe(path)));
                Pump();
                var row = rig.Vm.PluginRows.Single();
                row.ShowInFolderCommand.Execute(null);
                var started = Assert.Single(starts);
                Assert.Equal(PadForge.Common.ShellLaunch.Explorer, started.File);
                Assert.Equal("/select,\"" + path + "\"", started.Arguments);
            }
            finally
            {
                PadForge.Common.ShellLaunch.UserShellSeam = null;
            }

            string body = Body();
            int show = body.IndexOf("AutomationProperties.AutomationId=\"{Binding ShowInFolderId}\"", StringComparison.Ordinal);
            int remove = body.IndexOf("AutomationProperties.AutomationId=\"{Binding RemoveId}\"", show, StringComparison.Ordinal);
            int resetAll = body.IndexOf("AutomationProperties.AutomationId=\"{Binding ResetAllId}\"", StringComparison.Ordinal);
            Assert.True(show > 0 && show < remove && remove < resetAll, "Show in Folder is not before Remove and Reset All");
            string button = body.Substring(body.LastIndexOf("<Button", show, StringComparison.Ordinal), show - body.LastIndexOf("<Button", show, StringComparison.Ordinal));
            Assert.Contains("Command=\"{Binding ShowInFolderCommand}\"", button);
            Assert.Contains("common:Reach.Class=\"UsersOwn\"", button);
        }

        /// <summary>Trust New Versions From This Signer shows on a card only
        /// while the approved bytes' signing key is kept, names the signer,
        /// and reaches the plugin's switch both ways. Its reset turns it off.
        /// The row is the user's own.</summary>
        [Fact]
        public void TheTrustRow_ShowsForASignedPlugin_AndReachesItsSwitch()
        {
            using var rig = new Rig();
            var s = Strings.Instance;
            var actions = rig.Vm.PluginActions;
            // A file with no signature that verified: no key, no row.
            var approval = actions.Describe(rig.File("bridge.exe"));
            Assert.Null(approval.Key);
            Assert.True(actions.Add(approval));
            Pump();
            var row = rig.Vm.PluginRows.Single();
            Assert.False(row.CanTrust);

            // Approved for bytes whose signature verified.
            var run = rig.Controller.Manager.Snapshot().Single();
            rig.Controller.Manager.Approve(run, run.Sha256, "Example Studio", "ABCD");
            Pump();
            Assert.True(row.CanTrust);
            Assert.False(row.TrustSigner);
            Assert.Equal(string.Format(s.Plugins_Trust_Note_Format, "Example Studio"), row.TrustNote);
            Assert.EndsWith("Any other change asks you first.", s.Plugins_Trust_Note_Format);
            row.TrustSigner = true;
            Assert.True(run.TrustSigner);
            Assert.True(rig.Vm.PluginRecords.Single().TrustSigner);
            row.ResetTrustCommand.Execute(null);
            Assert.False(run.TrustSigner);
            Assert.False(row.TrustSigner);

            string body = Body();
            int at = body.IndexOf("x:Name=\"PluginTrustRow\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            string trustRow = body.Substring(body.LastIndexOf("<controls:SettingRow", at, StringComparison.Ordinal),
                body.IndexOf("</controls:SettingRow>", at, StringComparison.Ordinal) - body.LastIndexOf("<controls:SettingRow", at, StringComparison.Ordinal));
            Assert.Contains("common:Reach.Class=\"UsersOwn\"", trustRow);
            Assert.Contains("Visibility=\"{Binding CanTrust, Converter={StaticResource BoolToVisibilityConverter}}\"", trustRow);
            Assert.Contains("IsChecked=\"{Binding TrustSigner, Mode=TwoWay}\"", trustRow);
            Assert.Contains("Command=\"{Binding ResetTrustCommand}\"", trustRow);
            // It stands between the plugin's settings and Show in Folder.
            Assert.True(body.IndexOf("<views:PluginSettingsView DataContext=\"{Binding Settings}\"/>", StringComparison.Ordinal) < at
                && at < body.IndexOf("{Binding ShowInFolderId}", StringComparison.Ordinal));
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
            Assert.Null(rig.Vm.PluginActions.OpenRecording(rig.Vm.PluginActions.ReadRecording(PluginBuiltInTests.KeptRecording)));
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
            Assert.Equal(s.Plugins_Refused_RightsUnknown, PluginsController.RefusalText(PluginRefusal.RightsUnknown, 0));
            Assert.Equal(s.Plugins_Refused_StartedElevated, PluginsController.RefusalText(PluginRefusal.StartedElevated, 0));
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
            // The approval reads the signer with the one reader the request
            // card and Activity use, which finds a catalog's signer too
            // (notepad.exe is signed by a Windows catalog), on the handle
            // that was hashed.
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            Assert.Equal("Microsoft Windows", AuthenticodeSigner.Of(notepad));
            string controller = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginsController.cs");
            Assert.Contains("signer = held == null ? null : AuthenticodeSigner.OfHeld(full, held.SafeFileHandle);", controller);
            Assert.Contains("new PluginApproval(full, signer, sha, key)", controller);
            // The key is read on the handle that was hashed, with revocation
            // checked, and the test host asks no server: even a file that
            // carries a valid signature gives none here.
            Assert.Contains("using (var held = PluginFile.OpenHeld(full, out sha))", controller);
            Assert.Contains("key = PluginFile.KeyOf(full, held);", controller);
            // The key is the SHA-256 of the signing certificate's
            // SubjectPublicKeyInfo, read here from a file that carries a
            // signature and checked against .NET's own reading of it.
            string core = typeof(object).Assembly.Location;
            using (var signed = new FileStream(core, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var signature = AuthenticodeSigner.SignedHeld(core, signed.SafeFileHandle);
                Assert.NotNull(signature);
#pragma warning disable SYSLIB0057
                using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(core));
#pragma warning restore SYSLIB0057
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())), signature.Key);
                Assert.Equal(certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), signature.Signer);
                // The check with revocation asks a server, which the test
                // host never does: null here even for this file.
                Assert.Null(AuthenticodeSigner.VerifiedHeld(core, signed.SafeFileHandle));
            }
            // A file with no signature has none.
            string plain = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Unsigned." + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(plain, "not signed");
            try
            {
                using var unsigned = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.Null(AuthenticodeSigner.SignedHeld(plain, unsigned.SafeFileHandle));
            }
            finally { File.Delete(plain); }
            // The add's confirmation is PadForge's own, with Cancel first.
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("ConfirmDialog.Show(owner, s.Plugins_Confirm_Title, s.Plugins_Confirm_Message, s.Plugins_Confirm_Add,", code);
        }
        /// <summary>A trusted update needs revocation actually checked:
        /// WinVerifyTrust passes under Windows' default policy when a
        /// revocation server cannot be reached, so a chain whose revocation
        /// status is unknown, or offline or stale (CERT_TRUST_STATUS: 0x40,
        /// 0x01000000), is not taken and the update asks. The test host
        /// verifies nothing over the network, so the rule is tested on fake
        /// chain statuses, and the revocation-checked read is pinned to go
        /// through it.</summary>
        [Fact]
        public void ATrustedUpdate_NeedsRevocationActuallyChecked()
        {
            const uint Unknown = 0x00000040, Offline = 0x01000000;
            Assert.NotNull(AuthenticodeSigner.Signature(0, "Example Studio", "AB", true, 0));
            Assert.Null(AuthenticodeSigner.Signature(0, "Example Studio", "AB", true, Unknown));
            Assert.Null(AuthenticodeSigner.Signature(0, "Example Studio", "AB", true, Offline));
            Assert.Null(AuthenticodeSigner.Signature(0, "Example Studio", "AB", true, Offline | Unknown));
            // Read without revocation, as the confirmation reads the key, the
            // chain's revocation status is not asked about.
            Assert.NotNull(AuthenticodeSigner.Signature(0, "Example Studio", "AB", false, Offline | Unknown));
            Assert.Null(AuthenticodeSigner.Signature(unchecked((int)0x800B0100), "Example Studio", "AB", true, 0));
            Assert.Null(AuthenticodeSigner.Signature(0, null, "AB", true, 0));
            Assert.Null(AuthenticodeSigner.Signature(0, "Example Studio", null, true, 0));
            string engine = ExtrasPageTests.RepoText("PadForge.Engine", "Common", "AuthenticodeSigner.cs");
            Assert.Contains("return Signature(result, signer, key, revocation, chainError);", engine);
        }

        /// <summary>The confirmation's signer is read on the handle that was
        /// hashed: other bytes of the same length and write time at a path
        /// read before carry no signer they do not have.</summary>
        [Fact]
        public void TheConfirmationsSigner_IsTheHashedBytesSigner()
        {
            using var rig = new Rig();
            string host = Path.Combine(AppContext.BaseDirectory, "testhost.exe");
            string path = Path.Combine(rig.Dir, "bridge.exe");
            File.Copy(host, path);
            var written = File.GetLastWriteTimeUtc(path);
            var signed = rig.Controller.Describe(path);
            Assert.Equal("Microsoft Corporation", signed.Signer);
            // Other bytes, the same length and the same write time.
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, written);
            var changed = rig.Controller.Describe(path);
            Assert.NotEqual(signed.Sha256, changed.Sha256);
            Assert.Null(changed.Signer);
        }

        /// <summary>The card's line ends each state as its language does: a
        /// state that is whole sentences in its own string gets no period
        /// after it, and one that is a few words takes the row's own sentence
        /// end.</summary>
        [Fact]
        public void TheCardsLine_EndsEachStateAsItsLanguageDoes()
        {
            using var rig = new Rig();
            var s = Strings.Instance;
            var run = rig.Controller.Manager.Add(rig.File("pedals.exe"), PluginFile.Sha256(Path.Combine(rig.Dir, "pedals.exe")));
            rig.Controller.Manager.SetPluginsOn(true);
            Assert.Equal(PluginStatus.Waiting, run.Status);
            Assert.StartsWith(string.Format(Strings.Get("Plugins_Row_State_Format"), s.Plugins_State_Waiting) + " ", rig.Controller.StateLine(run));
            rig.Controller.Manager.SetEngine(true);
            rig.Launcher.Last.Exit(3);
            Assert.Equal(PluginStatus.Restarting, run.Status);
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ja");
                string line = rig.Controller.StateLine(run);
                Assert.DoesNotContain("。.", line);
                Assert.StartsWith(string.Format(s.Plugins_State_Exited_Format, 3, 1) + " ", line);
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = before; }
        }

        /// <summary>Open a Recording… reads the recording off the interface's
        /// thread, as the confirmation reads a plugin's file, and puts it in
        /// place on the page's own.</summary>
        [Fact]
        public async System.Threading.Tasks.Task TheRecording_IsReadOffTheInterfaceThread()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("var read = await ReadRecordingAsync(actions, dialog.FileName);", code);
            Assert.DoesNotContain("actions.OpenRecording(dialog.FileName)", code);
            Assert.Contains("if (_readingRecording) return;", code);
            var actions = new DescribeOnly();
            var read = await ExtrasPage.ReadRecordingOffTheInterfaceThread(actions, @"C:\Recordings\pads.zip");
            Assert.Equal("unreadable", read.Error);
            Assert.NotEqual(Environment.CurrentManagedThreadId, actions.Thread);
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
            public void SetTrust(PluginRowItem row, bool on) { }
            public RecordingRead ReadRecording(string path)
            {
                Thread = Environment.CurrentManagedThreadId;
                return new RecordingRead(null, "unreadable");
            }
            public string OpenRecording(RecordingRead read) => read?.Error;
            public void Play() { }
            public void Stop() { }
            public System.Threading.Tasks.Task<string> SaveKitAsync(string parent) => System.Threading.Tasks.Task.FromResult<string>(null);
        }
    }
}
